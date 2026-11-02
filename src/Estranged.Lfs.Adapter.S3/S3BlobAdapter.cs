using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Estranged.Lfs.Data;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Estranged.Lfs.Adapter.S3
{
    public sealed class S3BlobAdapter : IBlobAdapter
    {
        // The SDK otherwise special-cases us-east-1 presigns to SigV2 even
        // when the client requests SigV4. STS credentials require SigV4.
        static S3BlobAdapter() => Amazon.AWSConfigsS3.UseSignatureVersion4 = true;

        public static readonly TransientRetryPolicy DefaultRetryPolicy = new()
        {
            MaxAttempts = 3,
            InitialDelay = TimeSpan.FromMilliseconds(200),
            MaxDelay = TimeSpan.FromSeconds(1),
            AttemptTimeout = TimeSpan.FromSeconds(10)
        };

        private readonly IAmazonS3 client;
        private readonly IS3BlobAdapterConfig config;
        private readonly bool requireSha256;
        private readonly IAmazonS3 sharedClient;
        private readonly ISharedObjectPopulator populator;
        private readonly TransientRetryPolicy retryPolicy;

        public S3BlobAdapter(IAmazonS3 client, IS3BlobAdapterConfig config, bool requireSha256 = false, IAmazonS3 sharedClient = null,
            ISharedObjectPopulator populator = null, TransientRetryPolicy retryPolicy = null)
        {
            this.client = client;
            this.config = config;
            this.requireSha256 = requireSha256;
            this.sharedClient = sharedClient ?? client;
            this.populator = populator;
            this.retryPolicy = retryPolicy ?? DefaultRetryPolicy;
        }

        /// <summary>
        /// Throttling, server errors and connection failures from the object
        /// store. Not found, access denied and other client errors are answers.
        /// </summary>
        public static bool IsTransientStorageFailure(Exception exception) => exception switch
        {
            AmazonServiceException service => service.StatusCode == 0 ||
                                              TransientRetry.IsTransientStatus((int)service.StatusCode) ||
                                              service.InnerException != null && TransientRetry.IsTransientNetworkFailure(service.InnerException),
            AmazonClientException client => client.InnerException != null && TransientRetry.IsTransientNetworkFailure(client.InnerException),
            _ => TransientRetry.IsTransientNetworkFailure(exception),
        };

        private static bool IsSha256Oid(string oid) =>
            oid != null && oid.Length == 64 && oid.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f');

        // The canonical key is content addressed, so only a SHA-256 digest may
        // name it; anything else could address a key outside the shared prefix.
        private string SharedKeyFor(string oid)
        {
            if (string.IsNullOrWhiteSpace(config.SharedKeyPrefix) || !IsSha256Oid(oid))
                return null;
            string prefix = config.SharedKeyPrefix.EndsWith("/", StringComparison.Ordinal)
                ? config.SharedKeyPrefix
                : config.SharedKeyPrefix + "/";
            string targetKey = config.KeyPrefix + oid;
            string sharedKey = prefix + oid;
            return string.Equals(targetKey, sharedKey, StringComparison.Ordinal) ? null : sharedKey;
        }

        private static async Task<GetObjectMetadataResponse> TryGetObjectMetadataAsync(IAmazonS3 s3, string bucket, string key, CancellationToken token)
        {
            try
            {
                return await s3.GetObjectMetadataAsync(bucket, key, token).ConfigureAwait(false);
            }
            catch (AmazonS3Exception)
            {
                return null;
            }
        }

        // Populating the canonical object copies the whole object, so it runs
        // off the request path; the populator drops work when it is saturated.
        private void QueueSharedObject(string oid)
        {
            string sharedKey = SharedKeyFor(oid);
            if (sharedKey != null)
                populator?.TryEnqueue(config.Bucket, config.KeyPrefix + oid, sharedKey);
        }

        /// <summary>
        /// Presigned URLs must carry the scheme of the endpoint they are signed
        /// for; a plain-HTTP object store rejects an HTTPS presign outright.
        /// </summary>
        public static Protocol ProtocolFor(string serviceUrl) =>
            serviceUrl != null && serviceUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? Protocol.HTTP : Protocol.HTTPS;

        public Uri MakePreSignedUrl(string oid, HttpVerb verb, string mimeType)
        {
            var request = new GetPreSignedUrlRequest
            {
                Verb = verb,
                BucketName = config.Bucket,
                Key = config.KeyPrefix + oid,
                Protocol = ProtocolFor(client.Config?.ServiceURL),
                ContentType = mimeType,
                Expires = DateTime.UtcNow + config.Expiry
            };

            if (requireSha256 && verb == HttpVerb.PUT)
                request.Headers["x-amz-checksum-sha256"] = Convert.ToBase64String(Convert.FromHexString(oid));
            return new Uri(client.GetPreSignedURL(request));
        }

        public async Task<SignedBlob> UriForDownload(string oid, CancellationToken token)
        {
            GetObjectMetadataResponse metadataResponse;
            try
            {
                metadataResponse = await TransientRetry.ExecuteAsync("object storage", retryPolicy, IsTransientStorageFailure,
                    attempt => client.GetObjectMetadataAsync(config.Bucket, config.KeyPrefix + oid, attempt), token).ConfigureAwait(false);
            }
            catch (AmazonS3Exception ex)
            {
                return new SignedBlob
                {
                    ErrorCode = (int)ex.StatusCode,
                    ErrorMessage = ex.Message
                };
            }

            QueueSharedObject(oid);

            return new SignedBlob
            {
                Uri = MakePreSignedUrl(oid, HttpVerb.GET, null),
                Size = metadataResponse.ContentLength,
                Expiry = config.Expiry
            };
        }

        public async Task<SignedBlob> UriForUpload(string oid, long size, CancellationToken token)
        {
            if (config.DeduplicateUploads)
            {
                var deduplicated = await TryDeduplicateAsync(oid, size, token).ConfigureAwait(false);
                if (deduplicated != null)
                    return deduplicated;
            }

            var headers = new Dictionary<string, string> { { "Content-Type", BlobConstants.UploadMimeType } };
            if (requireSha256) headers["x-amz-checksum-sha256"] = Convert.ToBase64String(Convert.FromHexString(oid));
            return new SignedBlob
            {
                Uri = MakePreSignedUrl(oid, HttpVerb.PUT, BlobConstants.UploadMimeType),
                Expiry = config.Expiry,
                Headers = headers
            };
        }

        /// <summary>
        /// Skips the upload when the repository already holds the object, or
        /// when it can be copied from the canonical shared key. Deduplication is
        /// bounded by <see cref="IS3BlobAdapterConfig.DeduplicationTimeout"/> and
        /// any storage failure or timeout falls back to a presigned upload.
        /// </summary>
        private async Task<SignedBlob> TryDeduplicateAsync(string oid, long size, CancellationToken token)
        {
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token);
            bounded.CancelAfter(config.DeduplicationTimeout);
            try
            {
                string repoKey = config.KeyPrefix + oid;
                var existing = await TryGetObjectMetadataAsync(client, config.Bucket, repoKey, bounded.Token).ConfigureAwait(false);
                if (existing != null && (size <= 0 || existing.ContentLength == size))
                {
                    QueueSharedObject(oid);
                    return new SignedBlob
                    {
                        AlreadyExists = true,
                        Size = existing.ContentLength,
                        Expiry = config.Expiry
                    };
                }

                string sharedKey = SharedKeyFor(oid);
                if (sharedKey == null || sharedClient == null)
                    return null;

                var sharedMeta = await TryGetObjectMetadataAsync(sharedClient, config.Bucket, sharedKey, bounded.Token).ConfigureAwait(false);
                if (sharedMeta == null || size > 0 && sharedMeta.ContentLength != size)
                    return null;

                await sharedClient.CopyObjectAsync(new CopyObjectRequest
                {
                    SourceBucket = config.Bucket,
                    SourceKey = sharedKey,
                    DestinationBucket = config.Bucket,
                    DestinationKey = repoKey
                }, bounded.Token).ConfigureAwait(false);
                return new SignedBlob
                {
                    AlreadyExists = true,
                    Size = sharedMeta.ContentLength,
                    Expiry = config.Expiry
                };
            }
            catch (Exception exception) when (!token.IsCancellationRequested &&
                                              (exception is OperationCanceledException || exception is AmazonServiceException || IsTransientStorageFailure(exception)))
            {
                return null;
            }
        }
    }
}

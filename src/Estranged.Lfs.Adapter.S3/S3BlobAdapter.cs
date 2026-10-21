using Amazon.S3;
using Amazon.S3.Model;
using Estranged.Lfs.Data;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Estranged.Lfs.Adapter.S3
{
    public sealed class S3BlobAdapter : IBlobAdapter
    {
        // The SDK otherwise special-cases us-east-1 presigns to SigV2 even
        // when the client requests SigV4. STS credentials require SigV4.
        static S3BlobAdapter() => Amazon.AWSConfigsS3.UseSignatureVersion4 = true;

        private readonly IAmazonS3 client;
        private readonly IS3BlobAdapterConfig config;
        private readonly bool requireSha256;
        private readonly IAmazonS3 sharedClient;

        public S3BlobAdapter(IAmazonS3 client, IS3BlobAdapterConfig config, bool requireSha256 = false, IAmazonS3 sharedClient = null)
        {
            this.client = client;
            this.config = config;
            this.requireSha256 = requireSha256;
            this.sharedClient = sharedClient ?? client;
        }

        private string SharedKeyFor(string oid)
        {
            if (string.IsNullOrWhiteSpace(config.SharedKeyPrefix))
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

        private async Task TryEnsureSharedObjectAsync(string oid, CancellationToken token)
        {
            string sharedKey = SharedKeyFor(oid);
            if (sharedKey == null || sharedClient == null)
                return;

            try
            {
                if (await TryGetObjectMetadataAsync(sharedClient, config.Bucket, sharedKey, token).ConfigureAwait(false) != null)
                    return;

                await sharedClient.CopyObjectAsync(new CopyObjectRequest
                {
                    SourceBucket = config.Bucket,
                    SourceKey = config.KeyPrefix + oid,
                    DestinationBucket = config.Bucket,
                    DestinationKey = sharedKey
                }, token).ConfigureAwait(false);
            }
            catch (AmazonS3Exception)
            {
                // Best-effort populate of canonical content-addressed object.
            }
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
                metadataResponse = await client.GetObjectMetadataAsync(config.Bucket, config.KeyPrefix + oid, token).ConfigureAwait(false);
            }
            catch (AmazonS3Exception ex)
            {
                return new SignedBlob
                {
                    ErrorCode = (int)ex.StatusCode,
                    ErrorMessage = ex.Message
                };
            }

            await TryEnsureSharedObjectAsync(oid, token).ConfigureAwait(false);

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
                string repoKey = config.KeyPrefix + oid;
                var existing = await TryGetObjectMetadataAsync(client, config.Bucket, repoKey, token).ConfigureAwait(false);
                if (existing != null && (size <= 0 || existing.ContentLength == size))
                {
                    await TryEnsureSharedObjectAsync(oid, token).ConfigureAwait(false);
                    return new SignedBlob
                    {
                        AlreadyExists = true,
                        Size = existing.ContentLength,
                        Expiry = config.Expiry
                    };
                }

                string sharedKey = SharedKeyFor(oid);
                if (sharedKey != null && sharedClient != null)
                {
                    var sharedMeta = await TryGetObjectMetadataAsync(sharedClient, config.Bucket, sharedKey, token).ConfigureAwait(false);
                    if (sharedMeta != null && (size <= 0 || sharedMeta.ContentLength == size))
                    {
                        try
                        {
                            await sharedClient.CopyObjectAsync(new CopyObjectRequest
                            {
                                SourceBucket = config.Bucket,
                                SourceKey = sharedKey,
                                DestinationBucket = config.Bucket,
                                DestinationKey = repoKey
                            }, token).ConfigureAwait(false);
                            return new SignedBlob
                            {
                                AlreadyExists = true,
                                Size = sharedMeta.ContentLength,
                                Expiry = config.Expiry
                            };
                        }
                        catch (AmazonS3Exception)
                        {
                            // Fall back to regular presigned upload if cross-prefix copy is not permitted.
                        }
                    }
                }
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
    }
}

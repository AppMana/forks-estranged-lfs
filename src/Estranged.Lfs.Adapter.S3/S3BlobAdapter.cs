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
        private readonly IAmazonS3 client;
        private readonly IS3BlobAdapterConfig config;

        public S3BlobAdapter(IAmazonS3 client, IS3BlobAdapterConfig config)
        {
            this.client = client;
            this.config = config;
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

            return new SignedBlob
            {
                Uri = MakePreSignedUrl(oid, HttpVerb.GET, null),
                Size = metadataResponse.ContentLength,
                Expiry = config.Expiry
            };
        }

        public Task<SignedBlob> UriForUpload(string oid, long size, CancellationToken token)
        {
            return Task.FromResult(new SignedBlob
            {
                Uri = MakePreSignedUrl(oid, HttpVerb.PUT, BlobConstants.UploadMimeType),
                Expiry = config.Expiry,
                Headers = new Dictionary<string, string>
                {
                    {"Content-Type", BlobConstants.UploadMimeType}
                }
            });
        }
    }
}

using Amazon.Runtime;
using Amazon.S3;
using Estranged.Lfs.Data;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Estranged.Lfs.Adapter.S3
{
    // UUID routes never use the gateway's legacy, bucket-wide credentials.
    // Exchange the authenticated repository token lazily: MVC constructs the
    // controller before its authentication action filter runs.
    public sealed class RepositoryScopedS3BlobAdapter : IBlobAdapter, IDisposable
    {
        private readonly RepositoryGrant grant;
        private readonly string repositoryId;
        private readonly string bucket;
        private readonly string endpoint;
        private readonly string stsEndpoint;
        private readonly HttpClient http;
        private readonly SemaphoreSlim mutex = new(1, 1);
        private IAmazonS3 client;
        private S3BlobAdapter adapter;

        public RepositoryScopedS3BlobAdapter(RepositoryGrant grant, string repositoryId,
            string bucket, string endpoint, string stsEndpoint, HttpClient http)
        {
            this.grant = grant;
            this.repositoryId = repositoryId;
            this.bucket = bucket;
            this.endpoint = endpoint;
            this.stsEndpoint = stsEndpoint;
            this.http = http;
        }

        private static void ValidateOid(string oid)
        {
            if (oid == null || oid.Length != 64 || oid.Any(c => !(c >= '0' && c <= '9' || c >= 'a' && c <= 'f')))
                throw new ArgumentException("LFS OID must be a lowercase SHA-256 digest.", nameof(oid));
        }

        private async Task<S3BlobAdapter> Adapter(CancellationToken token)
        {
            await mutex.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (adapter != null) return adapter;
                if (!RepositoryGrant.IsRepositoryId(repositoryId) || grant.RepositoryId != repositoryId ||
                    !RepositoryGrant.IsStoragePrefix(grant.StoragePrefix) || string.IsNullOrWhiteSpace(grant.AccessToken))
                    throw new UnauthorizedAccessException("No authenticated grant for this repository.");
                if (string.IsNullOrWhiteSpace(stsEndpoint) || string.IsNullOrWhiteSpace(endpoint))
                    throw new InvalidOperationException("Repository storage requires S3 and STS endpoints.");

                using var form = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["Action"] = "AssumeRoleWithWebIdentity",
                    ["Version"] = "2011-06-15",
                    ["RoleArn"] = "arn:aws:iam:::role/sts-claim-based",
                    ["RoleSessionName"] = "lfs-" + repositoryId,
                    ["WebIdentityToken"] = grant.AccessToken,
                    ["DurationSeconds"] = "900",
                });
                using var response = await http.PostAsync(stsEndpoint, form, token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    throw new UnauthorizedAccessException($"Repository storage exchange failed ({(int)response.StatusCode}).");
                var document = XDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
                string Text(string name) => document.Descendants().Single(x => x.Name.LocalName == name).Value;
                var expiry = DateTimeOffset.Parse(Text("Expiration"), CultureInfo.InvariantCulture);
                var lifetime = expiry - DateTimeOffset.UtcNow - TimeSpan.FromSeconds(10);
                if (lifetime <= TimeSpan.Zero) throw new UnauthorizedAccessException("Storage credentials already expired.");
                client = new AmazonS3Client(new SessionAWSCredentials(Text("AccessKeyId"), Text("SecretAccessKey"), Text("SessionToken")),
                    new AmazonS3Config { ServiceURL = endpoint, ForcePathStyle = true });
                adapter = new S3BlobAdapter(client, new S3BlobAdapterConfig
                {
                    Bucket = bucket,
                    KeyPrefix = grant.StoragePrefix,
                    Expiry = lifetime < TimeSpan.FromMinutes(5) ? lifetime : TimeSpan.FromMinutes(5),
                });
                return adapter;
            }
            finally { mutex.Release(); }
        }

        public async Task<SignedBlob> UriForDownload(string oid, CancellationToken token)
        {
            ValidateOid(oid);
            return await (await Adapter(token).ConfigureAwait(false)).UriForDownload(oid, token).ConfigureAwait(false);
        }

        public async Task<SignedBlob> UriForUpload(string oid, long size, CancellationToken token)
        {
            ValidateOid(oid);
            if (size < 0) throw new ArgumentOutOfRangeException(nameof(size));
            return await (await Adapter(token).ConfigureAwait(false)).UriForUpload(oid, size, token).ConfigureAwait(false);
        }

        public void Dispose() { client?.Dispose(); mutex.Dispose(); }
    }
}

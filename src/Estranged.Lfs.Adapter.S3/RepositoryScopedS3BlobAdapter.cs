using Amazon.Runtime;
using Amazon.S3;
using Estranged.Lfs.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Runtime.ExceptionServices;
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
        private readonly IAmazonS3 sharedClient;
        private readonly string sharedKeyPrefix;
        private readonly bool deduplicateUploads;
        private readonly ISharedObjectPopulator populator;
        private readonly TransientRetryPolicy retryPolicy;
        private ExceptionDispatchInfo exchangeFailure;
        private readonly ILogger logger;
        private readonly Func<AWSCredentials, AmazonS3Config, IAmazonS3> clientFactory;
        private readonly SemaphoreSlim mutex = new(1, 1);
        private IAmazonS3 client;
        private readonly List<IAmazonS3> retired = new();
        private S3BlobAdapter adapter;

        public RepositoryScopedS3BlobAdapter(RepositoryGrant grant, string repositoryId,
            string bucket, string endpoint, string stsEndpoint, HttpClient http,
            IAmazonS3 sharedClient = null, string sharedKeyPrefix = null, bool deduplicateUploads = false,
            ISharedObjectPopulator populator = null, TransientRetryPolicy retryPolicy = null,
            ILogger logger = null, Func<AWSCredentials, AmazonS3Config, IAmazonS3> clientFactory = null)
        {
            this.logger = logger ?? NullLogger.Instance;
            this.clientFactory = clientFactory ?? ((credentials, config) => new AmazonS3Client(credentials, config));
            this.grant = grant;
            this.repositoryId = repositoryId;
            this.bucket = bucket;
            this.endpoint = endpoint;
            this.stsEndpoint = stsEndpoint;
            this.http = http;
            this.sharedClient = sharedClient;
            this.sharedKeyPrefix = sharedKeyPrefix;
            this.deduplicateUploads = deduplicateUploads;
            this.populator = populator;
            this.retryPolicy = retryPolicy ?? DefaultRetryPolicy;
        }

        private static void ValidateOid(string oid)
        {
            if (oid == null || oid.Length != 64 || oid.Any(c => !(c >= '0' && c <= '9' || c >= 'a' && c <= 'f')))
                throw new ArgumentException("LFS OID must be a lowercase SHA-256 digest.", nameof(oid));
        }

        public static readonly TransientRetryPolicy DefaultRetryPolicy = new()
        {
            MaxAttempts = 3,
            InitialDelay = TimeSpan.FromMilliseconds(200),
            MaxDelay = TimeSpan.FromSeconds(1),
            AttemptTimeout = TimeSpan.FromSeconds(5)
        };

        private sealed record StorageCredentials(string AccessKeyId, string SecretAccessKey, string SessionToken, DateTimeOffset Expiration);

        private async Task<StorageCredentials> ExchangeAsync(CancellationToken token)
        {
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
            int status = (int)response.StatusCode;
            if (TransientRetry.IsTransientStatus(status))
                throw new HttpRequestException($"Repository storage exchange failed ({status}).", null, response.StatusCode);
            if (!response.IsSuccessStatusCode)
                throw new UnauthorizedAccessException($"Repository storage exchange failed ({status}).");
            var document = XDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
            string Text(string name) => document.Descendants().Single(x => x.Name.LocalName == name).Value;
            return new StorageCredentials(Text("AccessKeyId"), Text("SecretAccessKey"), Text("SessionToken"),
                DateTimeOffset.Parse(Text("Expiration"), CultureInfo.InvariantCulture));
        }

        private async Task<S3BlobAdapter> Adapter(CancellationToken token)
        {
            await mutex.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (adapter != null) return adapter;
                // A batch asks for every object through this adapter; once the
                // exchange has failed definitively or exhausted its retries, the
                // remaining objects share that outcome instead of repeating it.
                exchangeFailure?.Throw();
                try
                {
                    if (!RepositoryGrant.IsRepositoryId(repositoryId) || grant.RepositoryId != repositoryId ||
                        !RepositoryGrant.IsStoragePrefix(grant.StoragePrefix) || string.IsNullOrWhiteSpace(grant.AccessToken))
                        throw new UnauthorizedAccessException("No authenticated grant for this repository.");
                    if (string.IsNullOrWhiteSpace(stsEndpoint) || string.IsNullOrWhiteSpace(endpoint))
                        throw new InvalidOperationException("Repository storage requires S3 and STS endpoints.");

                    var credentials = await TransientRetry.ExecuteAsync("repository storage STS", retryPolicy,
                        TransientRetry.IsTransientNetworkFailure, ExchangeAsync, token).ConfigureAwait(false);
                    var lifetime = credentials.Expiration - DateTimeOffset.UtcNow - TimeSpan.FromSeconds(10);
                    if (lifetime <= TimeSpan.Zero) throw new UnauthorizedAccessException("Storage credentials already expired.");
                    client = clientFactory(new SessionAWSCredentials(credentials.AccessKeyId, credentials.SecretAccessKey, credentials.SessionToken),
                        S3ClientDefaults.Apply(new AmazonS3Config { ServiceURL = endpoint, ForcePathStyle = true, SignatureVersion = "4", AuthenticationRegion = "us-east-1" }));
                    adapter = new S3BlobAdapter(client, new S3BlobAdapterConfig
                    {
                        Bucket = bucket,
                        KeyPrefix = grant.StoragePrefix,
                        SharedKeyPrefix = sharedKeyPrefix,
                        DeduplicateUploads = deduplicateUploads,
                        Expiry = lifetime < TimeSpan.FromMinutes(5) ? lifetime : TimeSpan.FromMinutes(5),
                    }, requireSha256: true, sharedClient: sharedClient, populator: populator, forbiddenIsTransient: true);
                    return adapter;
                }
                catch (Exception exception) when (exception is UnauthorizedAccessException || exception is TransientUpstreamException)
                {
                    exchangeFailure = ExceptionDispatchInfo.Capture(exception);
                    throw;
                }
            }
            finally { mutex.Release(); }
        }

        // SeaweedFS intermittently answers 403 to requests signed with the
        // scoped session inside the granted prefix. Retry with a fresh STS
        // exchange; when that keeps failing, fail the batch retryably, because
        // git-lfs never retries a per-object error.
        private async Task<SignedBlob> RetryForbiddenAsync(string oid, Func<S3BlobAdapter, CancellationToken, Task<SignedBlob>> operation, CancellationToken token)
        {
            var policy = new TransientRetryPolicy
            {
                MaxAttempts = retryPolicy.MaxAttempts,
                InitialDelay = retryPolicy.InitialDelay,
                MaxDelay = retryPolicy.MaxDelay
            };
            return await TransientRetry.ExecuteAsync("repository storage", policy, exception => exception is StorageForbiddenException, async attempt =>
            {
                var current = await Adapter(attempt).ConfigureAwait(false);
                try
                {
                    return await operation(current, attempt).ConfigureAwait(false);
                }
                catch (StorageForbiddenException exception)
                {
                    logger.LogWarning(exception, "Repository storage answered 403 for repository {RepositoryId} object {Oid}; retrying with fresh credentials",
                        repositoryId, oid);
                    await DiscardAsync(current).ConfigureAwait(false);
                    throw;
                }
            }, token).ConfigureAwait(false);
        }

        // Concurrent lookups may still hold the rejected client, so it is
        // disposed with the adapter rather than immediately.
        private async Task DiscardAsync(S3BlobAdapter rejected)
        {
            await mutex.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!ReferenceEquals(adapter, rejected)) return;
                adapter = null;
                retired.Add(client);
                client = null;
            }
            finally { mutex.Release(); }
        }

        public Task<SignedBlob> UriForDownload(string oid, CancellationToken token)
        {
            ValidateOid(oid);
            return RetryForbiddenAsync(oid, (current, attempt) => current.UriForDownload(oid, attempt), token);
        }

        public Task<SignedBlob> UriForUpload(string oid, long size, CancellationToken token)
        {
            ValidateOid(oid);
            if (size < 0) throw new ArgumentOutOfRangeException(nameof(size));
            return RetryForbiddenAsync(oid, (current, attempt) => current.UriForUpload(oid, size, attempt), token);
        }

        public void Dispose()
        {
            client?.Dispose();
            foreach (var old in retired) old?.Dispose();
            mutex.Dispose();
        }
    }
}

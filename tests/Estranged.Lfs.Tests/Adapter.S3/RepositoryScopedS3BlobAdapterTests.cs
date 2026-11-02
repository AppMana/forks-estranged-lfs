using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Estranged.Lfs.Adapter.S3;
using Estranged.Lfs.Data;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Xunit;

namespace Estranged.Lfs.Tests.Adapter.S3
{
    public class RepositoryScopedS3BlobAdapterTests
    {
        const string Repo = "6dd8f38e-dab5-40b0-b6b7-91c412a5186b";
        const string Oid = "d53de494a038b6a8ede0aea08c38bde00244b155924bf4c463d1de208faecee8";
        sealed class StsHandler : HttpMessageHandler
        {
            public int Calls;
            public HttpStatusCode Status = HttpStatusCode.OK;
            public readonly Queue<object> Script = new();
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                Calls++;
                var status = Status;
                if (Script.TryDequeue(out var step))
                {
                    if (step is Exception failure) throw failure;
                    status = (HttpStatusCode)step;
                }
                Assert.Equal("http://sts.example/", request.RequestUri.ToString());
                var form = HttpUtility.ParseQueryString(await request.Content.ReadAsStringAsync(token));
                Assert.Equal("repository-token", form["WebIdentityToken"]);
                Assert.Equal("arn:aws:iam:::role/sts-claim-based", form["RoleArn"]);
                return new HttpResponseMessage(status) { Content = new StringContent(
                    "<Response><Credentials><AccessKeyId>scoped-key</AccessKeyId><SecretAccessKey>scoped-secret</SecretAccessKey>" +
                    "<SessionToken>scoped-session</SessionToken><Expiration>" + DateTimeOffset.UtcNow.AddMinutes(4).ToString("O") +
                    "</Expiration></Credentials></Response>") };
            }
        }
        static readonly TransientRetryPolicy Fast = new() { MaxAttempts = 3, InitialDelay = TimeSpan.Zero, MaxDelay = TimeSpan.Zero };
        static RepositoryGrant Grant() => new() { RepositoryId = Repo, StoragePrefix = "AppMana/lbxx/", AccessToken = "repository-token" };

        [Fact]
        public async Task UsesScopedStsCredentialsAndTheBoundExistingStoragePrefix()
        {
            var handler = new StsHandler();
            using var http = new HttpClient(handler);
            var grant = new RepositoryGrant();
            using var adapter = new RepositoryScopedS3BlobAdapter(grant, Repo, "lfs", "https://s3.example", "http://sts.example", http);
            Assert.Equal(0, handler.Calls); // Controller construction precedes auth.
            grant.RepositoryId = Repo; grant.StoragePrefix = "AppMana/lbxx/"; grant.AccessToken = "repository-token";
            var signed = await adapter.UriForUpload(Oid, 123, CancellationToken.None);
            Assert.Equal("/lfs/AppMana/lbxx/" + Oid, signed.Uri.AbsolutePath);
            var query = HttpUtility.ParseQueryString(signed.Uri.Query);
            Assert.Equal("AWS4-HMAC-SHA256", query["X-Amz-Algorithm"]);
            Assert.Equal("scoped-session", query["X-Amz-Security-Token"]);
            Assert.StartsWith("scoped-key/", query["X-Amz-Credential"]);
            var checksum = Convert.ToBase64String(Convert.FromHexString(Oid));
            Assert.Equal(checksum, signed.Headers["x-amz-checksum-sha256"]);
            // The checksum must be authenticated, whether the SDK signs it as
            // a header or hoists it into the canonical query string.
            Assert.True(query["X-Amz-SignedHeaders"]?.Contains("x-amz-checksum-sha256") == true || query["x-amz-checksum-sha256"] == checksum);
            Assert.Null(query["AWSAccessKeyId"]); // SigV2 cannot resolve STS credentials.
            Assert.InRange(int.Parse(query["X-Amz-Expires"]), 1, 240);
            Assert.True(signed.Expiry < TimeSpan.FromMinutes(4));
            await adapter.UriForUpload(Oid, 123, CancellationToken.None);
            Assert.Equal(1, handler.Calls);
        }

        [Fact]
        public async Task AnotherRepositoryCannotUseTheGrantAndStsFailureHasNoFallback()
        {
            var handler = new StsHandler();
            using var http = new HttpClient(handler);
            using var other = new RepositoryScopedS3BlobAdapter(Grant(), Guid.NewGuid().ToString(), "lfs", "https://s3.example", "http://sts.example", http);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => other.UriForUpload(Oid, 1, CancellationToken.None));
            Assert.Equal(0, handler.Calls);
            handler.Status = HttpStatusCode.Forbidden;
            using var denied = new RepositoryScopedS3BlobAdapter(Grant(), Repo, "lfs", "https://s3.example", "http://sts.example", http);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => denied.UriForUpload(Oid, 1, CancellationToken.None));
        }

        [Fact]
        public async Task TransientStsFailuresAreRetried()
        {
            var handler = new StsHandler();
            handler.Script.Enqueue(HttpStatusCode.ServiceUnavailable);
            handler.Script.Enqueue(new HttpRequestException("Connection reset"));
            using var http = new HttpClient(handler);
            using var adapter = new RepositoryScopedS3BlobAdapter(Grant(), Repo, "lfs", "https://s3.example", "http://sts.example", http, retryPolicy: Fast);
            var signed = await adapter.UriForUpload(Oid, 1, CancellationToken.None);
            Assert.Equal("/lfs/AppMana/lbxx/" + Oid, signed.Uri.AbsolutePath);
            Assert.Equal(3, handler.Calls);
        }

        [Fact]
        public async Task ExhaustedStsFailuresAskTheClientToRetryAndAreNotRepeatedPerObject()
        {
            var handler = new StsHandler { Status = HttpStatusCode.BadGateway };
            using var http = new HttpClient(handler);
            using var adapter = new RepositoryScopedS3BlobAdapter(Grant(), Repo, "lfs", "https://s3.example", "http://sts.example", http, retryPolicy: Fast);
            await Assert.ThrowsAsync<TransientUpstreamException>(() => adapter.UriForDownload(Oid, CancellationToken.None));
            await Assert.ThrowsAsync<TransientUpstreamException>(() => adapter.UriForDownload(Oid, CancellationToken.None));
            Assert.Equal(3, handler.Calls);
        }

        [Fact]
        public async Task RejectedStsExchangeIsNotRetriedOrRepeated()
        {
            var handler = new StsHandler { Status = HttpStatusCode.Forbidden };
            using var http = new HttpClient(handler);
            using var adapter = new RepositoryScopedS3BlobAdapter(Grant(), Repo, "lfs", "https://s3.example", "http://sts.example", http, retryPolicy: Fast);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => adapter.UriForUpload(Oid, 1, CancellationToken.None));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => adapter.UriForUpload(Oid, 1, CancellationToken.None));
            Assert.Equal(1, handler.Calls);
        }

        sealed class RecordingLogger : ILogger
        {
            public readonly List<(LogLevel Level, string Message, IReadOnlyList<KeyValuePair<string, object>> State)> Entries = new();
            public IDisposable BeginScope<TState>(TState state) => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            {
                lock (Entries) Entries.Add((logLevel, formatter(state, exception) + " " + exception, state as IReadOnlyList<KeyValuePair<string, object>> ?? Array.Empty<KeyValuePair<string, object>>()));
            }
        }

        static AmazonS3Exception Forbidden() =>
            new("Access Denied", ErrorType.Sender, "AccessDenied", "requestId", HttpStatusCode.Forbidden);

        // Each STS exchange builds a new scoped client; the queue hands out one
        // per exchange so tests can tell whether a retry used fresh credentials.
        static Func<AWSCredentials, AmazonS3Config, IAmazonS3> Clients(Queue<Func<GetObjectMetadataResponse>> heads, List<string> sessions)
        {
            return (credentials, config) =>
            {
                sessions.Add(credentials.GetCredentials().Token);
                var head = heads.Dequeue();
                var client = new Mock<IAmazonS3>();
                client.Setup(x => x.Config).Returns(config);
                client.Setup(x => x.GetObjectMetadataAsync("lfs", "AppMana/lbxx/" + Oid, It.IsAny<CancellationToken>()))
                      .Returns(() => Task.FromResult(head()));
                client.Setup(x => x.GetPreSignedURL(It.IsAny<GetPreSignedUrlRequest>()))
                      .Returns((GetPreSignedUrlRequest request) => $"https://s3.example/{request.BucketName}/{request.Key}?verb={request.Verb}");
                return client.Object;
            };
        }

        static void AssertForbiddenWarnings(RecordingLogger logger, int count)
        {
            var warnings = logger.Entries.Where(x => x.Level == LogLevel.Warning).ToList();
            Assert.Equal(count, warnings.Count);
            foreach (var warning in warnings)
            {
                var state = warning.State.ToDictionary(x => x.Key, x => x.Value);
                Assert.Equal(Repo, state["RepositoryId"]);
                Assert.Equal(Oid, state["Oid"]);
                foreach (var secret in new[] { "scoped-secret", "scoped-session", "repository-token", "X-Amz-Signature" })
                    Assert.DoesNotContain(secret, warning.Message);
            }
        }

        [Fact]
        public async Task ScopedForbiddenMetadataIsRetriedWithFreshStorageCredentials()
        {
            var handler = new StsHandler();
            using var http = new HttpClient(handler);
            var sessions = new List<string>();
            var heads = new Queue<Func<GetObjectMetadataResponse>>(new Func<GetObjectMetadataResponse>[]
            {
                () => throw Forbidden(),
                () => new GetObjectMetadataResponse { ContentLength = 5 },
            });
            var logger = new RecordingLogger();
            using var adapter = new RepositoryScopedS3BlobAdapter(Grant(), Repo, "lfs", "https://s3.example", "http://sts.example", http,
                retryPolicy: Fast, logger: logger, clientFactory: Clients(heads, sessions));

            var signed = await adapter.UriForDownload(Oid, CancellationToken.None);

            Assert.Null(signed.ErrorCode);
            Assert.Equal(5, signed.Size);
            Assert.Equal(2, handler.Calls);
            Assert.Equal(2, sessions.Count);
            AssertForbiddenWarnings(logger, 1);
        }

        [Fact]
        public async Task PersistentScopedForbiddenFailsTheBatchRetryablyInsteadOfTheObject()
        {
            var handler = new StsHandler();
            using var http = new HttpClient(handler);
            var sessions = new List<string>();
            var heads = new Queue<Func<GetObjectMetadataResponse>>(Enumerable.Repeat<Func<GetObjectMetadataResponse>>(() => throw Forbidden(), 3));
            var logger = new RecordingLogger();
            using var adapter = new RepositoryScopedS3BlobAdapter(Grant(), Repo, "lfs", "https://s3.example", "http://sts.example", http,
                retryPolicy: Fast, logger: logger, clientFactory: Clients(heads, sessions));

            var failure = await Assert.ThrowsAsync<TransientUpstreamException>(() => adapter.UriForDownload(Oid, CancellationToken.None));

            Assert.IsType<AmazonS3Exception>(failure.InnerException?.InnerException ?? failure.InnerException);
            Assert.Equal(3, handler.Calls);
            AssertForbiddenWarnings(logger, 3);
        }

        [Fact]
        public async Task ScopedForbiddenDeduplicationHeadIsRetriedWithFreshStorageCredentials()
        {
            var handler = new StsHandler();
            using var http = new HttpClient(handler);
            var sessions = new List<string>();
            var heads = new Queue<Func<GetObjectMetadataResponse>>(new Func<GetObjectMetadataResponse>[]
            {
                () => throw Forbidden(),
                () => new GetObjectMetadataResponse { ContentLength = 7 },
            });
            var logger = new RecordingLogger();
            using var adapter = new RepositoryScopedS3BlobAdapter(Grant(), Repo, "lfs", "https://s3.example", "http://sts.example", http,
                deduplicateUploads: true, retryPolicy: Fast, logger: logger, clientFactory: Clients(heads, sessions));

            var signed = await adapter.UriForUpload(Oid, 7, CancellationToken.None);

            Assert.True(signed.AlreadyExists);
            Assert.Equal(2, handler.Calls);
            AssertForbiddenWarnings(logger, 1);
        }

        [Theory]
        [InlineData("../another-repo/oid")]
        [InlineData("not-an-oid")]
        public async Task InvalidOidsNeverReachSts(string oid)
        {
            var handler = new StsHandler();
            using var http = new HttpClient(handler);
            using var adapter = new RepositoryScopedS3BlobAdapter(Grant(), Repo, "lfs", "https://s3.example", "http://sts.example", http);
            await Assert.ThrowsAsync<ArgumentException>(() => adapter.UriForUpload(oid, 1, CancellationToken.None));
            Assert.Equal(0, handler.Calls);
        }
    }
}

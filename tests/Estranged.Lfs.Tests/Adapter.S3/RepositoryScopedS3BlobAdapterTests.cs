using Estranged.Lfs.Adapter.S3;
using Estranged.Lfs.Data;
using System;
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
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                Calls++;
                Assert.Equal("http://sts.example/", request.RequestUri.ToString());
                var form = HttpUtility.ParseQueryString(await request.Content.ReadAsStringAsync(token));
                Assert.Equal("repository-token", form["WebIdentityToken"]);
                Assert.Equal("arn:aws:iam:::role/sts-claim-based", form["RoleArn"]);
                return new HttpResponseMessage(Status) { Content = new StringContent(
                    "<Response><Credentials><AccessKeyId>scoped-key</AccessKeyId><SecretAccessKey>scoped-secret</SecretAccessKey>" +
                    "<SessionToken>scoped-session</SessionToken><Expiration>" + DateTimeOffset.UtcNow.AddMinutes(4).ToString("O") +
                    "</Expiration></Credentials></Response>") };
            }
        }
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
            Assert.Contains("scoped-session", signed.Uri.Query);
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

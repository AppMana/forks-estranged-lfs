using System.Net;
using Estranged.Lfs.Adapter.S3;
using Xunit;
namespace Estranged.Lfs.Registry.Tests;
public sealed class RootCredentialsTests
{
    sealed class Sts : HttpMessageHandler
    {
        public HttpStatusCode Status;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent("<ErrorResponse><Error><Code>ServiceUnavailable</Code></Error></ErrorResponse>") });
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.Forbidden, false)]
    public async Task RootStorageCredentialRefreshFailuresAreClassifiedForRetry(HttpStatusCode status, bool transient)
    {
        var tokenFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tokenFile, "service-account-token");
            using var http = new HttpClient(new Sts { Status = status });
            var credentials = new WebIdentityStsCredentials("http://sts.example/", "arn:aws:iam:::role/lfs", "estranged-lfs", tokenFile, http);
            var failure = await Assert.ThrowsAnyAsync<Exception>(() => credentials.GetCredentialsAsync());
            Assert.Equal(transient, S3BlobAdapter.IsTransientStorageFailure(failure));
            Assert.DoesNotContain("service-account-token", failure.ToString());
        }
        finally { File.Delete(tokenFile); }
    }
}

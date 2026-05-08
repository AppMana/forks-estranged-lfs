// Minimal ASP.NET Core host for Estranged.Lfs Git LFS server, intended
// to run as a container in-cluster as a drop-in replacement for the
// alanedwardes/Estranged.Lfs Lambda. The configuration surface is the
// same env-var schema as the Lambda's Startup.cs (LFS_BUCKET,
// LFS_USERNAME, LFS_PASSWORD, GITHUB_*, BITBUCKET_*, S3_ACCELERATION,
// LFS_AZUREBLOB_*) plus AWS_ENDPOINT_URL_S3 / AWS_REGION /
// AWS_ACCESS_KEY_ID / AWS_SECRET_ACCESS_KEY for SeaweedFS targeting.
using System;
using System.Collections.Generic;
using System.Linq;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Estranged.Lfs.Adapter.Azure.Blob;
using Estranged.Lfs.Adapter.S3;
using Estranged.Lfs.Api;
using Estranged.Lfs.Authenticator.BitBucket;
using Estranged.Lfs.Authenticator.GitHub;
using Estranged.Lfs.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Trace AWS SDK calls so we can see any request/sign mismatches.
Amazon.AWSConfigs.LoggingConfig.LogTo = Amazon.LoggingOptions.Console;
Amazon.AWSConfigs.LoggingConfig.LogResponses = Amazon.ResponseLoggingOption.Always;
Amazon.AWSConfigs.LoggingConfig.LogMetrics = true;

var builder = WebApplication.CreateBuilder(args);

// --- env-var configuration (matches the Lambda host's Startup.cs) ---
var cfg = builder.Configuration;
string lfsBucket   = cfg["LFS_BUCKET"];
string lfsUser     = cfg["LFS_USERNAME"];
string lfsPass     = cfg["LFS_PASSWORD"];
string ghOrg       = cfg["GITHUB_ORGANISATION"];
string ghRepo      = cfg["GITHUB_REPOSITORY"];
string bbWs        = cfg["BITBUCKET_WORKSPACE"];
string bbRepo      = cfg["BITBUCKET_REPOSITORY"];
bool   s3Accel     = bool.Parse(cfg["S3_ACCELERATION"] ?? "false");
string azConn      = cfg["LFS_AZUREBLOB_CONNECTIONSTRING"];
string azContainer = cfg["LFS_AZUREBLOB_CONTAINERNAME"];
string s3Endpoint  = cfg["AWS_ENDPOINT_URL_S3"];     // SeaweedFS S3 endpoint, optional

bool isS3        = !string.IsNullOrWhiteSpace(lfsBucket);
bool isAzure     = !string.IsNullOrWhiteSpace(azConn);
bool isDictAuth  = !string.IsNullOrWhiteSpace(lfsUser) && !string.IsNullOrWhiteSpace(lfsPass);
bool isGhAuth    = !string.IsNullOrWhiteSpace(ghOrg)   && !string.IsNullOrWhiteSpace(ghRepo);
bool isBbAuth    = !string.IsNullOrWhiteSpace(bbWs)    && !string.IsNullOrWhiteSpace(bbRepo);

if (new[] { isDictAuth, isGhAuth, isBbAuth }.Count(x => x) != 1)
    throw new InvalidOperationException(
        "Set exactly one auth backend: LFS_USERNAME+LFS_PASSWORD, " +
        "GITHUB_ORGANISATION+GITHUB_REPOSITORY, or BITBUCKET_WORKSPACE+BITBUCKET_REPOSITORY.");

var services = builder.Services;

if      (isDictAuth) services.AddLfsDictionaryAuthenticator(new Dictionary<string, string> { { lfsUser, lfsPass } });
else if (isGhAuth)   services.AddLfsGitHubAuthenticator(new GitHubAuthenticatorConfig    { Organisation = ghOrg, Repository = ghRepo });
else if (isBbAuth)   services.AddLfsBitBucketAuthenticator(new BitBucketAuthenticatorConfig { Workspace = bbWs, Repository = bbRepo });

if (isS3)
{
    var s3Config = new AmazonS3Config { UseAccelerateEndpoint = s3Accel };
    string awsRegion = cfg["AWS_REGION"] ?? cfg["AWS_DEFAULT_REGION"];
    if (!string.IsNullOrWhiteSpace(awsRegion))
        s3Config.RegionEndpoint = RegionEndpoint.GetBySystemName(awsRegion);
    if (!string.IsNullOrWhiteSpace(s3Endpoint))
    {
        // SeaweedFS / non-AWS path: ServiceURL + path style.
        s3Config.ServiceURL = s3Endpoint;
        s3Config.ForcePathStyle = true;
    }
    // Force env-var creds when AWS_ACCESS_KEY_ID is present. The .NET
    // SDK's default chain prefers ~/.aws/credentials over env vars, so
    // without this a stale local profile would silently override the
    // pod's mounted Secret.
    string awsAk = cfg["AWS_ACCESS_KEY_ID"];
    string awsSk = cfg["AWS_SECRET_ACCESS_KEY"];
    AmazonS3Client s3Client = !string.IsNullOrWhiteSpace(awsAk) && !string.IsNullOrWhiteSpace(awsSk)
        ? new AmazonS3Client(new BasicAWSCredentials(awsAk, awsSk), s3Config)
        : new AmazonS3Client(s3Config);
    services.AddLfsS3Adapter(new S3BlobAdapterConfig { Bucket = lfsBucket }, s3Client);
}
else if (isAzure)
{
    services.AddLfsAzureBlobAdapter(new AzureBlobAdapterConfig
    {
        ConnectionString = azConn,
        ContainerName    = azContainer
    });
}
else throw new InvalidOperationException("Set LFS_BUCKET (S3) or LFS_AZUREBLOB_CONNECTIONSTRING (Azure).");

services.AddLfsApi();
services.AddLogging(x => { x.AddConsole(); x.AddDebug(); });

// Health endpoint without auth, plus the LFS controllers.
var app = builder.Build();
app.MapGet("/healthz", () => Results.Ok(new { ok = true, mode = isS3 ? "s3" : "azure", bucket = lfsBucket }));
app.UseRouting();
app.UseEndpoints(e => e.MapControllers());
app.Run();

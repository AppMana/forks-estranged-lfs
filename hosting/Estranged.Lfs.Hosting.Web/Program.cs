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
using System.Text.Json;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Estranged.Lfs.Adapter.Azure.Blob;
using Estranged.Lfs.Adapter.S3;
using Estranged.Lfs.Api;
using Estranged.Lfs.Authenticator.BitBucket;
using Estranged.Lfs.Authenticator.GitHub;
using Estranged.Lfs.Authenticator.Keycloak;
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
string kcRealmUrl  = cfg["KEYCLOAK_REALM_URL"];
string kcRole      = cfg["KEYCLOAK_ROLE"] ?? "lfs";
string kcPrefix    = cfg["KEYCLOAK_CLIENT_PREFIX"] ?? "git-lfs-";
bool   s3Accel     = bool.Parse(cfg["S3_ACCELERATION"] ?? "false");
string azConn      = cfg["LFS_AZUREBLOB_CONNECTIONSTRING"];
string azContainer = cfg["LFS_AZUREBLOB_CONTAINERNAME"];
string s3Endpoint  = cfg["AWS_ENDPOINT_URL_S3"];     // SeaweedFS S3 endpoint, optional
string fallbackBucket = cfg["LFS_FALLBACK_BUCKET"];  // old AWS LFS bucket, optional read-through only
string fallbackRegion = cfg["LFS_FALLBACK_AWS_REGION"] ?? cfg["AWS_REGION"] ?? cfg["AWS_DEFAULT_REGION"];
string fallbackAk     = cfg["LFS_FALLBACK_AWS_ACCESS_KEY_ID"];
string fallbackSk     = cfg["LFS_FALLBACK_AWS_SECRET_ACCESS_KEY"];
string fallbackEndpoint = cfg["LFS_FALLBACK_AWS_ENDPOINT_URL_S3"];
string fallbacksJson = cfg["LFS_FALLBACKS_JSON"];

bool isS3        = !string.IsNullOrWhiteSpace(lfsBucket);
bool isAzure     = !string.IsNullOrWhiteSpace(azConn);
bool isDictAuth  = !string.IsNullOrWhiteSpace(lfsUser) && !string.IsNullOrWhiteSpace(lfsPass);
bool isGhAuth    = !string.IsNullOrWhiteSpace(ghOrg)   && !string.IsNullOrWhiteSpace(ghRepo);
bool isBbAuth    = !string.IsNullOrWhiteSpace(bbWs)    && !string.IsNullOrWhiteSpace(bbRepo);
bool isKcAuth    = !string.IsNullOrWhiteSpace(kcRealmUrl);

if (new[] { isDictAuth, isGhAuth, isBbAuth, isKcAuth }.Count(x => x) != 1)
    throw new InvalidOperationException(
        "Set exactly one auth backend: LFS_USERNAME+LFS_PASSWORD, " +
        "GITHUB_ORGANISATION+GITHUB_REPOSITORY, BITBUCKET_WORKSPACE+BITBUCKET_REPOSITORY, " +
        "or KEYCLOAK_REALM_URL.");

var services = builder.Services;

static AmazonS3Client CreateS3Client(string endpoint, string region, string accessKey, string secretKey, bool accelerate)
{
    var s3Config = new AmazonS3Config { UseAccelerateEndpoint = accelerate };
    if (!string.IsNullOrWhiteSpace(region))
        s3Config.RegionEndpoint = RegionEndpoint.GetBySystemName(region);
    if (!string.IsNullOrWhiteSpace(endpoint))
    {
        s3Config.ServiceURL = endpoint;
        s3Config.ForcePathStyle = true;
    }
    return !string.IsNullOrWhiteSpace(accessKey) && !string.IsNullOrWhiteSpace(secretKey)
        ? new AmazonS3Client(new BasicAWSCredentials(accessKey, secretKey), s3Config)
        : new AmazonS3Client(s3Config);
}

if      (isDictAuth) services.AddLfsDictionaryAuthenticator(new Dictionary<string, string> { { lfsUser, lfsPass } });
else if (isGhAuth)   services.AddLfsGitHubAuthenticator(new GitHubAuthenticatorConfig    { Organisation = ghOrg, Repository = ghRepo });
else if (isBbAuth)   services.AddLfsBitBucketAuthenticator(new BitBucketAuthenticatorConfig { Workspace = bbWs, Repository = bbRepo });
else if (isKcAuth)   services.AddLfsKeycloakAuthenticator(new KeycloakAuthenticatorConfig { RealmUrl = kcRealmUrl, RequiredRole = kcRole, ClientPrefix = kcPrefix });

if (isS3)
{
    string awsRegion = cfg["AWS_REGION"] ?? cfg["AWS_DEFAULT_REGION"];
    // Force env-var creds when AWS_ACCESS_KEY_ID is present. The .NET
    // SDK's default chain prefers ~/.aws/credentials over env vars, so
    // without this a stale local profile would silently override the
    // pod's mounted Secret.
    string awsAk = cfg["AWS_ACCESS_KEY_ID"];
    string awsSk = cfg["AWS_SECRET_ACCESS_KEY"];
    AmazonS3Client s3Client = CreateS3Client(s3Endpoint, awsRegion, awsAk, awsSk, s3Accel);
    services.AddSingleton<IAmazonS3>(s3Client);
    if (!string.IsNullOrWhiteSpace(fallbackBucket) &&
        !string.IsNullOrWhiteSpace(fallbackAk) &&
        !string.IsNullOrWhiteSpace(fallbackSk))
    {
        services.AddSingleton<IEnumerable<FallbackS3>>(new[]
        {
            new FallbackS3(
            CreateS3Client(fallbackEndpoint, fallbackRegion, fallbackAk, fallbackSk, false),
            new S3BlobAdapterConfig { Bucket = fallbackBucket, KeyPrefix = "" })
        });
    }
    else if (!string.IsNullOrWhiteSpace(fallbacksJson))
    {
        var fallbackDefs = JsonSerializer.Deserialize<List<S3FallbackConfig>>(fallbacksJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        services.AddSingleton<IEnumerable<FallbackS3>>(fallbackDefs
            .Where(x => !string.IsNullOrWhiteSpace(x.Bucket) && !string.IsNullOrWhiteSpace(x.AccessKeyId) && !string.IsNullOrWhiteSpace(x.SecretAccessKey))
            .Select(x => new FallbackS3(
                CreateS3Client(x.EndpointUrl, x.Region ?? fallbackRegion, x.AccessKeyId, x.SecretAccessKey, false),
                new S3BlobAdapterConfig { Bucket = x.Bucket, KeyPrefix = x.KeyPrefix ?? "" }))
            .ToList());
    }
    services.AddHttpContextAccessor();
    services.AddScoped<IBlobAdapter>(sp =>
    {
        var http = sp.GetRequiredService<IHttpContextAccessor>().HttpContext;
        string org = http?.Request.RouteValues.TryGetValue("org", out object orgValue) == true ? orgValue?.ToString() : null;
        string repo = http?.Request.RouteValues.TryGetValue("repo", out object repoValue) == true ? repoValue?.ToString() : null;
        string keyPrefix = string.IsNullOrWhiteSpace(org) || string.IsNullOrWhiteSpace(repo) ? "" : $"{org}/{repo}/";
        IBlobAdapter primary = new S3BlobAdapter(sp.GetRequiredService<IAmazonS3>(), new S3BlobAdapterConfig { Bucket = lfsBucket, KeyPrefix = keyPrefix });
        var fallbacks = sp.GetService<IEnumerable<FallbackS3>>()?
            .Select(x => new S3BlobAdapter(x.Client, x.Config))
            .ToList();
        return fallbacks == null || fallbacks.Count == 0 ? primary : new FallbackBlobAdapter(primary, fallbacks);
    });
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

sealed record FallbackS3(AmazonS3Client Client, S3BlobAdapterConfig Config);
sealed record S3FallbackConfig(
    string Bucket,
    string KeyPrefix,
    string Region,
    string EndpointUrl,
    string AccessKeyId,
    string SecretAccessKey);

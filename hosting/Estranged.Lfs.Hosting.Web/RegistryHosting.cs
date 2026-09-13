using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Estranged.Lfs.Data;
using Estranged.Lfs.Registry;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

public static class RegistryHosting
{
    public static void Configure(IServiceCollection services, IConfiguration config)
    {
        var url = config["LFS_PUBLIC_URL"] ?? "https://lfs.appmana.com";
        services.AddDbContext<RepositoryDbContext>(o => o.UseNpgsql(RepositoryDbContextFactory.ConnectionString()));
        services.AddScoped<RepositoryGrant>();
        services.AddSingleton(new RepositoryCredentialKeys(File.ReadAllText(config["LFS_CREDENTIAL_KEYS_FILE"])));
        services.AddSingleton<IIdentityVerifier>(new FederatedIdentityVerifier(config["LFS_TRUSTED_ISSUERS_JSON"], url));
        var signer = new StorageTokenIssuer(url, File.ReadAllText(config["LFS_SIGNING_KEY_FILE"]));
        services.AddSingleton(signer);
        services.AddSingleton<IStorageTokenIssuer>(signer);
        services.AddScoped<IRepositoryAuthenticator, RepositoryAuthenticator>();
        services.AddScoped(sp => new EnrollmentService(sp.GetRequiredService<RepositoryDbContext>(), sp.GetRequiredService<RepositoryCredentialKeys>(), url));
        services.AddHttpClient();
    }
    static async Task<Principal> Actor(HttpContext http)
    {
        http.Response.Headers.CacheControl = "no-store";
        string authorization = http.Request.Headers.Authorization;
        if (authorization?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) != true) throw new AuthenticationException();
        var identity = await http.RequestServices.GetRequiredService<IIdentityVerifier>().Verify(authorization[7..].Trim(), http.RequestAborted);
        return identity.Principal;
    }
    static async Task<IResult> Control(HttpContext context, Func<Task<IResult>> action)
    {
        context.Response.Headers.CacheControl = "no-store";
        try { return await action(); }
        catch (AuthenticationException) { return Results.Unauthorized(); }
        catch (RegistryDeniedException) { return Results.StatusCode(403); }
        catch (ArgumentException) { return Results.BadRequest(new { error = "Invalid enrollment request." }); }
        catch (JsonException) { return Results.BadRequest(new { error = "Invalid JSON." }); }
    }
    public static void Map(WebApplication app)
    {
        app.MapGet("/.well-known/jwks.json", (StorageTokenIssuer issuer) => Results.Json(issuer.Jwks));
        app.MapGet("/.well-known/openid-configuration", (StorageTokenIssuer issuer) => Results.Json(new
        {
            issuer = issuer.Issuer, jwks_uri = issuer.Issuer + "/.well-known/jwks.json",
            response_types_supported = new[] { "id_token" }, subject_types_supported = new[] { "public" },
            id_token_signing_alg_values_supported = new[] { "RS256" }
        }));
        app.MapGet("/readyz", async (RepositoryDbContext db) =>
            await db.Database.CanConnectAsync() && !(await db.Database.GetPendingMigrationsAsync()).Any() ? Results.Ok() : Results.StatusCode(503));
        app.MapGet("/api/v1/enrollments/{key}", (HttpContext http, string key, RepositoryDbContext db) => Control(http, async () =>
        {
            var actor = await Actor(http);
            if (!await db.Provisioners.AnyAsync(x => x.PrincipalKey == actor.Key && x.Enabled, http.RequestAborted)) throw new RegistryDeniedException();
            var enrollment = await db.Enrollments.AsNoTracking().Include(x => x.Repository).SingleOrDefaultAsync(x => x.OwnerKey == actor.Key && x.Key == key, http.RequestAborted);
            if (enrollment == null) return Results.NotFound();
            return Results.Ok(new { id = enrollment.RepositoryId, displayName = enrollment.Repository.DisplayName, enabled = enrollment.Repository.Enabled });
        }));
        app.MapPut("/api/v1/enrollments/{key}", (HttpContext http, string key, EnrollmentRequest request, EnrollmentService service) => Control(http, async () =>
            Results.Ok(await service.Reconcile(await Actor(http), key, request, http.RequestAborted))));
        app.MapPut("/api/v1/repositories/{id:guid}/grants/{manager}", (HttpContext http, Guid id, string manager, DesiredGrant[] grants, EnrollmentService service) => Control(http, async () =>
        {
            await service.ReconcileGrants(await Actor(http), id, manager, grants, http.RequestAborted);
            return Results.NoContent();
        }));
        app.MapDelete("/api/v1/repositories/{id:guid}/credentials", (HttpContext http, Guid id, EnrollmentService service) => Control(http, async () =>
        {
            await service.RevokeCredential(await Actor(http), id, http.RequestAborted);
            return Results.NoContent();
        }));
    }
}

public sealed record TrustedIssuer(string Issuer, string CaFile = null, string DiscoveryTokenFile = null);
public sealed class FederatedIdentityVerifier : IIdentityVerifier
{
    readonly Dictionary<string, ConfigurationManager<OpenIdConnectConfiguration>> issuers = new(StringComparer.Ordinal);
    readonly string audience;
    public FederatedIdentityVerifier(string document, string audience, Func<TrustedIssuer, HttpClient> clients = null)
    {
        this.audience = audience;
        var definitions = JsonSerializer.Deserialize<TrustedIssuer[]>(document, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (definitions == null || definitions.Length == 0) throw new ArgumentException("Trusted issuers required.");
        foreach (var definition in definitions)
        {
            new Principal(definition.Issuer, "configuration").Validate();
            var handler = new HttpClientHandler { AllowAutoRedirect = false };
            if (definition.CaFile != null)
            {
                var root = X509Certificate2.CreateFromPemFile(definition.CaFile);
                handler.ServerCertificateCustomValidationCallback = (_, cert, _, errors) =>
                {
                    if (cert == null || (errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0) return false;
                    using var chain = new X509Chain();
                    chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                    chain.ChainPolicy.CustomTrustStore.Add(root);
                    chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                    return chain.Build(cert);
                };
            }
            var client = clients?.Invoke(definition) ?? new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            var retriever = new IssuerDocumentRetriever(client, definition);
            issuers.Add(definition.Issuer, new ConfigurationManager<OpenIdConnectConfiguration>(definition.Issuer.TrimEnd('/') + "/.well-known/openid-configuration", new OpenIdConnectConfigurationRetriever(), retriever));
        }
    }
    public async Task<VerifiedIdentity> Verify(string assertion, CancellationToken token)
    {
        if (string.IsNullOrEmpty(assertion) || assertion.Length > 32768) throw new AuthenticationException();
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        try
        {
            var untrusted = handler.ReadJwtToken(assertion);
            // Unverified issuer selects only a preconfigured trust root. It
            // never supplies an arbitrary discovery URL or authorization claim.
            if (!issuers.TryGetValue(untrusted.Issuer, out var manager)) throw new AuthenticationException();
            var configuration = await manager.GetConfigurationAsync(token);
            if (configuration.Issuer != untrusted.Issuer) throw new AuthenticationException();
            ClaimsPrincipal principal;
            SecurityToken validated;
            try { principal = handler.ValidateToken(assertion, Parameters(configuration), out validated); }
            catch (SecurityTokenSignatureKeyNotFoundException)
            {
                manager.RequestRefresh();
                configuration = await manager.GetConfigurationAsync(token);
                principal = handler.ValidateToken(assertion, Parameters(configuration), out validated);
            }
            var subject = principal.FindAll("sub").Single().Value;
            var identity = new Principal(configuration.Issuer, subject); identity.Validate();
            return new VerifiedIdentity(identity, validated.ValidTo);
        }
        catch (Exception ex) when (ex is SecurityTokenException || ex is ArgumentException || ex is InvalidOperationException)
        { throw new AuthenticationException("Invalid identity."); }
    }
    TokenValidationParameters Parameters(OpenIdConnectConfiguration configuration) => new()
    {
        ValidateIssuer = true, ValidIssuer = configuration.Issuer,
        ValidateAudience = true, ValidAudience = audience,
        ValidateLifetime = true, RequireExpirationTime = true, ClockSkew = TimeSpan.Zero,
        RequireSignedTokens = true, ValidateIssuerSigningKey = true,
        IssuerSigningKeys = configuration.SigningKeys,
        ValidAlgorithms = new[] { SecurityAlgorithms.RsaSha256, SecurityAlgorithms.RsaSha384, SecurityAlgorithms.RsaSha512, SecurityAlgorithms.EcdsaSha256 }
    };
}
sealed class IssuerDocumentRetriever : IDocumentRetriever
{
    readonly HttpClient client;
    readonly TrustedIssuer issuer;
    public IssuerDocumentRetriever(HttpClient client, TrustedIssuer issuer) { this.client = client; this.issuer = issuer; }
    public async Task<string> GetDocumentAsync(string address, CancellationToken cancel)
    {
        var uri = new Uri(address);
        if (uri.Scheme != "https" || uri.GetLeftPart(UriPartial.Authority) != new Uri(issuer.Issuer).GetLeftPart(UriPartial.Authority)) throw new AuthenticationException("Discovery origin is not trusted.");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (issuer.DiscoveryTokenFile != null)
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", (await File.ReadAllTextAsync(issuer.DiscoveryTokenFile, cancel)).Trim());
        using var response = await client.SendAsync(request, cancel);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancel);
    }
}
public sealed class StorageTokenIssuer : IStorageTokenIssuer, IDisposable
{
    readonly RSA rsa;
    readonly SigningCredentials signing;
    public string Issuer { get; }
    public object Jwks { get; }
    public StorageTokenIssuer(string issuer, string pem)
    {
        Issuer = issuer.TrimEnd('/');
        rsa = RSA.Create(); rsa.ImportFromPem(pem);
        if (rsa.KeySize < 2048) throw new ArgumentException("RSA key is too small.");
        var key = new RsaSecurityKey(rsa);
        key.KeyId = Base64UrlEncoder.Encode(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo()));
        signing = new SigningCredentials(key, SecurityAlgorithms.RsaSha256);
        var pub = rsa.ExportParameters(false);
        Jwks = new { keys = new[] { new { kty = "RSA", use = "sig", alg = "RS256", kid = key.KeyId, n = Base64UrlEncoder.Encode(pub.Modulus), e = Base64UrlEncoder.Encode(pub.Exponent) } } };
    }
    public string Issue(Guid repositoryId, string storagePrefix, bool write, DateTime expiresAt)
    {
        if (repositoryId == Guid.Empty || !RepositoryGrant.IsStoragePrefix(storagePrefix)) throw new ArgumentException("Invalid storage binding.");
        var now = DateTime.UtcNow;
        expiresAt = expiresAt < now.AddMinutes(5) ? expiresAt : now.AddMinutes(5);
        if (expiresAt <= now) throw new AuthenticationException("Expired identity.");
        var claims = new[] { new Claim("sub", "r/" + repositoryId.ToString("D")), new Claim("repository_id", repositoryId.ToString("D")),
            new Claim("storage_prefix", storagePrefix), new Claim("seaweedfs_policies", write ? "lfs-repository-rw" : "lfs-repository-read"), new Claim("jti", Guid.NewGuid().ToString("N")) };
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(Issuer, RepositoryGrant.StorageAudience, claims, now, expiresAt, signing));
    }
    public void Dispose() => rsa.Dispose();
}

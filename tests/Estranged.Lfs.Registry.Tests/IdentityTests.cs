using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Authentication;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Estranged.Lfs.Data;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Estranged.Lfs.Registry.Tests;
public sealed class IdentityTests
{
    sealed class Discovery : HttpMessageHandler
    {
        public int Calls;
        readonly string jwks;
        public Discovery(RSA rsa)
        {
            var p = rsa.ExportParameters(false);
            jwks = JsonSerializer.Serialize(new { keys = new[] { new { kty = "RSA", kid = "test", use = "sig", alg = "RS256", n = Base64UrlEncoder.Encode(p.Modulus), e = Base64UrlEncoder.Encode(p.Exponent) } } });
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            Assert.Equal("https://identity.test", request.RequestUri.GetLeftPart(UriPartial.Authority));
            var json = request.RequestUri.AbsolutePath == "/keys" ? jwks : "{\"issuer\":\"https://identity.test\",\"jwks_uri\":\"https://identity.test/keys\"}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }
    static string Token(RSA rsa, string issuer = "https://identity.test", string audience = "https://lfs.test", bool expired = false)
    {
        var now = DateTime.UtcNow;
        var signing = new SigningCredentials(new RsaSecurityKey(rsa) { KeyId = "test" }, SecurityAlgorithms.RsaSha256);
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(issuer, audience, new[] { new Claim("sub", "controller") }, now.AddMinutes(-10), expired ? now.AddMinutes(-1) : now.AddMinutes(5), signing));
    }
    [Theory]
    [InlineData("valid")]
    [InlineData("audience")]
    [InlineData("expired")]
    [InlineData("signature")]
    [InlineData("issuer")]
    public async Task FederationValidatesSignatureIssuerAudienceAndExpiry(string scenario)
    {
        using var rsa = RSA.Create(2048); using var attacker = RSA.Create(2048);
        using var discovery = new Discovery(rsa); using var client = new HttpClient(discovery);
        var verifier = new FederatedIdentityVerifier("[{\"issuer\":\"https://identity.test\"}]", "https://lfs.test", _ => client);
        var token = Token(scenario == "signature" ? attacker : rsa, scenario == "issuer" ? "https://attacker.test" : "https://identity.test", scenario == "audience" ? "other-service" : "https://lfs.test", scenario == "expired");
        if (scenario == "valid") Assert.Equal("controller", (await verifier.Verify(token, default)).Principal.Subject);
        else await Assert.ThrowsAsync<AuthenticationException>(() => verifier.Verify(token, default));
        if (scenario == "issuer") Assert.Equal(0, discovery.Calls);
    }
    [Fact] public void StorageTokensCarryOnlyValidatedScopeAndBoundedLifetime()
    {
        using var rsa = RSA.Create(2048); using var issuer = new StorageTokenIssuer("https://lfs.test", rsa.ExportRSAPrivateKeyPem());
        var id = Guid.NewGuid();
        var token = issuer.Issue(id, "AppMana/lbxx/", false, DateTime.UtcNow.AddHours(1));
        new JwtSecurityTokenHandler().ValidateToken(token, new TokenValidationParameters
        {
            ValidIssuer = "https://lfs.test", ValidAudience = RepositoryGrant.StorageAudience,
            IssuerSigningKey = new RsaSecurityKey(rsa), ClockSkew = TimeSpan.Zero
        }, out var validated);
        var jwt = (JwtSecurityToken)validated;
        Assert.Equal("lfs-repository-read", jwt.Claims.Single(x => x.Type == "seaweedfs_policies").Value);
        Assert.Equal("AppMana/lbxx/", jwt.Claims.Single(x => x.Type == "storage_prefix").Value);
        Assert.Equal(id.ToString(), jwt.Claims.Single(x => x.Type == "repository_id").Value);
        Assert.InRange(jwt.ValidTo - DateTime.UtcNow, TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(5));
        Assert.DoesNotContain("private", JsonSerializer.Serialize(issuer.Jwks));
    }
}

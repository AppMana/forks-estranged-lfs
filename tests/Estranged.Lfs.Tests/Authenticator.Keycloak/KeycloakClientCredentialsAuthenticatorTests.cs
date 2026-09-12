using Estranged.Lfs.Authenticator.Keycloak;
using Estranged.Lfs.Data;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Xunit;

namespace Estranged.Lfs.Tests.Authenticator.Keycloak
{
    public class KeycloakClientCredentialsAuthenticatorTests
    {
        private sealed class RecordingHandler : HttpMessageHandler
        {
            public List<Dictionary<string, string>> Forms { get; } = new();
            public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
            public string AccessToken { get; set; } = Jwt(new Dictionary<string, object> { ["realm_access"] = new Dictionary<string, object> { ["roles"] = new[] { "lfs" } } });

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Assert.Equal("https://login.example/realms/appmana/protocol/openid-connect/token", request.RequestUri.ToString());
                string body = await request.Content.ReadAsStringAsync(cancellationToken);
                var form = new Dictionary<string, string>();
                var parsed = HttpUtility.ParseQueryString(body);
                foreach (string key in parsed.AllKeys)
                {
                    form[key] = parsed[key];
                }
                Forms.Add(form);
                return new HttpResponseMessage(Status)
                {
                    Content = new StringContent($"{{\"access_token\":\"{AccessToken}\"}}", Encoding.UTF8, "application/json"),
                };
            }
        }

        private static string Base64Url(string value) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        private static string Jwt(Dictionary<string, object> claims) =>
            $"{Base64Url("{\"alg\":\"RS256\"}")}.{Base64Url(System.Text.Json.JsonSerializer.Serialize(claims))}.signature";

        private const string Subject = "system:serviceaccount:workspace-build:game-build-execution";

        private static string Assertion(string subject = Subject) =>
            Jwt(new Dictionary<string, object> { ["sub"] = subject, ["iss"] = "https://kubernetes.default.svc" });

        private static (KeycloakClientCredentialsAuthenticator Authenticator, RecordingHandler Handler) Create()
        {
            var handler = new RecordingHandler();
            var authenticator = new KeycloakClientCredentialsAuthenticator(new HttpClient(handler), new KeycloakAuthenticatorConfig
            {
                RealmUrl = "https://login.example/realms/appmana/",
            });
            return (authenticator, handler);
        }

        [Fact]
        public async Task SecretModePostsTheRepositoryClientSecret()
        {
            var (authenticator, handler) = Create();

            await authenticator.Authenticate("t", "repository-secret", "AppMana", "game", LfsPermission.Read, CancellationToken.None);

            var form = Assert.Single(handler.Forms);
            Assert.Equal("client_credentials", form["grant_type"]);
            Assert.Equal("git-lfs-AppMana-game", form["client_id"]);
            Assert.Equal("repository-secret", form["client_secret"]);
            Assert.False(form.ContainsKey("client_assertion"));
        }

        [Fact]
        public async Task AssertionModePostsTheWorkloadTokenForTheSubjectBoundClient()
        {
            var (authenticator, handler) = Create();
            string assertion = Assertion();

            await authenticator.Authenticate("assertion", assertion, "AppMana", "game", LfsPermission.Write, CancellationToken.None);

            var form = Assert.Single(handler.Forms);
            Assert.Equal("client_credentials", form["grant_type"]);
            Assert.Equal("git-lfs-AppMana-game@" + Subject, form["client_id"]);
            Assert.Equal("urn:ietf:params:oauth:client-assertion-type:jwt-bearer", form["client_assertion_type"]);
            Assert.Equal(assertion, form["client_assertion"]);
            Assert.False(form.ContainsKey("client_secret"));
        }

        [Fact]
        public void TheBuildClientIdIsExactForRouteAndSubject()
        {
            string game = KeycloakClientCredentialsAuthenticator.AssertionClientId("git-lfs-", "AppMana", "game", Subject);
            string other = KeycloakClientCredentialsAuthenticator.AssertionClientId("git-lfs-", "AppMana", "game-other", Subject);

            Assert.Equal("git-lfs-AppMana-game@" + Subject, game);
            Assert.NotEqual(game, other);
            Assert.False(other.StartsWith(game, StringComparison.Ordinal));
        }

        [Fact]
        public async Task AssertionWithoutSubjectIsRejectedBeforeKeycloak()
        {
            var (authenticator, handler) = Create();
            string assertion = Jwt(new Dictionary<string, object> { ["iss"] = "https://kubernetes.default.svc" });

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                authenticator.Authenticate("assertion", assertion, "AppMana", "game", LfsPermission.Read, CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                authenticator.Authenticate("assertion", "not-a-jwt", "AppMana", "game", LfsPermission.Read, CancellationToken.None));

            Assert.Empty(handler.Forms);
        }

        [Fact]
        public async Task AssertionRejectedByKeycloakIsUnauthorised()
        {
            var (authenticator, handler) = Create();
            handler.Status = HttpStatusCode.Unauthorized;

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                authenticator.Authenticate("assertion", Assertion(), "AppMana", "game", LfsPermission.Read, CancellationToken.None));
        }

        [Fact]
        public async Task TokenWithoutTheRealmRoleIsForbidden()
        {
            var (authenticator, handler) = Create();
            handler.AccessToken = Jwt(new Dictionary<string, object> { ["realm_access"] = new Dictionary<string, object> { ["roles"] = new[] { "other" } } });

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                authenticator.Authenticate("assertion", Assertion(), "AppMana", "game", LfsPermission.Read, CancellationToken.None));
        }

        [Fact]
        public async Task SuccessfulAssertionsAreCachedPerToken()
        {
            var (authenticator, handler) = Create();
            string assertion = Assertion();

            await authenticator.Authenticate("assertion", assertion, "AppMana", "game", LfsPermission.Read, CancellationToken.None);
            await authenticator.Authenticate("assertion", assertion, "AppMana", "game", LfsPermission.Read, CancellationToken.None);
            await authenticator.Authenticate("assertion", Assertion("system:serviceaccount:workspace-build:other-build-execution"), "AppMana", "game", LfsPermission.Read, CancellationToken.None);

            Assert.Equal(2, handler.Forms.Count);
        }
    }
}

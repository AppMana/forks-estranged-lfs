using Estranged.Lfs.Data;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Estranged.Lfs.Authenticator.Keycloak
{
    internal sealed class KeycloakClientCredentialsAuthenticator : IAuthenticator
    {
        private sealed class CacheEntry
        {
            public DateTimeOffset ExpiresAt { get; init; }
        }

        private readonly HttpClient httpClient;
        private readonly IKeycloakAuthenticatorConfig config;
        private readonly ConcurrentDictionary<string, CacheEntry> cache = new();

        public KeycloakClientCredentialsAuthenticator(HttpClient httpClient, IKeycloakAuthenticatorConfig config)
        {
            this.httpClient = httpClient;
            this.config = config;
        }

        public async Task Authenticate(string username, string password, string organisation, string repository, LfsPermission requiredPermission, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(organisation) || string.IsNullOrWhiteSpace(repository))
            {
                throw new InvalidOperationException("LFS route did not include organisation and repository.");
            }

            if (string.IsNullOrWhiteSpace(password))
            {
                throw new InvalidOperationException("No Keycloak credential was supplied.");
            }

            string repositoryClientId = $"{config.ClientPrefix}{organisation}-{repository}";
            string clientId;
            Dictionary<string, string> grant;
            if (username == config.AssertionUsername)
            {
                // The password is a Kubernetes ServiceAccount token. Keycloak
                // verifies it against the cluster issuer and only accepts it
                // for the client bound to exactly this subject, so the subject
                // read here is only used to name that client.
                string subject = SubjectClaim(password);
                if (string.IsNullOrWhiteSpace(subject))
                {
                    throw new InvalidOperationException("Workload assertion does not carry a subject.");
                }

                clientId = AssertionClientId(config.ClientPrefix, organisation, repository, subject);
                grant = new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = clientId,
                    ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
                    ["client_assertion"] = password,
                };
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(username) && username != "x" && username != "t" && username != repositoryClientId)
                {
                    throw new UnauthorizedAccessException($"Credential username {username} does not match {repositoryClientId}.");
                }

                clientId = repositoryClientId;
                grant = new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = clientId,
                    ["client_secret"] = password,
                };
            }

            string cacheKey = $"{clientId}:{Hash(password)}";
            if (cache.TryGetValue(cacheKey, out CacheEntry cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
            {
                return;
            }

            using var response = await httpClient.PostAsync(
                $"{config.RealmUrl.TrimEnd('/')}/protocol/openid-connect/token",
                new FormUrlEncodedContent(grant),
                token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Keycloak rejected client credentials for {clientId}: {(int)response.StatusCode}");
            }

            using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
            string accessToken = doc.RootElement.GetProperty("access_token").GetString();
            if (!HasRealmRole(accessToken, config.RequiredRole))
            {
                throw new UnauthorizedAccessException($"Keycloak token for {clientId} is missing realm role {config.RequiredRole}.");
            }

            cache[cacheKey] = new CacheEntry { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(30) };
        }

        /// <summary>
        /// The client bound to one workload identity for one repository. The
        /// subject separator cannot occur in an organisation or repository
        /// name, so no client id for another route is a prefix of this one.
        /// </summary>
        public static string AssertionClientId(string prefix, string organisation, string repository, string subject) =>
            $"{prefix}{organisation}-{repository}@{subject}";

        private static string SubjectClaim(string jwt)
        {
            JsonElement? claims = Claims(jwt);
            if (claims is null || !claims.Value.TryGetProperty("sub", out JsonElement subject) || subject.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            return subject.GetString();
        }

        private static JsonElement? Claims(string jwt)
        {
            if (string.IsNullOrWhiteSpace(jwt))
            {
                return null;
            }

            string[] parts = jwt.Split('.');
            if (parts.Length < 2)
            {
                return null;
            }

            try
            {
                using JsonDocument payload = JsonDocument.Parse(Base64UrlDecode(parts[1]));
                return payload.RootElement.Clone();
            }
            catch (Exception exception) when (exception is FormatException || exception is JsonException)
            {
                return null;
            }
        }

        private static string Hash(string value)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
            return Convert.ToHexString(hash);
        }

        private static bool HasRealmRole(string jwt, string role)
        {
            JsonElement? claims = Claims(jwt);
            if (claims is null ||
                !claims.Value.TryGetProperty("realm_access", out JsonElement realmAccess) ||
                !realmAccess.TryGetProperty("roles", out JsonElement roles) ||
                roles.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (JsonElement candidate in roles.EnumerateArray())
            {
                if (candidate.GetString() == role)
                {
                    return true;
                }
            }

            return false;
        }

        private static byte[] Base64UrlDecode(string value)
        {
            string padded = value.Replace('-', '+').Replace('_', '/');
            switch (padded.Length % 4)
            {
                case 2:
                    padded += "==";
                    break;
                case 3:
                    padded += "=";
                    break;
            }
            return Convert.FromBase64String(padded);
        }
    }
}

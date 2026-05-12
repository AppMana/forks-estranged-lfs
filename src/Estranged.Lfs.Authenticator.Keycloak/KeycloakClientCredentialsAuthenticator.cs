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

            string expectedClientId = $"{config.ClientPrefix}{organisation}-{repository}";
            if (!string.IsNullOrWhiteSpace(username) && username != "x" && username != "t" && username != expectedClientId)
            {
                throw new UnauthorizedAccessException($"Credential username {username} does not match {expectedClientId}.");
            }

            if (string.IsNullOrWhiteSpace(password))
            {
                throw new InvalidOperationException("No Keycloak client secret was supplied.");
            }

            string cacheKey = $"{expectedClientId}:{Hash(password)}";
            if (cache.TryGetValue(cacheKey, out CacheEntry cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
            {
                return;
            }

            using var response = await httpClient.PostAsync(
                $"{config.RealmUrl.TrimEnd('/')}/protocol/openid-connect/token",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = expectedClientId,
                    ["client_secret"] = password,
                }),
                token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Keycloak rejected client credentials for {expectedClientId}: {(int)response.StatusCode}");
            }

            using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
            string accessToken = doc.RootElement.GetProperty("access_token").GetString();
            if (!HasRealmRole(accessToken, config.RequiredRole))
            {
                throw new UnauthorizedAccessException($"Keycloak token for {expectedClientId} is missing realm role {config.RequiredRole}.");
            }

            cache[cacheKey] = new CacheEntry { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(30) };
        }

        private static string Hash(string value)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
            return Convert.ToHexString(hash);
        }

        private static bool HasRealmRole(string jwt, string role)
        {
            if (string.IsNullOrWhiteSpace(jwt))
            {
                return false;
            }

            string[] parts = jwt.Split('.');
            if (parts.Length < 2)
            {
                return false;
            }

            byte[] payloadBytes = Base64UrlDecode(parts[1]);
            using JsonDocument payload = JsonDocument.Parse(payloadBytes);

            if (!payload.RootElement.TryGetProperty("realm_access", out JsonElement realmAccess) ||
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

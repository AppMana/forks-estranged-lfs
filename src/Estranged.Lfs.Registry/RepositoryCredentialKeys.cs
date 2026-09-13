using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Estranged.Lfs.Registry
{
    // Stable, reissuable repository credentials without storing plaintext in
    // PostgreSQL. Keep previous key versions during deliberate key rotation.
    public sealed class RepositoryCredentialKeys
    {
        public string ActiveVersion { get; }
        readonly Dictionary<string, byte[]> keys = new();
        public RepositoryCredentialKeys(string document)
        {
            using var json = JsonDocument.Parse(document);
            ActiveVersion = json.RootElement.GetProperty("active").GetString();
            foreach (var item in json.RootElement.GetProperty("keys").EnumerateObject())
            {
                if (item.Name.Length == 0 || item.Name.Length > 32 || !System.Text.RegularExpressions.Regex.IsMatch(item.Name, "^[a-zA-Z0-9_-]+$")) throw new ArgumentException("Invalid credential key version.");
                var key = Convert.FromBase64String(item.Value.GetString());
                if (key.Length < 32) throw new ArgumentException("Credential key must have at least 256 bits.");
                keys.Add(item.Name, key);
            }
            if (!keys.ContainsKey(ActiveVersion)) throw new ArgumentException("Active credential key is absent.");
        }
        public string Encode(RepositoryCredential credential)
        {
            var payload = "lfs1:" + credential.RepositoryId.ToString("D") + ":" + credential.Id.ToString("D") + ":" + credential.KeyVersion;
            var digest = HMACSHA256.HashData(keys[credential.KeyVersion], Encoding.UTF8.GetBytes(payload));
            return "lfs1." + credential.Id.ToString("N") + "." + credential.KeyVersion + "." + Convert.ToBase64String(digest).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
        public bool Verify(RepositoryCredential credential, string supplied)
        {
            if (!credential.Enabled || !keys.ContainsKey(credential.KeyVersion)) return false;
            var expected = Encoding.UTF8.GetBytes(Encode(credential));
            var actual = Encoding.UTF8.GetBytes(supplied);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        public static Guid? CredentialId(string supplied)
        {
            var parts = supplied?.Split('.');
            return parts?.Length == 4 && parts[0] == "lfs1" && Guid.TryParseExact(parts[1], "N", out var id) ? id : null;
        }
    }
}

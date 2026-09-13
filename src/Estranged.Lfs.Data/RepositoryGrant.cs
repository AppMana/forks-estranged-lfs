using System;
using System.Text.RegularExpressions;

namespace Estranged.Lfs.Data
{
    // Request-scoped, populated only after the identity provider authenticates
    // the caller. Never populate this from HTTP headers or a decoded caller JWT.
    public sealed class RepositoryGrant
    {
        public const string StorageAudience = "git-lfs-storage";
        public const string StoragePolicy = "lfs-repository-rw";
        public string RepositoryId { get; set; }
        public string StoragePrefix { get; set; }
        public string AccessToken { get; set; }

        public static bool IsRepositoryId(string value) =>
            value != null && Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty && id.ToString("D") == value;

        public static bool IsStoragePrefix(string value) =>
            value != null && Regex.IsMatch(value, @"\A[A-Za-z0-9_-]+(?:/[A-Za-z0-9_-]+)*/\z");
    }
}

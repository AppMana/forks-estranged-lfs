using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Authentication;
using Estranged.Lfs.Data;
using Microsoft.EntityFrameworkCore;

namespace Estranged.Lfs.Registry
{
    public sealed record VerifiedIdentity(Principal Principal, DateTime ExpiresAt);
    public interface IIdentityVerifier
    {
        Task<VerifiedIdentity> Verify(string assertion, CancellationToken token);
    }
    public interface IStorageTokenIssuer
    {
        string Issue(Guid repositoryId, string storagePrefix, bool write, DateTime expiresAt);
    }
    public sealed class RepositoryAuthenticator : IRepositoryAuthenticator
    {
        readonly RepositoryDbContext db;
        readonly RepositoryCredentialKeys keys;
        readonly IIdentityVerifier identities;
        readonly IStorageTokenIssuer issuer;
        readonly RepositoryGrant grant;
        public RepositoryAuthenticator(RepositoryDbContext db, RepositoryCredentialKeys keys, IIdentityVerifier identities, IStorageTokenIssuer issuer, RepositoryGrant grant)
        { this.db = db; this.keys = keys; this.identities = identities; this.issuer = issuer; this.grant = grant; }
        public async Task Authenticate(string authorization, string repositoryId, LfsPermission permission, CancellationToken token)
        {
            if (!RepositoryGrant.IsRepositoryId(repositoryId)) throw new AuthenticationException("Invalid repository.");
            var id = Guid.Parse(repositoryId);
            var repository = await db.Repositories.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.Enabled, token);
            if (repository == null || !RepositoryGrant.IsStoragePrefix(repository.StoragePrefix)) throw new UnauthorizedAccessException();
            string assertion = null;
            DateTime expires = DateTime.UtcNow.AddMinutes(5);
            var write = (permission & LfsPermission.Write) != 0;
            if (authorization?.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase) == true)
            {
                string basic;
                try { basic = Encoding.UTF8.GetString(Convert.FromBase64String(authorization[6..].Trim())); }
                catch (FormatException) { throw new AuthenticationException("Invalid credentials."); }
                int separator = basic.IndexOf(':');
                if (separator < 1) throw new AuthenticationException("Invalid credentials.");
                string user = basic[..separator], password = basic[(separator + 1)..];
                if (user == "t")
                {
                    var credentialId = RepositoryCredentialKeys.CredentialId(password);
                    var credential = await db.Credentials.AsNoTracking().SingleOrDefaultAsync(x => x.Id == credentialId && x.RepositoryId == id, token);
                    if (credential == null || !keys.Verify(credential, password)) throw new AuthenticationException("Invalid credentials.");
                }
                else if (user == "assertion") assertion = password;
                else throw new AuthenticationException("Invalid credentials.");
            }
            else if (authorization?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true) assertion = authorization[7..].Trim();
            else throw new AuthenticationException("Credentials required.");
            if (assertion != null)
            {
                var identity = await identities.Verify(assertion, token);
                identity.Principal.Validate();
                if (identity.ExpiresAt <= DateTime.UtcNow) throw new AuthenticationException("Expired identity.");
                var required = write ? 2 : 1;
                if (!await db.Grants.AsNoTracking().AnyAsync(x => x.RepositoryId == id && x.PrincipalKey == identity.Principal.Key && x.Permission >= required, token)) throw new UnauthorizedAccessException();
                expires = identity.ExpiresAt < expires ? identity.ExpiresAt : expires;
            }
            // The validated binding is the sole source of both the token scope
            // and the adapter's object prefix. Never accept either from callers.
            grant.RepositoryId = repositoryId;
            grant.StoragePrefix = repository.StoragePrefix;
            grant.AccessToken = issuer.Issue(id, repository.StoragePrefix, write, expires);
        }
    }
}

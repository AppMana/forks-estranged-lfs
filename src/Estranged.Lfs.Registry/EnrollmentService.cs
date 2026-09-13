using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace Estranged.Lfs.Registry
{
    public sealed record Principal(string Issuer, string Subject)
    {
        public string Key => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Issuer + "\0" + Subject))).ToLowerInvariant();
        public void Validate()
        {
            if (!Uri.TryCreate(Issuer, UriKind.Absolute, out var uri) || uri.Scheme != "https" || Issuer.Length > 512 ||
                string.IsNullOrWhiteSpace(Subject) || Subject.Length > 512 || Subject.Contains('\0') || Issuer.Contains('\0'))
                throw new ArgumentException("A canonical issuer and subject are required.");
        }
    }
    public sealed record DesiredGrant(string Issuer, string Subject, string Permission);
    public sealed record EnrollmentRequest(string DisplayName, string ManagerKey, DesiredGrant[] Grants, bool RepositoryCredential = false);
    public sealed record EnrollmentResult(Guid Id, string DisplayName, string Url, string Credential);
    public sealed class RegistryDeniedException : Exception { public RegistryDeniedException() : base("Repository access denied.") { } }

    public sealed class EnrollmentService
    {
        readonly RepositoryDbContext db;
        readonly RepositoryCredentialKeys keys;
        readonly string baseUrl;
        public EnrollmentService(RepositoryDbContext db, RepositoryCredentialKeys keys, string baseUrl)
        { this.db = db; this.keys = keys; this.baseUrl = baseUrl.TrimEnd('/'); }
        public static int Permission(string value) => value switch
        { "read" => 1, "write" => 2, "manage" => 3, _ => throw new ArgumentException("Permission must be read, write or manage.") };
        static void Key(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(c => char.IsControl(c)))
                throw new ArgumentException("A nonempty key of at most 128 characters is required.");
        }
        static void Validate(EnrollmentRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Length > 256 || request.Grants == null || request.Grants.Length > 100)
                throw new ArgumentException("Invalid enrollment request.");
            Key(request.ManagerKey);
            var identities = new HashSet<string>();
            foreach (var grant in request.Grants)
            {
                var principal = new Principal(grant.Issuer, grant.Subject); principal.Validate(); Permission(grant.Permission);
                if (!identities.Add(principal.Key)) throw new ArgumentException("Duplicate grant principal.");
            }
        }
        public async Task<bool> CanManage(Principal actor, Guid id, CancellationToken token) =>
            await db.Enrollments.AnyAsync(x => x.RepositoryId == id && x.OwnerKey == actor.Key, token) ||
            await db.Grants.AnyAsync(x => x.RepositoryId == id && x.PrincipalKey == actor.Key && x.Permission >= 3, token);

        public async Task<EnrollmentResult> Reconcile(Principal actor, string key, EnrollmentRequest request, CancellationToken token)
        {
            actor.Validate(); Key(key); Validate(request);
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
            // Serialize control-plane writes, including migration imports. This
            // avoids duplicate UUIDs and lost updates across gateway replicas.
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(624198501)", token);
            if (!await db.Provisioners.AnyAsync(x => x.PrincipalKey == actor.Key && x.Enabled, token)) throw new RegistryDeniedException();
            var enrollment = await db.Enrollments.Include(x => x.Repository).SingleOrDefaultAsync(x => x.OwnerKey == actor.Key && x.Key == key, token);
            if (enrollment == null)
            {
                var id = Guid.NewGuid();
                var prefix = "r/" + id.ToString("D") + "/";
                var existingPrefixes = await db.Repositories.Select(x => x.StoragePrefix).ToListAsync(token);
                if (existingPrefixes.Any(x => prefix.StartsWith(x, StringComparison.Ordinal) || x.StartsWith(prefix, StringComparison.Ordinal)))
                    throw new RegistryDeniedException();
                var repository = new LfsRepository { Id = id, DisplayName = request.DisplayName, StoragePrefix = "r/" + id.ToString("D") + "/", Enabled = true, CreatedAt = DateTime.UtcNow };
                enrollment = new Enrollment { OwnerKey = actor.Key, Key = key, Repository = repository, RepositoryId = id };
                db.Enrollments.Add(enrollment);
            }
            if (!enrollment.Repository.Enabled) throw new RegistryDeniedException();
            enrollment.Repository.DisplayName = request.DisplayName;
            await ReplaceGrants(actor, enrollment.RepositoryId, request.ManagerKey, request.Grants, token);
            var credential = request.RepositoryCredential ? await Credential(actor, enrollment.RepositoryId, token) : null;
            db.Audit.Add(new RegistryAudit { At = DateTime.UtcNow, RepositoryId = enrollment.RepositoryId, ActorKey = actor.Key, Operation = "reconcile", ManagerKey = request.ManagerKey });
            await db.SaveChangesAsync(token); await transaction.CommitAsync(token);
            return new EnrollmentResult(enrollment.RepositoryId, request.DisplayName, baseUrl + "/r/" + enrollment.RepositoryId.ToString("D"), credential);
        }
        public async Task ReconcileGrants(Principal actor, Guid id, string managerKey, DesiredGrant[] grants, CancellationToken token)
        {
            actor.Validate(); Validate(new EnrollmentRequest("grants", managerKey, grants));
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(624198501)", token);
            if (!await CanManage(actor, id, token)) throw new RegistryDeniedException();
            await ReplaceGrants(actor, id, managerKey, grants, token);
            db.Audit.Add(new RegistryAudit { At = DateTime.UtcNow, RepositoryId = id, ActorKey = actor.Key, Operation = "reconcile-grants", ManagerKey = managerKey });
            await db.SaveChangesAsync(token); await transaction.CommitAsync(token);
        }
        async Task ReplaceGrants(Principal actor, Guid id, string manager, DesiredGrant[] desired, CancellationToken token)
        {
            var existing = await db.Grants.Where(x => x.RepositoryId == id && x.ControllerKey == actor.Key && x.ManagerKey == manager).ToListAsync(token);
            foreach (var item in existing)
                if (!desired.Any(x => new Principal(x.Issuer, x.Subject).Key == item.PrincipalKey)) db.Grants.Remove(item);
            foreach (var item in desired)
            {
                var principal = new Principal(item.Issuer, item.Subject);
                var grant = existing.SingleOrDefault(x => x.PrincipalKey == principal.Key);
                if (grant == null)
                {
                    grant = new AccessGrant { RepositoryId = id, ControllerKey = actor.Key, ManagerKey = manager, PrincipalKey = principal.Key, Issuer = item.Issuer, Subject = item.Subject };
                    db.Grants.Add(grant);
                }
                grant.Permission = Permission(item.Permission);
            }
        }
        async Task<string> Credential(Principal actor, Guid id, CancellationToken token)
        {
            var credential = await db.Credentials.SingleOrDefaultAsync(x => x.RepositoryId == id && x.OwnerKey == actor.Key, token);
            if (credential == null)
            {
                credential = new RepositoryCredential { Id = Guid.NewGuid(), RepositoryId = id, OwnerKey = actor.Key, KeyVersion = keys.ActiveVersion, Enabled = true };
                db.Credentials.Add(credential);
            }
            // A normal reconcile must not undo an explicit revocation.
            if (!credential.Enabled) throw new RegistryDeniedException();
            return keys.Encode(credential);
        }
        public async Task RevokeCredential(Principal actor, Guid id, CancellationToken token)
        {
            actor.Validate();
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(624198501)", token);
            if (!await CanManage(actor, id, token)) throw new RegistryDeniedException();
            var credentials = await db.Credentials.Where(x => x.RepositoryId == id).ToListAsync(token);
            foreach (var credential in credentials) credential.Enabled = false;
            db.Audit.Add(new RegistryAudit { At = DateTime.UtcNow, RepositoryId = id, ActorKey = actor.Key, Operation = "revoke-credentials", ManagerKey = "all" });
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
        }
    }
}

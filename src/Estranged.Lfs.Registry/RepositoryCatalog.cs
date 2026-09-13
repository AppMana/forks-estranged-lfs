using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Estranged.Lfs.Data;
using Microsoft.EntityFrameworkCore;

namespace Estranged.Lfs.Registry
{
    public sealed record CatalogSource(string Provider, string Id);
    public sealed record CatalogRepository(string Id, string DisplayName, string StoragePrefix, bool Enabled, CatalogSource[] Sources, Principal Owner, string EnrollmentKey);
    public sealed record CatalogDocument(int Version, CatalogRepository[] Repositories, Principal[] Provisioners);

    public static class RepositoryCatalog
    {
        public static CatalogDocument Parse(string json)
        {
            var document = JsonSerializer.Deserialize<CatalogDocument>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (document?.Version != 1 || document.Repositories == null || document.Provisioners == null) throw new ArgumentException("Unsupported repository catalog.");
            foreach (var principal in document.Provisioners) principal.Validate();
            var ids = new HashSet<string>(); var enrollments = new HashSet<(string, string)>(); var prefixes = new List<string>();
            foreach (var item in document.Repositories)
            {
                item.Owner.Validate();
                if (string.IsNullOrWhiteSpace(item.EnrollmentKey) || item.EnrollmentKey.Length > 128 || item.EnrollmentKey.Any(char.IsControl) || !enrollments.Add((item.Owner.Key, item.EnrollmentKey)))
                    throw new ArgumentException("Invalid migration enrollment key.");
                if (!RepositoryGrant.IsRepositoryId(item.Id) || !ids.Add(item.Id) || string.IsNullOrWhiteSpace(item.DisplayName) || item.DisplayName.Length > 256 ||
                    !RepositoryGrant.IsStoragePrefix(item.StoragePrefix) || item.StoragePrefix.Length > 512 ||
                    prefixes.Any(p => p.StartsWith(item.StoragePrefix, StringComparison.Ordinal) || item.StoragePrefix.StartsWith(p, StringComparison.Ordinal)) ||
                    item.Sources == null)
                    throw new ArgumentException("Invalid or overlapping repository binding.");
                prefixes.Add(item.StoragePrefix);
                foreach (var source in item.Sources)
                    if (string.IsNullOrWhiteSpace(source.Provider) || source.Provider.Length > 128 || string.IsNullOrWhiteSpace(source.Id) || source.Id.Length > 256)
                        throw new ArgumentException("Source metadata must be nonempty.");
            }
            return document;
        }

        // GitOps supplies reviewed imports. No public API claims ownership of
        // an existing UUID, changes its storage directory, or deletes mappings.
        public static async Task Import(RepositoryDbContext db, CatalogDocument document, CancellationToken token)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(624198501)", token);
            // Imports share the control API lock and are deliberately explicit;
            // they cannot change a UUID's existing storage or enrollment owner.
            foreach (var principal in document.Provisioners)
            {
                var provisioner = await db.Provisioners.FindAsync(new object[] { principal.Key }, token);
                if (provisioner == null) db.Provisioners.Add(new Provisioner { PrincipalKey = principal.Key, Issuer = principal.Issuer, Subject = principal.Subject, Enabled = true });
            }
            var existing = await db.Repositories.Include(x => x.Sources).ToListAsync(token);
            foreach (var item in document.Repositories)
            {
                var id = Guid.Parse(item.Id);
                var repository = existing.SingleOrDefault(x => x.Id == id);
                if (repository != null && repository.StoragePrefix != item.StoragePrefix)
                    throw new InvalidOperationException("A repository's storage binding is immutable.");
                if (existing.Any(x => x.Id != id && (x.StoragePrefix.StartsWith(item.StoragePrefix, StringComparison.Ordinal) || item.StoragePrefix.StartsWith(x.StoragePrefix, StringComparison.Ordinal))))
                    throw new InvalidOperationException("Repository storage boundaries overlap.");
                if (repository == null)
                {
                    repository = new LfsRepository { Id = id, StoragePrefix = item.StoragePrefix, CreatedAt = DateTime.UtcNow };
                    db.Repositories.Add(repository); existing.Add(repository);
                }
                var enrollment = await db.Enrollments.SingleOrDefaultAsync(x => x.OwnerKey == item.Owner.Key && x.Key == item.EnrollmentKey, token);
                if (enrollment != null && enrollment.RepositoryId != id)
                    throw new InvalidOperationException("An enrollment cannot be reassigned to another UUID.");
                if (enrollment == null)
                {
                    if (await db.Enrollments.AnyAsync(x => x.RepositoryId == id, token))
                        throw new InvalidOperationException("An imported UUID already has a different owner or enrollment key.");
                    db.Enrollments.Add(new Enrollment { OwnerKey = item.Owner.Key, Key = item.EnrollmentKey, RepositoryId = id, Repository = repository });
                }
                repository.DisplayName = item.DisplayName;
                repository.Enabled = item.Enabled;
                foreach (var source in item.Sources)
                {
                    var mapping = repository.Sources.SingleOrDefault(x => x.Provider == source.Provider && x.Id == source.Id);
                    if (mapping == null) repository.Sources.Add(new RepositorySource { Provider = source.Provider, Id = source.Id, RepositoryId = id });
                }
            }
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
        }
    }

}

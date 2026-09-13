using System.Security.Authentication;
using System.Text;
using Estranged.Lfs.Data;
using Estranged.Lfs.Registry;
using Microsoft.EntityFrameworkCore;
using Moq;
using Npgsql;
using Xunit;

namespace Estranged.Lfs.Registry.Tests;

// A real PostgreSQL database per test exercises transaction locks, unique
// indexes, migration history and rollback behavior that an in-memory fake omits.
public sealed class RegistryTests : IAsyncLifetime
{
    string connection;
    readonly string database = "lfs_test_" + Guid.NewGuid().ToString("N");
    readonly Principal owner = new("https://issuer.test", "controller-a");
    readonly Principal other = new("https://issuer.test", "controller-b");
    readonly Principal workload = new("https://issuer.test", "workload");
    static readonly RepositoryCredentialKeys Keys = new("{\"active\":\"v1\",\"keys\":{\"v1\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=\"}}");
    string Admin => Environment.GetEnvironmentVariable("LFS_TEST_POSTGRES") ?? throw new InvalidOperationException("Run scripts/test-registry.sh or set LFS_TEST_POSTGRES to a disposable PostgreSQL server.");
    RepositoryDbContext Db() => new(new DbContextOptionsBuilder<RepositoryDbContext>().UseNpgsql(connection).Options);
    EnrollmentService Service(RepositoryDbContext db) => new(db, Keys, "https://lfs.test");
    EnrollmentRequest Request(string manager = "project-a", string permission = "write", bool credential = true) => new("a repository", manager, new[] { new DesiredGrant(workload.Issuer, workload.Subject, permission) }, credential);
    public async Task InitializeAsync()
    {
        await using var admin = new NpgsqlConnection(Admin); await admin.OpenAsync();
        await using (var command = new NpgsqlCommand($"CREATE DATABASE {database}", admin)) await command.ExecuteNonQueryAsync();
        connection = new NpgsqlConnectionStringBuilder(Admin) { Database = database }.ConnectionString;
        await using var db = Db(); await db.Database.MigrateAsync();
        foreach (var principal in new[] { owner, other }) db.Provisioners.Add(new Provisioner { PrincipalKey = principal.Key, Issuer = principal.Issuer, Subject = principal.Subject, Enabled = true });
        await db.SaveChangesAsync();
    }
    public async Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await using var admin = new NpgsqlConnection(Admin); await admin.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE {database} WITH (FORCE)", admin); await command.ExecuteNonQueryAsync();
    }
    [Fact] public async Task MigrationCanRunTwiceWithoutLosingData()
    {
        await using var db = Db(); var enrolled = await Service(db).Reconcile(owner, "external-id", Request(), default);
        await db.Database.MigrateAsync();
        Assert.Single(await db.Database.GetAppliedMigrationsAsync());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Equal(enrolled.Id, (await db.Repositories.SingleAsync()).Id);
    }
    [Fact] public async Task ConcurrentReconciliationsReturnOneUuidAndCredential()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var db = Db(); return await Service(db).Reconcile(owner, "stable-key", Request(), default);
        }));
        Assert.Single(results.Select(x => x.Id).Distinct()); Assert.Single(results.Select(x => x.Credential).Distinct());
        await using var check = Db(); Assert.Single(await check.Repositories.ToListAsync()); Assert.Single(await check.Grants.ToListAsync());
    }
    [Fact] public async Task SameExternalKeyAndDisplayNameDoNotConferOwnership()
    {
        await using var db = Db();
        var a = await Service(db).Reconcile(owner, "same-key", Request(), default);
        var b = await Service(db).Reconcile(other, "same-key", Request(), default);
        Assert.NotEqual(a.Id, b.Id);
        await Assert.ThrowsAsync<RegistryDeniedException>(() => Service(db).ReconcileGrants(other, a.Id, "project-a", Array.Empty<DesiredGrant>(), default));
    }
    [Fact] public async Task ReconciliationPreservesOtherManagersGrants()
    {
        await using var db = Db(); var service = Service(db);
        var enrolled = await service.Reconcile(owner, "repo", Request("first"), default);
        await service.Reconcile(owner, "repo", Request("second", "read"), default);
        await service.ReconcileGrants(owner, enrolled.Id, "first", Array.Empty<DesiredGrant>(), default);
        var grant = await db.Grants.SingleAsync(); Assert.Equal("second", grant.ManagerKey); Assert.Equal(1, grant.Permission);
    }
    [Fact] public async Task ReconciliationPreservesOtherControllersGrants()
    {
        await using var db = Db(); var service = Service(db);
        var enrolled = await service.Reconcile(owner, "repo", new("repo", "owner", new[] { new DesiredGrant(other.Issuer, other.Subject, "manage") }), default);
        await service.ReconcileGrants(other, enrolled.Id, "shared-name", Request().Grants, default);
        await service.ReconcileGrants(owner, enrolled.Id, "shared-name", Array.Empty<DesiredGrant>(), default);
        Assert.True(await db.Grants.AnyAsync(x => x.ControllerKey == other.Key && x.PrincipalKey == workload.Key));
    }
    [Fact] public async Task RevocationSurvivesReconcileAndFailedTransactionRollsBackGrantChanges()
    {
        Guid id;
        await using (var db = Db()) { var service = Service(db); id = (await service.Reconcile(owner, "repo", Request(), default)).Id; await service.RevokeCredential(owner, id, default); }
        await using (var db = Db()) await Assert.ThrowsAsync<RegistryDeniedException>(() => Service(db).Reconcile(owner, "repo", Request(permission: "read"), default));
        await using var check = Db(); Assert.False((await check.Credentials.SingleAsync()).Enabled); Assert.Equal(2, (await check.Grants.SingleAsync()).Permission);
    }
    [Fact] public async Task UnapprovedProvisionerCannotAllocateStorage()
    {
        await using var db = Db();
        await Assert.ThrowsAsync<RegistryDeniedException>(() => Service(db).Reconcile(workload, "repo", Request(), default));
        Assert.Empty(await db.Repositories.ToListAsync());
    }
    [Fact] public async Task ValidIdentityStillRequiresRepositoryGrantAndReadCannotWrite()
    {
        await using var db = Db(); var enrolled = await Service(db).Reconcile(owner, "repo", Request(permission: "read"), default);
        var identities = new Mock<IIdentityVerifier>(MockBehavior.Strict);
        identities.Setup(x => x.Verify("assertion", It.IsAny<CancellationToken>())).ReturnsAsync(new VerifiedIdentity(workload, DateTime.UtcNow.AddMinutes(2)));
        var issuer = new Mock<IStorageTokenIssuer>(MockBehavior.Strict);
        issuer.Setup(x => x.Issue(enrolled.Id, "r/" + enrolled.Id + "/", false, It.IsAny<DateTime>())).Returns("scoped-token");
        var grant = new RepositoryGrant(); var auth = new RepositoryAuthenticator(db, Keys, identities.Object, issuer.Object, grant);
        await auth.Authenticate("Bearer assertion", enrolled.Id.ToString(), LfsPermission.Read, default);
        Assert.Equal("scoped-token", grant.AccessToken);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => auth.Authenticate("Bearer assertion", enrolled.Id.ToString(), LfsPermission.Write, default));
        var second = await Service(db).Reconcile(other, "another", new("another", "manager", Array.Empty<DesiredGrant>()), default);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => auth.Authenticate("Bearer assertion", second.Id.ToString(), LfsPermission.Read, default));
        issuer.Verify(x => x.Issue(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<DateTime>()), Times.Once);
    }
    [Fact] public async Task CredentialCannotBeReplayedAgainstAnotherUuidOrAfterRevocation()
    {
        await using var db = Db(); var a = await Service(db).Reconcile(owner, "a", Request(), default); var b = await Service(db).Reconcile(owner, "b", Request(), default);
        var issuer = new Mock<IStorageTokenIssuer>(MockBehavior.Strict);
        issuer.Setup(x => x.Issue(a.Id, "r/" + a.Id + "/", true, It.IsAny<DateTime>())).Returns("scoped");
        var identities = new Mock<IIdentityVerifier>(MockBehavior.Strict);
        var auth = new RepositoryAuthenticator(db, Keys, identities.Object, issuer.Object, new RepositoryGrant());
        var header = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("t:" + a.Credential));
        await auth.Authenticate(header, a.Id.ToString(), LfsPermission.Write, default);
        await Assert.ThrowsAsync<AuthenticationException>(() => auth.Authenticate(header, b.Id.ToString(), LfsPermission.Read, default));
        await Service(db).RevokeCredential(owner, a.Id, default);
        await Assert.ThrowsAsync<AuthenticationException>(() => auth.Authenticate(header, a.Id.ToString(), LfsPermission.Read, default));
        identities.VerifyNoOtherCalls();
    }
    CatalogDocument ImportDocument(Guid id, string prefix) => RepositoryCatalog.Parse(System.Text.Json.JsonSerializer.Serialize(
        new CatalogDocument(1, new[] { new CatalogRepository(id.ToString(), "legacy", prefix, true, Array.Empty<CatalogSource>(), owner, "import-key") }, new[] { owner })));
    [Fact] public async Task ImportKeepsLegacyStorageAndStableUuidAcrossReconcile()
    {
        var id = Guid.NewGuid();
        await using var db = Db();
        await RepositoryCatalog.Import(db, ImportDocument(id, "AppMana/lbxx/"), default);
        await RepositoryCatalog.Import(db, ImportDocument(id, "AppMana/lbxx/"), default);
        var result = await Service(db).Reconcile(owner, "import-key", Request(), default);
        Assert.Equal(id, result.Id); Assert.Equal("AppMana/lbxx/", (await db.Repositories.SingleAsync()).StoragePrefix);
    }
    [Fact] public async Task ImportCannotMoveStorageOrReassignEnrollment()
    {
        var id = Guid.NewGuid();
        await using (var db = Db()) await RepositoryCatalog.Import(db, ImportDocument(id, "AppMana/lbxx/"), default);
        await using (var db = Db()) await Assert.ThrowsAsync<InvalidOperationException>(() => RepositoryCatalog.Import(db, ImportDocument(id, "different/path/"), default));
        await using (var db = Db()) await Assert.ThrowsAsync<InvalidOperationException>(() => RepositoryCatalog.Import(db, ImportDocument(Guid.NewGuid(), "different/path/"), default));
        await using var check = Db(); Assert.Single(await check.Repositories.ToListAsync()); Assert.Equal(id, (await check.Enrollments.SingleAsync()).RepositoryId);
    }
    [Fact] public void ImportRejectsOverlappingStorageBoundaries()
    {
        var first = new CatalogRepository(Guid.NewGuid().ToString(), "one", "AppMana/lbxx/", true, Array.Empty<CatalogSource>(), owner, "one");
        var second = new CatalogRepository(Guid.NewGuid().ToString(), "two", "AppMana/lbxx/nested/", true, Array.Empty<CatalogSource>(), other, "two");
        Assert.Throws<ArgumentException>(() => RepositoryCatalog.Parse(System.Text.Json.JsonSerializer.Serialize(new CatalogDocument(1, new[] { first, second }, new[] { owner, other }))));
    }

    [Fact] public async Task MigrationExecutableAcceptsFlagsAndImportsWithoutWebSecrets()
    {
        var path = Path.GetTempFileName();
        try
        {
            var id = Guid.NewGuid();
            await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(ImportDocument(id, "legacy/objects/")));
            var start = new System.Diagnostics.ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            start.ArgumentList.Add(typeof(StorageTokenIssuer).Assembly.Location);
            start.ArgumentList.Add("--migrate"); start.ArgumentList.Add("--import-repositories"); start.ArgumentList.Add(path);
            start.Environment["LFS_DATABASE_URL"] = connection;
            using var process = System.Diagnostics.Process.Start(start);
            var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(deadline.Token);
            Assert.True(process.ExitCode == 0, await output + await errors);
            await using var db = Db(); Assert.Equal(id, (await db.Repositories.SingleAsync()).Id);
        }
        finally { File.Delete(path); }
    }

}

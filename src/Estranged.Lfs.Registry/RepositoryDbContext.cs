using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Npgsql;

namespace Estranged.Lfs.Registry
{
    public sealed class LfsRepository
    {
        public Guid Id { get; set; }
        public string DisplayName { get; set; }
        public string StoragePrefix { get; set; }
        public bool Enabled { get; set; }
        public DateTime CreatedAt { get; set; }
        public List<RepositorySource> Sources { get; set; } = new();
    }
    public sealed class RepositorySource
    {
        public string Provider { get; set; }
        public string Id { get; set; }
        public Guid RepositoryId { get; set; }
        public LfsRepository Repository { get; set; }
    }
    public sealed class Provisioner
    {
        public string PrincipalKey { get; set; }
        public string Issuer { get; set; }
        public string Subject { get; set; }
        public bool Enabled { get; set; }
    }
    public sealed class Enrollment
    {
        public string OwnerKey { get; set; }
        public string Key { get; set; }
        public Guid RepositoryId { get; set; }
        public LfsRepository Repository { get; set; }
    }
    public sealed class AccessGrant
    {
        public Guid RepositoryId { get; set; }
        public string ControllerKey { get; set; }
        public string ManagerKey { get; set; }
        public string PrincipalKey { get; set; }
        public string Issuer { get; set; }
        public string Subject { get; set; }
        public int Permission { get; set; }
    }
    public sealed class RepositoryCredential
    {
        public Guid Id { get; set; }
        public Guid RepositoryId { get; set; }
        public string OwnerKey { get; set; }
        public string KeyVersion { get; set; }
        public bool Enabled { get; set; }
    }
    public sealed class RegistryAudit
    {
        public long Id { get; set; }
        public DateTime At { get; set; }
        public Guid RepositoryId { get; set; }
        public string ActorKey { get; set; }
        public string Operation { get; set; }
        public string ManagerKey { get; set; }
    }
    public sealed class RepositoryDbContext : DbContext
    {
        public RepositoryDbContext(DbContextOptions<RepositoryDbContext> options) : base(options) { }
        public DbSet<LfsRepository> Repositories => Set<LfsRepository>();
        public DbSet<RepositorySource> Sources => Set<RepositorySource>();
        public DbSet<Provisioner> Provisioners => Set<Provisioner>();
        public DbSet<Enrollment> Enrollments => Set<Enrollment>();
        public DbSet<AccessGrant> Grants => Set<AccessGrant>();
        public DbSet<RepositoryCredential> Credentials => Set<RepositoryCredential>();
        public DbSet<RegistryAudit> Audit => Set<RegistryAudit>();
        protected override void OnModelCreating(ModelBuilder model)
        {
            var repo = model.Entity<LfsRepository>();
            repo.ToTable("lfs_repositories"); repo.HasKey(x => x.Id);
            repo.Property(x => x.Id).ValueGeneratedNever();
            repo.Property(x => x.DisplayName).IsRequired().HasMaxLength(256);
            repo.Property(x => x.StoragePrefix).IsRequired().HasMaxLength(512);
            repo.HasIndex(x => x.StoragePrefix).IsUnique();
            var source = model.Entity<RepositorySource>();
            source.ToTable("lfs_repository_sources"); source.HasKey(x => new { x.RepositoryId, x.Provider, x.Id });
            source.Property(x => x.Provider).HasMaxLength(128); source.Property(x => x.Id).HasMaxLength(256);
            source.HasOne(x => x.Repository).WithMany(x => x.Sources).HasForeignKey(x => x.RepositoryId).OnDelete(DeleteBehavior.Restrict);
            var provisioner = model.Entity<Provisioner>();
            provisioner.ToTable("lfs_provisioners"); provisioner.HasKey(x => x.PrincipalKey);
            provisioner.Property(x => x.PrincipalKey).HasMaxLength(64);
            provisioner.Property(x => x.Issuer).IsRequired().HasMaxLength(512);
            provisioner.Property(x => x.Subject).IsRequired().HasMaxLength(512);
            var enrollment = model.Entity<Enrollment>();
            enrollment.ToTable("lfs_enrollments"); enrollment.HasKey(x => new { x.OwnerKey, x.Key });
            enrollment.Property(x => x.OwnerKey).HasMaxLength(64); enrollment.Property(x => x.Key).HasMaxLength(128);
            enrollment.HasOne(x => x.Repository).WithMany().HasForeignKey(x => x.RepositoryId).OnDelete(DeleteBehavior.Restrict);
            var grant = model.Entity<AccessGrant>();
            grant.ToTable("lfs_access_grants"); grant.HasKey(x => new { x.RepositoryId, x.ControllerKey, x.ManagerKey, x.PrincipalKey });
            grant.Property(x => x.ControllerKey).HasMaxLength(64); grant.Property(x => x.ManagerKey).HasMaxLength(128);
            grant.Property(x => x.PrincipalKey).HasMaxLength(64);
            grant.Property(x => x.Issuer).IsRequired().HasMaxLength(512); grant.Property(x => x.Subject).IsRequired().HasMaxLength(512);
            grant.HasOne<LfsRepository>().WithMany().HasForeignKey(x => x.RepositoryId).OnDelete(DeleteBehavior.Restrict);
            var credential = model.Entity<RepositoryCredential>();
            credential.ToTable("lfs_credentials"); credential.HasKey(x => x.Id);
            credential.Property(x => x.Id).ValueGeneratedNever(); credential.Property(x => x.OwnerKey).HasMaxLength(64);
            credential.Property(x => x.KeyVersion).IsRequired().HasMaxLength(32);
            credential.HasIndex(x => new { x.RepositoryId, x.OwnerKey }).IsUnique();
            credential.HasOne<LfsRepository>().WithMany().HasForeignKey(x => x.RepositoryId).OnDelete(DeleteBehavior.Restrict);
            var audit = model.Entity<RegistryAudit>();
            audit.ToTable("lfs_registry_audit"); audit.HasKey(x => x.Id);
            audit.Property(x => x.ActorKey).IsRequired().HasMaxLength(64);
            audit.Property(x => x.Operation).IsRequired().HasMaxLength(64);
            audit.Property(x => x.ManagerKey).IsRequired().HasMaxLength(128);
        }
    }
    public sealed class RepositoryDbContextFactory : IDesignTimeDbContextFactory<RepositoryDbContext>
    {
        public static string ConnectionString()
        {
            var explicitUrl = Environment.GetEnvironmentVariable("LFS_DATABASE_URL");
            if (!string.IsNullOrEmpty(explicitUrl)) return explicitUrl;
            var builder = new NpgsqlConnectionStringBuilder
            {
                Host = Environment.GetEnvironmentVariable("LFS_DB_HOST") ?? "localhost",
                Database = Environment.GetEnvironmentVariable("LFS_DB_NAME") ?? "lfs",
                Username = Environment.GetEnvironmentVariable("LFS_DB_USER") ?? "lfs_owner",
                Password = Environment.GetEnvironmentVariable("LFS_DB_PASSWORD"),
                Timeout = 10, CommandTimeout = 30,
            };
            if (Environment.GetEnvironmentVariable("LFS_DB_ROOT_CERT") is string ca)
            {
                builder.SslMode = SslMode.VerifyFull;
                builder.RootCertificate = ca;
            }
            return builder.ConnectionString;
        }
        public RepositoryDbContext CreateDbContext(string[] args) => new(
            new DbContextOptionsBuilder<RepositoryDbContext>().UseNpgsql(ConnectionString()).Options);
    }
}

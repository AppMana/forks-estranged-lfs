using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Estranged.Lfs.Registry.Migrations
{
    /// <inheritdoc />
    public partial class InitialRepositoryRegistry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "lfs_provisioners",
                columns: table => new
                {
                    PrincipalKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Issuer = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Subject = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lfs_provisioners", x => x.PrincipalKey);
                });

            migrationBuilder.CreateTable(
                name: "lfs_registry_audit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RepositoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Operation = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ManagerKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lfs_registry_audit", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "lfs_repositories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    StoragePrefix = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lfs_repositories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "lfs_access_grants",
                columns: table => new
                {
                    RepositoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    ControllerKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ManagerKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    PrincipalKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Issuer = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Subject = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Permission = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lfs_access_grants", x => new { x.RepositoryId, x.ControllerKey, x.ManagerKey, x.PrincipalKey });
                    table.ForeignKey(
                        name: "FK_lfs_access_grants_lfs_repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "lfs_repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "lfs_credentials",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RepositoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    KeyVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lfs_credentials", x => x.Id);
                    table.ForeignKey(
                        name: "FK_lfs_credentials_lfs_repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "lfs_repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "lfs_enrollments",
                columns: table => new
                {
                    OwnerKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    RepositoryId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lfs_enrollments", x => new { x.OwnerKey, x.Key });
                    table.ForeignKey(
                        name: "FK_lfs_enrollments_lfs_repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "lfs_repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "lfs_repository_sources",
                columns: table => new
                {
                    Provider = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    RepositoryId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lfs_repository_sources", x => new { x.RepositoryId, x.Provider, x.Id });
                    table.ForeignKey(
                        name: "FK_lfs_repository_sources_lfs_repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "lfs_repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_lfs_credentials_RepositoryId_OwnerKey",
                table: "lfs_credentials",
                columns: new[] { "RepositoryId", "OwnerKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_lfs_enrollments_RepositoryId",
                table: "lfs_enrollments",
                column: "RepositoryId");

            migrationBuilder.CreateIndex(
                name: "IX_lfs_repositories_StoragePrefix",
                table: "lfs_repositories",
                column: "StoragePrefix",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "lfs_access_grants");

            migrationBuilder.DropTable(
                name: "lfs_credentials");

            migrationBuilder.DropTable(
                name: "lfs_enrollments");

            migrationBuilder.DropTable(
                name: "lfs_provisioners");

            migrationBuilder.DropTable(
                name: "lfs_registry_audit");

            migrationBuilder.DropTable(
                name: "lfs_repository_sources");

            migrationBuilder.DropTable(
                name: "lfs_repositories");
        }
    }
}

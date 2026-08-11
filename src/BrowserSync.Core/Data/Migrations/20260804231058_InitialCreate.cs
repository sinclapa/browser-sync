using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace BrowserSync.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CanonicalBookmarks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ParentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Url = table.Column<string>(type: "TEXT", nullable: true),
                    SortIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    RoleRoot = table.Column<int>(type: "INTEGER", nullable: false),
                    LastModifiedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastModifiedByClientId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CanonicalBookmarks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CanonicalBookmarks_CanonicalBookmarks_ParentId",
                        column: x => x.ParentId,
                        principalTable: "CanonicalBookmarks",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ClientBookmarkMappings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ClientId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CanonicalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    NativeId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientBookmarkMappings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Clients",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    BrowserKind = table.Column<int>(type: "INTEGER", nullable: false),
                    LastSeenUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastReconciledUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Clients", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Tombstones",
                columns: table => new
                {
                    CanonicalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DeletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DeletedByClientId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tombstones", x => x.CanonicalId);
                });

            migrationBuilder.InsertData(
                table: "CanonicalBookmarks",
                columns: new[] { "Id", "Kind", "LastModifiedByClientId", "LastModifiedUtc", "ParentId", "RoleRoot", "SortIndex", "Title", "Url" },
                values: new object[,]
                {
                    { new Guid("00000000-0000-0000-0000-000000000001"), 1, null, new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, 1, 0, "Bookmarks Bar", null },
                    { new Guid("00000000-0000-0000-0000-000000000002"), 1, null, new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, 2, 1, "Other Bookmarks", null },
                    { new Guid("00000000-0000-0000-0000-000000000003"), 1, null, new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, 3, 2, "Mobile Bookmarks", null }
                });

            migrationBuilder.CreateIndex(
                name: "IX_CanonicalBookmarks_ParentId",
                table: "CanonicalBookmarks",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientBookmarkMappings_ClientId_CanonicalId",
                table: "ClientBookmarkMappings",
                columns: new[] { "ClientId", "CanonicalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClientBookmarkMappings_ClientId_NativeId",
                table: "ClientBookmarkMappings",
                columns: new[] { "ClientId", "NativeId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CanonicalBookmarks");

            migrationBuilder.DropTable(
                name: "ClientBookmarkMappings");

            migrationBuilder.DropTable(
                name: "Clients");

            migrationBuilder.DropTable(
                name: "Tombstones");
        }
    }
}

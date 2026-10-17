using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yanmt.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuditEntries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TimestampUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    User = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Action = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    EntityType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    EntityKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Changes = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Nodes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Hostname = table.Column<string>(type: "TEXT", maxLength: 253, nullable: false, collation: "NOCASE"),
                    ManagementIp = table.Column<string>(type: "TEXT", maxLength: 45, nullable: false),
                    Site = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Icon = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    IcmpEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    SnmpEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    SshEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Nodes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Settings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    JsonValue = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "Tags",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false, collation: "NOCASE")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tags", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Events",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TimestampUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    NodeId = table.Column<int>(type: "INTEGER", nullable: true),
                    Source = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    SourceKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    OldState = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    NewState = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Message = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    AcknowledgedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    AcknowledgedUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Events_Nodes_NodeId",
                        column: x => x.NodeId,
                        principalTable: "Nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "StatusSources",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    NodeId = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceType = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    SourceKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Value = table.Column<double>(type: "REAL", nullable: true),
                    ValueText = table.Column<string>(type: "TEXT", nullable: true),
                    SinceUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastUpdateUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    PendingState = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    PendingCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StatusSources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StatusSources_Nodes_NodeId",
                        column: x => x.NodeId,
                        principalTable: "Nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NodeTags",
                columns: table => new
                {
                    NodeId = table.Column<int>(type: "INTEGER", nullable: false),
                    TagId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NodeTags", x => new { x.NodeId, x.TagId });
                    table.ForeignKey(
                        name: "FK_NodeTags_Nodes_NodeId",
                        column: x => x.NodeId,
                        principalTable: "Nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_NodeTags_Tags_TagId",
                        column: x => x.TagId,
                        principalTable: "Tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEntries_EntityType_EntityKey",
                table: "AuditEntries",
                columns: new[] { "EntityType", "EntityKey" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEntries_TimestampUtc",
                table: "AuditEntries",
                column: "TimestampUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Events_NodeId_TimestampUtc",
                table: "Events",
                columns: new[] { "NodeId", "TimestampUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Events_TimestampUtc",
                table: "Events",
                column: "TimestampUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Nodes_Hostname",
                table: "Nodes",
                column: "Hostname",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Nodes_ManagementIp",
                table: "Nodes",
                column: "ManagementIp");

            migrationBuilder.CreateIndex(
                name: "IX_Nodes_Site",
                table: "Nodes",
                column: "Site");

            migrationBuilder.CreateIndex(
                name: "IX_NodeTags_TagId",
                table: "NodeTags",
                column: "TagId");

            migrationBuilder.CreateIndex(
                name: "IX_StatusSources_NodeId_SourceType_SourceKey",
                table: "StatusSources",
                columns: new[] { "NodeId", "SourceType", "SourceKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tags_Name",
                table: "Tags",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditEntries");

            migrationBuilder.DropTable(
                name: "Events");

            migrationBuilder.DropTable(
                name: "NodeTags");

            migrationBuilder.DropTable(
                name: "Settings");

            migrationBuilder.DropTable(
                name: "StatusSources");

            migrationBuilder.DropTable(
                name: "Tags");

            migrationBuilder.DropTable(
                name: "Nodes");
        }
    }
}

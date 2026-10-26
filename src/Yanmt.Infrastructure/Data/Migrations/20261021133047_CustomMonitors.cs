using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yanmt.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class CustomMonitors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DisplayName",
                table: "StatusSources",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BoundMonitorInstanceId",
                table: "MapLinks",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ShowBoundValue",
                table: "MapLinks",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "MonitorDefinitions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false, collation: "NOCASE"),
                    Description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    StarterKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    OidMode = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Oid = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    LabelOid = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    ValueMode = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Scale = table.Column<decimal>(type: "TEXT", nullable: false),
                    DenominatorOid = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    DenominatorScale = table.Column<decimal>(type: "TEXT", nullable: false),
                    Unit = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    ValueMap = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    RulesJson = table.Column<string>(type: "TEXT", maxLength: 16000, nullable: false),
                    DefaultState = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    ConsecutiveMatches = table.Column<int>(type: "INTEGER", nullable: false),
                    RaiseEvents = table.Column<bool>(type: "INTEGER", nullable: false),
                    IntervalSeconds = table.Column<int>(type: "INTEGER", nullable: true),
                    FilterOid = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    FilterOperator = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    FilterValue = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    LabelRegex = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MonitorDefinitions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MonitorAssignments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DefinitionId = table.Column<int>(type: "INTEGER", nullable: false),
                    Scope = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    NodeId = table.Column<int>(type: "INTEGER", nullable: true),
                    TagName = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true, collation: "NOCASE"),
                    InstanceSuffix = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    InstanceLabel = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MonitorAssignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MonitorAssignments_MonitorDefinitions_DefinitionId",
                        column: x => x.DefinitionId,
                        principalTable: "MonitorDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MonitorAssignments_Nodes_NodeId",
                        column: x => x.NodeId,
                        principalTable: "Nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MonitorInstances",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DefinitionId = table.Column<int>(type: "INTEGER", nullable: false),
                    NodeId = table.Column<int>(type: "INTEGER", nullable: false),
                    InstanceKey = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Label = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    LastRaw = table.Column<decimal>(type: "TEXT", nullable: true),
                    LastRawUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastUptimeTicks = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MonitorInstances", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MonitorInstances_MonitorDefinitions_DefinitionId",
                        column: x => x.DefinitionId,
                        principalTable: "MonitorDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MonitorInstances_Nodes_NodeId",
                        column: x => x.NodeId,
                        principalTable: "Nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MapLinks_BoundMonitorInstanceId",
                table: "MapLinks",
                column: "BoundMonitorInstanceId");

            migrationBuilder.CreateIndex(
                name: "IX_MonitorAssignments_DefinitionId_Scope_NodeId_TagName_InstanceSuffix",
                table: "MonitorAssignments",
                columns: new[] { "DefinitionId", "Scope", "NodeId", "TagName", "InstanceSuffix" });

            migrationBuilder.CreateIndex(
                name: "IX_MonitorAssignments_NodeId",
                table: "MonitorAssignments",
                column: "NodeId");

            migrationBuilder.CreateIndex(
                name: "IX_MonitorDefinitions_Name",
                table: "MonitorDefinitions",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MonitorInstances_DefinitionId_NodeId_InstanceKey",
                table: "MonitorInstances",
                columns: new[] { "DefinitionId", "NodeId", "InstanceKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MonitorInstances_NodeId",
                table: "MonitorInstances",
                column: "NodeId");

            migrationBuilder.AddForeignKey(
                name: "FK_MapLinks_MonitorInstances_BoundMonitorInstanceId",
                table: "MapLinks",
                column: "BoundMonitorInstanceId",
                principalTable: "MonitorInstances",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MapLinks_MonitorInstances_BoundMonitorInstanceId",
                table: "MapLinks");

            migrationBuilder.DropTable(
                name: "MonitorAssignments");

            migrationBuilder.DropTable(
                name: "MonitorInstances");

            migrationBuilder.DropTable(
                name: "MonitorDefinitions");

            migrationBuilder.DropIndex(
                name: "IX_MapLinks_BoundMonitorInstanceId",
                table: "MapLinks");

            migrationBuilder.DropColumn(
                name: "DisplayName",
                table: "StatusSources");

            migrationBuilder.DropColumn(
                name: "BoundMonitorInstanceId",
                table: "MapLinks");

            migrationBuilder.DropColumn(
                name: "ShowBoundValue",
                table: "MapLinks");
        }
    }
}

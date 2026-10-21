using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yanmt.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class SnmpDefaultStats : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "CpuCriticalPercent",
                table: "Nodes",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "CpuWarningPercent",
                table: "Nodes",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "MemoryCriticalPercent",
                table: "Nodes",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "MemoryWarningPercent",
                table: "Nodes",
                type: "REAL",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MetricSamples",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    StatusSourceId = table.Column<long>(type: "INTEGER", nullable: false),
                    TimestampUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Value = table.Column<double>(type: "REAL", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetricSamples", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MetricSamples_StatusSources_StatusSourceId",
                        column: x => x.StatusSourceId,
                        principalTable: "StatusSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NodeSystemInfo",
                columns: table => new
                {
                    NodeId = table.Column<int>(type: "INTEGER", nullable: false),
                    SysDescr = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    SysObjectId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    SysName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    SysLocation = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    SysContact = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    UptimeTicks = table.Column<long>(type: "INTEGER", nullable: true),
                    EngineId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    EngineBoots = table.Column<int>(type: "INTEGER", nullable: true),
                    MemoryPool = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    LastPollUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastSuccessUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastRebootUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NodeSystemInfo", x => x.NodeId);
                    table.ForeignKey(
                        name: "FK_NodeSystemInfo_Nodes_NodeId",
                        column: x => x.NodeId,
                        principalTable: "Nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MetricSamples_StatusSourceId_TimestampUtc",
                table: "MetricSamples",
                columns: new[] { "StatusSourceId", "TimestampUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MetricSamples");

            migrationBuilder.DropTable(
                name: "NodeSystemInfo");

            migrationBuilder.DropColumn(
                name: "CpuCriticalPercent",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "CpuWarningPercent",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "MemoryCriticalPercent",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "MemoryWarningPercent",
                table: "Nodes");
        }
    }
}

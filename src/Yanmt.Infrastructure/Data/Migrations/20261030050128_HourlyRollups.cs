using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yanmt.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class HourlyRollups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IcmpRollupHours",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    NodeId = table.Column<int>(type: "INTEGER", nullable: false),
                    HourUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Sent = table.Column<int>(type: "INTEGER", nullable: false),
                    Received = table.Column<int>(type: "INTEGER", nullable: false),
                    MinMs = table.Column<double>(type: "REAL", nullable: true),
                    AvgMs = table.Column<double>(type: "REAL", nullable: true),
                    MaxMs = table.Column<double>(type: "REAL", nullable: true),
                    LossPercent = table.Column<double>(type: "REAL", nullable: false),
                    SampleCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IcmpRollupHours", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IcmpRollupHours_Nodes_NodeId",
                        column: x => x.NodeId,
                        principalTable: "Nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MetricRollupHours",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    StatusSourceId = table.Column<long>(type: "INTEGER", nullable: false),
                    HourUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Min = table.Column<double>(type: "REAL", nullable: false),
                    Avg = table.Column<double>(type: "REAL", nullable: false),
                    Max = table.Column<double>(type: "REAL", nullable: false),
                    SampleCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetricRollupHours", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MetricRollupHours_StatusSources_StatusSourceId",
                        column: x => x.StatusSourceId,
                        principalTable: "StatusSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IcmpRollupHours_HourUtc",
                table: "IcmpRollupHours",
                column: "HourUtc");

            migrationBuilder.CreateIndex(
                name: "IX_IcmpRollupHours_NodeId_HourUtc",
                table: "IcmpRollupHours",
                columns: new[] { "NodeId", "HourUtc" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MetricRollupHours_HourUtc",
                table: "MetricRollupHours",
                column: "HourUtc");

            migrationBuilder.CreateIndex(
                name: "IX_MetricRollupHours_StatusSourceId_HourUtc",
                table: "MetricRollupHours",
                columns: new[] { "StatusSourceId", "HourUtc" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IcmpRollupHours");

            migrationBuilder.DropTable(
                name: "MetricRollupHours");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yanmt.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class MapsAndIcmp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IcmpSamples",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    NodeId = table.Column<int>(type: "INTEGER", nullable: false),
                    TimestampUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Sent = table.Column<int>(type: "INTEGER", nullable: false),
                    Received = table.Column<int>(type: "INTEGER", nullable: false),
                    MinMs = table.Column<double>(type: "REAL", nullable: true),
                    AvgMs = table.Column<double>(type: "REAL", nullable: true),
                    MaxMs = table.Column<double>(type: "REAL", nullable: true),
                    LossPercent = table.Column<double>(type: "REAL", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IcmpSamples", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IcmpSamples_Nodes_NodeId",
                        column: x => x.NodeId,
                        principalTable: "Nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Maps",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false, collation: "NOCASE"),
                    ParentMapId = table.Column<int>(type: "INTEGER", nullable: true),
                    BackgroundImage = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Width = table.Column<int>(type: "INTEGER", nullable: false),
                    Height = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Maps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Maps_Maps_ParentMapId",
                        column: x => x.ParentMapId,
                        principalTable: "Maps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "MapElements",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MapId = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    NodeId = table.Column<int>(type: "INTEGER", nullable: true),
                    TargetMapId = table.Column<int>(type: "INTEGER", nullable: true),
                    X = table.Column<double>(type: "REAL", nullable: false),
                    Y = table.Column<double>(type: "REAL", nullable: false),
                    LabelSourceType = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    LabelSourceKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MapElements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MapElements_Maps_MapId",
                        column: x => x.MapId,
                        principalTable: "Maps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MapElements_Maps_TargetMapId",
                        column: x => x.TargetMapId,
                        principalTable: "Maps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MapElements_Nodes_NodeId",
                        column: x => x.NodeId,
                        principalTable: "Nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MapLinks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MapId = table.Column<int>(type: "INTEGER", nullable: false),
                    FromElementId = table.Column<int>(type: "INTEGER", nullable: false),
                    ToElementId = table.Column<int>(type: "INTEGER", nullable: false),
                    Label = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MapLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MapLinks_MapElements_FromElementId",
                        column: x => x.FromElementId,
                        principalTable: "MapElements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MapLinks_MapElements_ToElementId",
                        column: x => x.ToElementId,
                        principalTable: "MapElements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MapLinks_Maps_MapId",
                        column: x => x.MapId,
                        principalTable: "Maps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IcmpSamples_NodeId_TimestampUtc",
                table: "IcmpSamples",
                columns: new[] { "NodeId", "TimestampUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MapElements_MapId_NodeId",
                table: "MapElements",
                columns: new[] { "MapId", "NodeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MapElements_MapId_TargetMapId",
                table: "MapElements",
                columns: new[] { "MapId", "TargetMapId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MapElements_NodeId",
                table: "MapElements",
                column: "NodeId");

            migrationBuilder.CreateIndex(
                name: "IX_MapElements_TargetMapId",
                table: "MapElements",
                column: "TargetMapId");

            migrationBuilder.CreateIndex(
                name: "IX_MapLinks_FromElementId",
                table: "MapLinks",
                column: "FromElementId");

            migrationBuilder.CreateIndex(
                name: "IX_MapLinks_MapId_FromElementId_ToElementId",
                table: "MapLinks",
                columns: new[] { "MapId", "FromElementId", "ToElementId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MapLinks_ToElementId",
                table: "MapLinks",
                column: "ToElementId");

            migrationBuilder.CreateIndex(
                name: "IX_Maps_Name",
                table: "Maps",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Maps_ParentMapId",
                table: "Maps",
                column: "ParentMapId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IcmpSamples");

            migrationBuilder.DropTable(
                name: "MapLinks");

            migrationBuilder.DropTable(
                name: "MapElements");

            migrationBuilder.DropTable(
                name: "Maps");
        }
    }
}

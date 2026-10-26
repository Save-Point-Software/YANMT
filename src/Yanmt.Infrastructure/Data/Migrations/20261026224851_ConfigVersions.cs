using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yanmt.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class ConfigVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ConfigVersions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    NodeId = table.Column<int>(type: "INTEGER", nullable: false),
                    CapturedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ContentHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ProtectedRaw = table.Column<byte[]>(type: "BLOB", nullable: false),
                    RawLength = table.Column<int>(type: "INTEGER", nullable: false),
                    LineCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConfigVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConfigVersions_Nodes_NodeId",
                        column: x => x.NodeId,
                        principalTable: "Nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConfigVersions_NodeId_CapturedUtc",
                table: "ConfigVersions",
                columns: new[] { "NodeId", "CapturedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConfigVersions");
        }
    }
}

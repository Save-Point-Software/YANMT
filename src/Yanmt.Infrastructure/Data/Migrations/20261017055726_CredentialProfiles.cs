using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yanmt.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class CredentialProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SnmpProfileId",
                table: "Nodes",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SshHostKeyAlgorithm",
                table: "Nodes",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SshHostKeyFingerprint",
                table: "Nodes",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SshProfileId",
                table: "Nodes",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CredentialProfiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false, collation: "NOCASE"),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Username = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Port = table.Column<int>(type: "INTEGER", nullable: false),
                    SnmpAuth = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    SnmpPriv = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    ProtectedSecrets = table.Column<byte[]>(type: "BLOB", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CredentialProfiles", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Nodes_SnmpProfileId",
                table: "Nodes",
                column: "SnmpProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_Nodes_SshProfileId",
                table: "Nodes",
                column: "SshProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_CredentialProfiles_Name",
                table: "CredentialProfiles",
                column: "Name",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Nodes_CredentialProfiles_SnmpProfileId",
                table: "Nodes",
                column: "SnmpProfileId",
                principalTable: "CredentialProfiles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Nodes_CredentialProfiles_SshProfileId",
                table: "Nodes",
                column: "SshProfileId",
                principalTable: "CredentialProfiles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Nodes_CredentialProfiles_SnmpProfileId",
                table: "Nodes");

            migrationBuilder.DropForeignKey(
                name: "FK_Nodes_CredentialProfiles_SshProfileId",
                table: "Nodes");

            migrationBuilder.DropTable(
                name: "CredentialProfiles");

            migrationBuilder.DropIndex(
                name: "IX_Nodes_SnmpProfileId",
                table: "Nodes");

            migrationBuilder.DropIndex(
                name: "IX_Nodes_SshProfileId",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "SnmpProfileId",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "SshHostKeyAlgorithm",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "SshHostKeyFingerprint",
                table: "Nodes");

            migrationBuilder.DropColumn(
                name: "SshProfileId",
                table: "Nodes");
        }
    }
}

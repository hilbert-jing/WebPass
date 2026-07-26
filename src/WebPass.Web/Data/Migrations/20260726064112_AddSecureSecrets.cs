using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WebPass.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSecureSecrets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DataEncryptionKeys",
                columns: table => new
                {
                    KeyVersion = table.Column<int>(type: "int", nullable: false),
                    WrappedKey = table.Column<byte[]>(type: "varbinary(1024)", maxLength: 1024, nullable: false),
                    CertificateThumbprint = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ActivatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RetiredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataEncryptionKeys", x => x.KeyVersion);
                });

            migrationBuilder.CreateTable(
                name: "ServerSecrets",
                columns: table => new
                {
                    ServerAssetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Ciphertext = table.Column<byte[]>(type: "varbinary(4096)", maxLength: 4096, nullable: false),
                    Nonce = table.Column<byte[]>(type: "binary(12)", fixedLength: true, maxLength: 12, nullable: false),
                    AuthenticationTag = table.Column<byte[]>(type: "binary(16)", fixedLength: true, maxLength: 16, nullable: false),
                    KeyVersion = table.Column<int>(type: "int", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServerSecrets", x => x.ServerAssetId);
                    table.ForeignKey(
                        name: "FK_ServerSecrets_DataEncryptionKeys_KeyVersion",
                        column: x => x.KeyVersion,
                        principalTable: "DataEncryptionKeys",
                        principalColumn: "KeyVersion",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ServerSecrets_ServerAssets_ServerAssetId",
                        column: x => x.ServerAssetId,
                        principalTable: "ServerAssets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DataEncryptionKeys_RetiredAt",
                table: "DataEncryptionKeys",
                column: "RetiredAt",
                unique: true,
                filter: "[RetiredAt] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ServerSecrets_KeyVersion",
                table: "ServerSecrets",
                column: "KeyVersion");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ServerSecrets");

            migrationBuilder.DropTable(
                name: "DataEncryptionKeys");
        }
    }
}

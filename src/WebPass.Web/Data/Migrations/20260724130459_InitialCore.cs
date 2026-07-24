using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WebPass.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuditLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Action = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ObjectType = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ObjectId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Result = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SourceIp = table.Column<string>(type: "nvarchar(45)", maxLength: 45, nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Details = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Subnets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Cidr = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    NetworkAddress = table.Column<string>(type: "nvarchar(15)", maxLength: 15, nullable: false),
                    PrefixLength = table.Column<int>(type: "int", nullable: false),
                    Location = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Subnets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Username = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    PasswordHash = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    IsAdministrator = table.Column<bool>(type: "bit", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    FailedLoginCount = table.Column<int>(type: "int", nullable: false),
                    LockedUntil = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    MustChangePassword = table.Column<bool>(type: "bit", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ServerAssets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SubnetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BusinessIp = table.Column<string>(type: "nvarchar(15)", maxLength: 15, nullable: false),
                    BusinessIpNumber = table.Column<long>(type: "bigint", nullable: false),
                    Location = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    AliveStatus = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ComputerName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    SystemName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    OperatingSystemVersion = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DatabaseVersion = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsArchived = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ArchivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ArchivedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServerAssets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ServerAssets_Subnets_SubnetId",
                        column: x => x.SubnetId,
                        principalTable: "Subnets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "UserPermissions",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PermissionCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserPermissions", x => new { x.UserId, x.PermissionCode });
                    table.ForeignKey(
                        name: "FK_UserPermissions_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PingResults",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ServerAssetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TargetIp = table.Column<string>(type: "nvarchar(15)", maxLength: 15, nullable: false),
                    Outcome = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    LatencyMilliseconds = table.Column<long>(type: "bigint", nullable: true),
                    ErrorCode = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    ExecutedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExecutedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PingResults", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PingResults_ServerAssets_ServerAssetId",
                        column: x => x.ServerAssetId,
                        principalTable: "ServerAssets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PingResults_ServerAssetId",
                table: "PingResults",
                column: "ServerAssetId");

            migrationBuilder.CreateIndex(
                name: "IX_ServerAssets_BusinessIp",
                table: "ServerAssets",
                column: "BusinessIp",
                unique: true,
                filter: "[IsArchived] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_ServerAssets_SubnetId",
                table: "ServerAssets",
                column: "SubnetId");

            migrationBuilder.CreateIndex(
                name: "IX_Subnets_Cidr",
                table: "Subnets",
                column: "Cidr",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_Username",
                table: "Users",
                column: "Username",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditLogs");

            migrationBuilder.DropTable(
                name: "PingResults");

            migrationBuilder.DropTable(
                name: "UserPermissions");

            migrationBuilder.DropTable(
                name: "ServerAssets");

            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.DropTable(
                name: "Subnets");
        }
    }
}

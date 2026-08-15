using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WebPass.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditActorUsername : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ActorUsername",
                table: "AuditLogs",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE [audit]
                SET [audit].[ActorUsername] = [users].[Username]
                FROM [AuditLogs] AS [audit]
                INNER JOIN [Users] AS [users]
                    ON [audit].[ActorUserId] = [users].[Id]
                WHERE [audit].[ActorUsername] IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ActorUsername",
                table: "AuditLogs");
        }
    }
}

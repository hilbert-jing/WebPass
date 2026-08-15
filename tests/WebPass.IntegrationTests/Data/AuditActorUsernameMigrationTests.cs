using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using WebPass.Web.Data;
using Xunit;

namespace WebPass.IntegrationTests.Data;

public sealed class AuditActorUsernameMigrationTests
{
    [Fact]
    public async Task Backfills_matching_actor_and_preserves_unresolved_history()
    {
        var databaseName = "WebPassAuditActorMigration_" + Guid.NewGuid().ToString("N");
        var connection = $"Server=localhost\\SQLEXPRESS;Database={databaseName};Integrated Security=True;TrustServerCertificate=True";
        var options = new DbContextOptionsBuilder<WebPassDbContext>()
            .UseSqlServer(connection)
            .Options;
        await using var db = new WebPassDbContext(options);

        try
        {
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync("20260726131039_AddImportJobs");
            var matchedActorId = Guid.NewGuid();
            var orphanActorId = Guid.NewGuid();
            var matchedAuditId = Guid.NewGuid();
            var orphanAuditId = Guid.NewGuid();
            var systemAuditId = Guid.NewGuid();
            var occurredAt = DateTimeOffset.Parse("2026-08-15T00:00:00+00:00");

            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO [Users]
                    ([Id], [Username], [PasswordHash], [IsAdministrator], [IsEnabled],
                     [FailedLoginCount], [MustChangePassword])
                VALUES
                    ({matchedActorId}, {"historical-operator"}, {"hash"}, {false}, {true}, {0}, {false});
                """);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO [AuditLogs]
                    ([Id], [ActorUserId], [Action], [ObjectType], [Result], [OccurredAt])
                VALUES
                    ({matchedAuditId}, {matchedActorId}, {"Matched"}, {"Object"}, {"Success"}, {occurredAt}),
                    ({orphanAuditId}, {orphanActorId}, {"Orphan"}, {"Object"}, {"Success"}, {occurredAt}),
                    ({systemAuditId}, {null}, {"System"}, {"Object"}, {"Success"}, {occurredAt});
                """);

            await migrator.MigrateAsync();
            db.ChangeTracker.Clear();

            var matched = await db.AuditLogs.SingleAsync(entry => entry.Id == matchedAuditId);
            var orphan = await db.AuditLogs.SingleAsync(entry => entry.Id == orphanAuditId);
            var system = await db.AuditLogs.SingleAsync(entry => entry.Id == systemAuditId);
            Assert.Equal("historical-operator", matched.ActorUsername);
            Assert.Equal(matchedActorId, matched.ActorUserId);
            Assert.Null(orphan.ActorUsername);
            Assert.Equal(orphanActorId, orphan.ActorUserId);
            Assert.Null(system.ActorUsername);
            Assert.Null(system.ActorUserId);
        }
        finally
        {
            await db.Database.EnsureDeletedAsync();
        }
    }
}

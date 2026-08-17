using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using WebPass.Web.Data;
using WebPass.Web.Domain.Entities;
using WebPass.Web.Infrastructure.Auditing;
using WebPass.Web.Infrastructure.Identity;
using Xunit;

namespace WebPass.IntegrationTests.Identity;

public sealed class PasswordChangeSqlServerTests
{
    [Fact]
    public async Task Sql_rowversion_conflict_does_not_overwrite_user_or_write_audit()
    {
        var connection = NewConnectionString();
        var hasher = new Argon2PasswordHasher();
        var userId = Guid.NewGuid();
        await using (var setup = NewDatabase(connection))
        {
            await setup.Database.EnsureCreatedAsync();
            setup.Users.Add(new AppUser
            {
                Id = userId,
                Username = "operator",
                PasswordHash = hasher.Hash("current-password"),
                MustChangePassword = true,
            });
            await setup.SaveChangesAsync();
        }

        try
        {
            var interceptor = new BeforeFirstSaveInterceptor(async () =>
            {
                await using var competing = NewDatabase(connection);
                var user = await competing.Users.SingleAsync(x => x.Id == userId);
                user.FailedLoginCount = 7;
                await competing.SaveChangesAsync();
            });
            await using var db = NewDatabase(connection, interceptor);
            var service = new PasswordChangeService(db, hasher, new AuditWriter(db));

            var result = await service.ChangeAsync(
                userId,
                "current-password",
                "new-password",
                default);

            Assert.Equal(PasswordChangeResultKind.ConcurrencyConflict, result.Kind);
            db.ChangeTracker.Clear();
            var persisted = await db.Users.SingleAsync(x => x.Id == userId);
            Assert.True(hasher.Verify("current-password", persisted.PasswordHash));
            Assert.False(hasher.Verify("new-password", persisted.PasswordHash));
            Assert.Equal(7, persisted.FailedLoginCount);
            Assert.Empty(await db.AuditLogs.ToListAsync());
        }
        finally
        {
            await using var cleanup = NewDatabase(connection);
            await cleanup.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task Sql_audit_failure_rolls_back_password_change()
    {
        var connection = NewConnectionString();
        var hasher = new Argon2PasswordHasher();
        await using var db = NewDatabase(connection);
        await db.Database.EnsureCreatedAsync();
        var user = new AppUser
        {
            Username = "operator",
            PasswordHash = hasher.Hash("current-password"),
            MustChangePassword = true,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        try
        {
            await db.Database.ExecuteSqlRawAsync(
                "CREATE TRIGGER [TR_AuditLogs_RejectPasswordChange] ON [AuditLogs] INSTEAD OF INSERT AS BEGIN THROW 50000, 'Audit rejected', 1; END");
            var service = new PasswordChangeService(db, hasher, new AuditWriter(db));

            await Assert.ThrowsAsync<DbUpdateException>(() => service.ChangeAsync(
                user.Id,
                "current-password",
                "new-password",
                default));

            db.ChangeTracker.Clear();
            var persisted = await db.Users.SingleAsync(x => x.Id == user.Id);
            Assert.True(hasher.Verify("current-password", persisted.PasswordHash));
            Assert.False(hasher.Verify("new-password", persisted.PasswordHash));
            Assert.True(persisted.MustChangePassword);
            Assert.Empty(await db.AuditLogs.ToListAsync());
        }
        finally
        {
            await db.Database.EnsureDeletedAsync();
        }
    }

    private static WebPassDbContext NewDatabase(
        string connection,
        IInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<WebPassDbContext>()
            .UseSqlServer(connection);
        if (interceptor is not null)
        {
            options.AddInterceptors(interceptor);
        }

        return new(options.Options);
    }

    private static string NewConnectionString()
    {
        var name = "WebPassPasswordChange_" + Guid.NewGuid().ToString("N");
        return $"Server=localhost\\SQLEXPRESS;Database={name};Integrated Security=True;TrustServerCertificate=True";
    }

    private sealed class BeforeFirstSaveInterceptor(Func<Task> beforeSave)
        : SaveChangesInterceptor
    {
        private int invoked;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref invoked, 1) == 0)
            {
                await beforeSave();
            }

            return result;
        }
    }
}

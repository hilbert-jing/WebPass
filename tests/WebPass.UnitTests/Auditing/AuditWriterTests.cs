using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using WebPass.Web.Data;
using WebPass.Web.Domain.Entities;
using WebPass.Web.Infrastructure.Auditing;
using Xunit;

namespace WebPass.UnitTests.Auditing;

public sealed class AuditWriterTests
{
    [Fact]
    public async Task Writes_username_snapshot_without_requiring_a_matching_user()
    {
        var options = new DbContextOptionsBuilder<WebPassDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new WebPassDbContext(options);
        var actor = new AppUser
        {
            Username = "operator",
            PasswordHash = "hash",
        };
        db.Users.Add(actor);
        await db.SaveChangesAsync();
        var missingActorId = Guid.NewGuid();
        var writer = new AuditWriter(db);

        await writer.WriteAsync(
            new AuditEntry(actor.Id, "Resolved", "Object", "1", "Success", null),
            default);
        await writer.WriteAsync(
            new AuditEntry(missingActorId, "Unresolved", "Object", "2", "Success", null),
            default);

        var resolved = await db.AuditLogs.SingleAsync(entry => entry.Action == "Resolved");
        var unresolved = await db.AuditLogs.SingleAsync(entry => entry.Action == "Unresolved");
        Assert.Equal(actor.Id, resolved.ActorUserId);
        Assert.Equal("operator", resolved.ActorUsername);
        Assert.Equal(missingActorId, unresolved.ActorUserId);
        Assert.Null(unresolved.ActorUsername);
    }

    [Fact]
    public async Task Rejects_payloads_with_sensitive_property_names()
    {
        var options = new DbContextOptionsBuilder<WebPassDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new WebPassDbContext(options);
        var writer = new AuditWriter(db);

        var entry = new AuditEntry(
            null,
            "Login",
            "User",
            "operator",
            "Failure",
            IPAddress.Loopback,
            Payload: new Dictionary<string, object?> { ["changes"] = new Dictionary<string, object?> { ["password"] = "must-not-be-logged" } });

        await Assert.ThrowsAsync<ArgumentException>(() => writer.WriteAsync(entry, default));
        Assert.Empty(db.AuditLogs);
    }

    [Fact]
    public async Task Uses_sanitized_request_ip_and_trace_identifier_when_entry_omits_them()
    {
        var options = new DbContextOptionsBuilder<WebPassDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new WebPassDbContext(options);
        var context = new DefaultHttpContext { TraceIdentifier = "trace-123" };
        context.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.8");
        var writer = new AuditWriter(db, new HttpContextAccessor { HttpContext = context });

        await writer.WriteAsync(new AuditEntry(null, "Test", "Object", "1", "Success", null), default);

        var audit = await db.AuditLogs.SingleAsync();
        Assert.Equal("10.0.0.8", audit.SourceIp);
        Assert.Equal("trace-123", audit.CorrelationId);
    }

}

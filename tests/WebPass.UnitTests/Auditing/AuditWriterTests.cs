using System.Net;
using Microsoft.EntityFrameworkCore;
using WebPass.Web.Data;
using WebPass.Web.Infrastructure.Auditing;
using Xunit;

namespace WebPass.UnitTests.Auditing;

public sealed class AuditWriterTests
{
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
}

using Microsoft.EntityFrameworkCore;
using WebPass.AdminInit;
using WebPass.Web.Data;
using Xunit;

namespace WebPass.UnitTests.AdminInit;

public sealed class AdminInitApplicationTests
{
    [Fact]
    public async Task Valid_command_creates_user_and_returns_zero()
    {
        var options = CreateOptions();
        var console = new FakeConsole("secret-value", "secret-value");

        var exitCode = await AdminInitApplication.RunAsync(
            [
                "--connection-string", "test-connection",
                "--username", "admin",
            ],
            console,
            _ => new WebPassDbContext(options),
            default);

        Assert.Equal(0, exitCode);
        Assert.Equal(
            "Administrator 'admin' created.",
            Assert.Single(console.Output));
        await using var verificationDb = new WebPassDbContext(options);
        Assert.Single(verificationDb.Users);
        Assert.DoesNotContain(
            console.Output,
            line => line.Contains(
                "secret-value",
                StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(InvalidArgumentCases))]
    public async Task Invalid_arguments_return_two_without_opening_database(
        string[] args)
    {
        var console = new FakeConsole();
        var opened = false;

        var exitCode = await AdminInitApplication.RunAsync(
            args,
            console,
            _ =>
            {
                opened = true;
                throw new InvalidOperationException();
            },
            default);

        Assert.Equal(2, exitCode);
        Assert.False(opened);
        Assert.Equal(
            "Usage: WebPass.AdminInit --connection-string <value> --username <value>",
            Assert.Single(console.Output));
    }

    public static TheoryData<string[]> InvalidArgumentCases =>
        new()
        {
            Array.Empty<string>(),
            new[] { "--username", "admin" },
            new[] { "--connection-string", "test" },
            new[] { "--unknown", "value" },
        };

    [Fact]
    public async Task Unavailable_interactive_input_returns_two()
    {
        var console = new ThrowingConsole();
        var opened = false;

        var exitCode = await AdminInitApplication.RunAsync(
            [
                "--connection-string", "test-connection",
                "--username", "admin",
            ],
            console,
            _ =>
            {
                opened = true;
                throw new InvalidOperationException();
            },
            default);

        Assert.Equal(2, exitCode);
        Assert.False(opened);
        Assert.Equal(
            "Interactive password input is required.",
            Assert.Single(console.Output));
    }

    [Fact]
    public async Task Mismatched_password_returns_two_without_writing()
    {
        var options = CreateOptions();
        var console = new FakeConsole("secret-one", "secret-two");

        var exitCode = await AdminInitApplication.RunAsync(
            [
                "--connection-string", "test-connection",
                "--username", "admin",
            ],
            console,
            _ => new WebPassDbContext(options),
            default);

        Assert.Equal(2, exitCode);
        await using var verificationDb = new WebPassDbContext(options);
        Assert.Empty(verificationDb.Users);
        Assert.Equal(
            "Password confirmation does not match.",
            Assert.Single(console.Output));
        Assert.DoesNotContain(
            console.Output,
            line => line.Contains("secret-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Duplicate_username_returns_three()
    {
        var options = CreateOptions();
        var first = new FakeConsole(
            "first-password",
            "first-password");
        var duplicate = new FakeConsole(
            "second-password",
            "second-password");
        var args = new[]
        {
            "--connection-string", "test-connection",
            "--username", "admin",
        };
        Assert.Equal(
            0,
            await AdminInitApplication.RunAsync(
                args,
                first,
                _ => new WebPassDbContext(options),
                default));

        var exitCode = await AdminInitApplication.RunAsync(
            args,
            duplicate,
            _ => new WebPassDbContext(options),
            default);

        Assert.Equal(3, exitCode);
        Assert.Equal(
            "Username already exists.",
            Assert.Single(duplicate.Output));
        await using var verificationDb = new WebPassDbContext(options);
        Assert.Single(verificationDb.Users);
    }

    [Fact]
    public async Task Operational_failure_returns_one_without_sensitive_details()
    {
        var console = new FakeConsole("secret-value", "secret-value");

        var exitCode = await AdminInitApplication.RunAsync(
            [
                "--connection-string", "sensitive-connection",
                "--username", "admin",
            ],
            console,
            _ => throw new InvalidOperationException(
                "sensitive-connection secret-value"),
            default);

        Assert.Equal(1, exitCode);
        Assert.Equal(
            "Administrator creation failed. Verify database connectivity and permissions.",
            Assert.Single(console.Output));
    }

    private static DbContextOptions<WebPassDbContext> CreateOptions() =>
        new DbContextOptionsBuilder<WebPassDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

    private sealed class FakeConsole(params string[] secrets)
        : IAdminInitConsole
    {
        private readonly Queue<string> _secrets = new(secrets);
        public List<string> Output { get; } = [];

        public string ReadSecret(string prompt)
        {
            Assert.Equal(
                _secrets.Count == secrets.Length
                    ? "Password: "
                    : "Confirm password: ",
                prompt);
            return _secrets.Dequeue();
        }

        public void WriteLine(string message) => Output.Add(message);
    }

    private sealed class ThrowingConsole : IAdminInitConsole
    {
        public List<string> Output { get; } = [];

        public string ReadSecret(string prompt) =>
            throw new InvalidOperationException(
                "No interactive input.");

        public void WriteLine(string message) => Output.Add(message);
    }
}

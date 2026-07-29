using Microsoft.EntityFrameworkCore;
using WebPass.Web.Data;
using WebPass.Web.Infrastructure.Identity;

namespace WebPass.AdminInit;

public interface IAdminInitConsole
{
    string ReadSecret(string prompt);
    void WriteLine(string message);
}

public static class AdminInitApplication
{
    private const string Usage =
        "Usage: WebPass.AdminInit --connection-string <value> --username <value>";

    public static async Task<int> RunAsync(
        string[] args,
        IAdminInitConsole console,
        Func<string, WebPassDbContext> dbFactory,
        CancellationToken ct)
    {
        if (!TryParse(args, out var connectionString, out var username))
        {
            console.WriteLine(Usage);
            return 2;
        }

        string password;
        string confirmation;
        try
        {
            password = console.ReadSecret("Password: ");
            confirmation = console.ReadSecret("Confirm password: ");
        }
        catch (InvalidOperationException)
        {
            console.WriteLine(
                "Interactive password input is required.");
            return 2;
        }

        try
        {
            await using var db = dbFactory(connectionString);
            var initializer = new AdministratorInitializer(
                db,
                new Argon2PasswordHasher());
            var result = await initializer.CreateAsync(
                username,
                password,
                confirmation,
                ct);
            return WriteResult(console, result);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            console.WriteLine(
                "Administrator creation failed. Verify database connectivity and permissions.");
            return 1;
        }
    }

    private static bool TryParse(
        string[] args,
        out string connectionString,
        out string username)
    {
        connectionString = string.Empty;
        username = string.Empty;
        if (args.Length != 4)
        {
            return false;
        }

        var values = new Dictionary<string, string>(
            StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (args[index] is not ("--connection-string" or "--username")
                || string.IsNullOrEmpty(args[index + 1])
                || !values.TryAdd(args[index], args[index + 1]))
            {
                return false;
            }
        }

        if (!values.TryGetValue(
                "--connection-string",
                out var parsedConnectionString)
            || !values.TryGetValue(
                "--username",
                out var parsedUsername))
        {
            return false;
        }

        connectionString = parsedConnectionString;
        username = parsedUsername.Trim();
        return username.Length is > 0 and <= 128;
    }

    private static int WriteResult(
        IAdminInitConsole console,
        AdministratorInitializationResult result)
    {
        switch (result.Kind)
        {
            case AdministratorInitializationResultKind.Created:
                console.WriteLine(
                    $"Administrator '{result.Username}' created.");
                return 0;
            case AdministratorInitializationResultKind.DuplicateUsername:
                console.WriteLine("Username already exists.");
                return 3;
            case AdministratorInitializationResultKind.InvalidUsername:
                console.WriteLine(
                    "Username must contain 1 to 128 characters.");
                return 2;
            case AdministratorInitializationResultKind.InvalidPassword:
                console.WriteLine("Password must not be empty.");
                return 2;
            case AdministratorInitializationResultKind.PasswordMismatch:
                console.WriteLine(
                    "Password confirmation does not match.");
                return 2;
            default:
                throw new InvalidOperationException(
                    "Unknown initialization result.");
        }
    }
}

public sealed class SystemAdminInitConsole : IAdminInitConsole
{
    public string ReadSecret(string prompt)
    {
        if (Console.IsInputRedirected)
        {
            throw new InvalidOperationException(
                "Redirected password input is not supported.");
        }

        Console.Write(prompt);
        var characters = new List<char>();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return new string([.. characters]);
            }

            if (key.Key == ConsoleKey.Backspace && characters.Count > 0)
            {
                characters.RemoveAt(characters.Count - 1);
            }
            else if (!char.IsControl(key.KeyChar))
            {
                characters.Add(key.KeyChar);
            }
        }
    }

    public void WriteLine(string message) => Console.WriteLine(message);
}

internal static class Program
{
    public static Task<int> Main(string[] args) =>
        AdminInitApplication.RunAsync(
            args,
            new SystemAdminInitConsole(),
            connectionString =>
            {
                var options =
                    new DbContextOptionsBuilder<WebPassDbContext>()
                        .UseSqlServer(connectionString)
                        .Options;
                return new WebPassDbContext(options);
            },
            CancellationToken.None);
}

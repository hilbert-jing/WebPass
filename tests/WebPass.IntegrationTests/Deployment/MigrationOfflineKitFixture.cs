using System.Diagnostics;
using Xunit;

namespace WebPass.IntegrationTests.Deployment;

[CollectionDefinition(Name)]
public sealed class MigrationOfflineKitCollection
    : ICollectionFixture<MigrationOfflineKitFixture>
{
    public const string Name = "Migration offline kit";
}

public sealed class MigrationOfflineKitFixture : IAsyncLifetime
{
    private string? ownedRoot;

    public string RepositoryRoot { get; }

    public string KitPath { get; private set; } = string.Empty;

    public bool OwnsKit { get; private set; }

    public MigrationOfflineKitFixture()
    {
        RepositoryRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            ".."));
    }

    public async Task InitializeAsync()
    {
        var providedKit = Environment.GetEnvironmentVariable(
            "WEBPASS_MIGRATION_OFFLINE_KIT");
        if (!string.IsNullOrWhiteSpace(providedKit))
        {
            KitPath = Path.GetFullPath(providedKit);
            OwnsKit = false;
        }
        else
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "WebPassMigrationOfflineKitTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            KitPath = Path.Combine(root, "kit");
            OwnsKit = true;
            ownedRoot = root;

            var result = await RunAsync(
                "powershell.exe",
                [
                    "-NoProfile",
                    "-File",
                    Path.Combine(
                        RepositoryRoot,
                        "scripts",
                        "Prepare-WebPassMigrationOfflineKit.ps1"),
                    "-OutputPath",
                    KitPath,
                ],
                timeout: TimeSpan.FromMinutes(15));

            Assert.True(
                result.ExitCode == 0,
                $"Offline-kit preparation failed.{Environment.NewLine}" +
                $"{result.Error}{Environment.NewLine}{result.Output}");
        }
    }

    public Task DisposeAsync()
    {
        if (OwnsKit && ownedRoot is not null && Directory.Exists(ownedRoot))
        {
            Directory.Delete(ownedRoot, recursive: true);
        }

        return Task.CompletedTask;
    }

    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?>? environment = null,
        TimeSpan? timeout = null)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                process.StartInfo.Environment[key] = value;
            }
        }

        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var cancellation = new CancellationTokenSource(
            timeout ?? TimeSpan.FromMinutes(5));
        try
        {
            await process.WaitForExitAsync(cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync();
        }

        return new(
            process.ExitCode,
            await output,
            await error);
    }
}

public sealed record ProcessResult(
    int ExitCode,
    string Output,
    string Error);

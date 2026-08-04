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

public sealed class MigrationOfflineKitPreparationScriptTests
{
    [Fact]
    public async Task Preparation_script_rejects_repository_root_before_mutation()
    {
        var fixture = new MigrationOfflineKitFixture();
        var result = await MigrationOfflineKitFixture.RunAsync(
            "powershell.exe",
            [
                "-NoProfile",
                "-File",
                Path.Combine(
                    fixture.RepositoryRoot,
                    "scripts",
                    "Prepare-WebPassMigrationOfflineKit.ps1"),
                "-OutputPath",
                fixture.RepositoryRoot,
                "-Force",
            ]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "The offline-kit output cannot be the repository or its parent.",
            result.Error + result.Output,
            StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(
            fixture.RepositoryRoot,
            "WebPass.sln")));
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MigrationSourceGuardCollection
{
    public const string Name = "Migration source guard";
}

[Collection(MigrationSourceGuardCollection.Name)]
public sealed class MigrationSourceGuardTests
{
    [Fact]
    public async Task Bundle_script_rejects_an_ignored_sdk_compiled_source_file()
    {
        await AssertBundleScriptRejectsAsync("src/WebPass.Web");
    }

    [Fact]
    public async Task Bundle_script_rejects_an_ignored_source_file_in_nested_bin()
    {
        await AssertBundleScriptRejectsAsync("src/WebPass.Web/Application/bin");
    }

    [Fact]
    public async Task Preparation_script_rejects_an_ignored_sdk_compiled_source_file()
    {
        await AssertPreparationScriptRejectsAsync("src/WebPass.Web");
    }

    [Fact]
    public async Task Preparation_script_rejects_an_ignored_source_file_in_nested_bin()
    {
        await AssertPreparationScriptRejectsAsync(
            "src/WebPass.Web/Application/bin");
    }

    private static async Task AssertBundleScriptRejectsAsync(
        string relativeSourceDirectory)
    {
        var fixture = new MigrationOfflineKitFixture();
        var output = Path.Combine(
            Path.GetTempPath(),
            $"WebPassIgnoredSourceBuild{Guid.NewGuid():N}.exe");

        try
        {
            await AssertIgnoredSourceIsRejectedAsync(
                fixture.RepositoryRoot,
                "Build-WebPassMigrationBundle.ps1",
                [
                    "-OfflineKitPath",
                    Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
                    "-OutputPath",
                    output,
                ],
                relativeSourceDirectory);

            Assert.False(File.Exists(output));
        }
        finally
        {
            File.Delete(output);
        }
    }

    private static async Task AssertPreparationScriptRejectsAsync(
        string relativeSourceDirectory)
    {
        var fixture = new MigrationOfflineKitFixture();
        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "WebPassIgnoredSourcePreparationTests",
            Guid.NewGuid().ToString("N"));
        var output = Path.Combine(temporaryDirectory, "kit");
        var shimDirectory = Path.Combine(temporaryDirectory, "shim");
        Directory.CreateDirectory(shimDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(shimDirectory, "dotnet.cmd"),
            "@echo 0.0.0\r\n");

        try
        {
            await AssertIgnoredSourceIsRejectedAsync(
                fixture.RepositoryRoot,
                "Prepare-WebPassMigrationOfflineKit.ps1",
                ["-OutputPath", output],
                relativeSourceDirectory,
                new Dictionary<string, string?>
                {
                    ["PATH"] = shimDirectory + Path.PathSeparator +
                        Environment.GetEnvironmentVariable("PATH"),
                });

            Assert.False(Directory.Exists(output));
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    private static async Task AssertIgnoredSourceIsRejectedAsync(
        string repositoryRoot,
        string scriptName,
        IReadOnlyList<string> scriptArguments,
        string relativeSourceDirectory,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        var sourceId = Guid.NewGuid().ToString("N")[..12];
        var relativeSource =
            $"{relativeSourceDirectory}/Injected{sourceId}.cs";
        var sourceFile = Path.Combine(
            repositoryRoot,
            relativeSource.Replace('/', Path.DirectorySeparatorChar));
        var sourceDirectory = Path.GetDirectoryName(sourceFile)!;
        var sourceDirectoryExisted = Directory.Exists(sourceDirectory);
        var gitPath = await MigrationOfflineKitFixture.RunAsync(
            "git",
            ["-C", repositoryRoot, "rev-parse", "--git-path", "info/exclude"]);
        Assert.Equal(0, gitPath.ExitCode);
        var excludeFile = gitPath.Output.Trim();
        if (!Path.IsPathRooted(excludeFile))
        {
            excludeFile = Path.GetFullPath(excludeFile, repositoryRoot);
        }

        var excludeExisted = File.Exists(excludeFile);
        var originalExclude = excludeExisted
            ? await File.ReadAllBytesAsync(excludeFile)
            : null;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(excludeFile)!);
            await File.AppendAllTextAsync(
                excludeFile,
                $"{Environment.NewLine}/{relativeSource}{Environment.NewLine}");
            Directory.CreateDirectory(sourceDirectory);
            await File.WriteAllTextAsync(
                sourceFile,
                "namespace WebPass.Web; internal sealed class Injected { }");

            var ignored = await MigrationOfflineKitFixture.RunAsync(
                "git",
                ["-C", repositoryRoot, "check-ignore", "--", relativeSource]);
            Assert.Equal(0, ignored.ExitCode);

            var arguments = new List<string>
            {
                "-NoProfile",
                "-File",
                Path.Combine(repositoryRoot, "scripts", scriptName),
            };
            arguments.AddRange(scriptArguments);
            var result = await MigrationOfflineKitFixture.RunAsync(
                "powershell.exe",
                arguments,
                environment,
                timeout: TimeSpan.FromSeconds(30));

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains(
                "The WebPass source tree contains unreviewed changes:",
                result.Error + result.Output,
                StringComparison.Ordinal);
            Assert.Contains(
                relativeSource,
                result.Error + result.Output,
                StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(sourceFile);
            if (!sourceDirectoryExisted && Directory.Exists(sourceDirectory))
            {
                Directory.Delete(sourceDirectory, recursive: false);
            }
            if (excludeExisted)
            {
                await File.WriteAllBytesAsync(excludeFile, originalExclude!);
            }
            else
            {
                File.Delete(excludeFile);
            }
        }
    }
}

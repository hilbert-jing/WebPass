using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebPass.Web.Data;
using Xunit;

namespace WebPass.IntegrationTests.Deployment;

[Collection(MigrationOfflineKitCollection.Name)]
public sealed class MigrationBundleTests(
    MigrationOfflineKitFixture offlineKit)
{
    [Fact]
    public async Task Preparation_script_creates_a_valid_offline_kit()
    {
        Assert.True(File.Exists(Path.Combine(
            offlineKit.KitPath,
            "manifest.json")));
        Assert.True(File.Exists(Path.Combine(
            offlineKit.KitPath,
            "NuGet.Config")));
        Assert.True(File.Exists(Path.Combine(
            offlineKit.KitPath,
            "tools",
            "dotnet-ef.exe")));
        Assert.NotEmpty(Directory.EnumerateFiles(
            Path.Combine(offlineKit.KitPath, "feed"),
            "*.nupkg"));
        Assert.NotEmpty(Directory.EnumerateDirectories(
            Path.Combine(offlineKit.KitPath, "packages")));

        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(offlineKit.KitPath, "manifest.json")));
        var root = manifest.RootElement;
        Assert.Equal(1, root.GetProperty("formatVersion").GetInt32());
        Assert.Equal(
            "10.0.0",
            root.GetProperty("dotnetEfVersion").GetString());
        Assert.Equal(10, root.GetProperty("sdkMajorVersion").GetInt32());
        Assert.Equal(
            "win-x64",
            root.GetProperty("targetRuntime").GetString());

        var config = await File.ReadAllTextAsync(Path.Combine(
            offlineKit.KitPath,
            "NuGet.Config"));
        Assert.Contains("<clear />", config, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "http://",
            config,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "https://",
            config,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Script_builds_bundle_and_bundle_applies_all_migrations()
    {
        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "WebPassMigrationBundleTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        var bundle = Path.Combine(
            temporaryDirectory,
            "WebPass.Migrations.exe");
        var databaseName =
            "WebPassBundle_" + Guid.NewGuid().ToString("N");
        var connection =
            $"Server=localhost\\SQLEXPRESS;Database={databaseName};Integrated Security=True;TrustServerCertificate=True";
        var options = new DbContextOptionsBuilder<WebPassDbContext>()
            .UseSqlServer(connection)
            .Options;

        try
        {
            var build = await MigrationOfflineKitFixture.RunAsync(
                "powershell.exe",
                [
                    "-NoProfile",
                    "-File",
                    Path.Combine(
                        offlineKit.RepositoryRoot,
                        "scripts",
                        "Build-WebPassMigrationBundle.ps1"),
                    "-OfflineKitPath",
                    offlineKit.KitPath,
                    "-OutputPath",
                    bundle,
                ],
                timeout: TimeSpan.FromMinutes(10));
            Assert.True(
                build.ExitCode == 0,
                $"Bundle build failed.{Environment.NewLine}{build.Error}{Environment.NewLine}{build.Output}");
            Assert.DoesNotContain(
                "http://",
                build.Output,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                "https://",
                build.Output,
                StringComparison.OrdinalIgnoreCase);
            Assert.True(
                File.Exists(bundle),
                $"Bundle missing: {bundle}");

            var migrate = await MigrationOfflineKitFixture.RunAsync(
                bundle,
                [
                    "--connection",
                    connection,
                ]);
            Assert.True(
                migrate.ExitCode == 0,
                $"Bundle execution failed.{Environment.NewLine}{migrate.Error}{Environment.NewLine}{migrate.Output}");

            await using var db = new WebPassDbContext(options);
            Assert.Equal(
                db.Database.GetMigrations().Order(),
                (await db.Database.GetAppliedMigrationsAsync()).Order());
        }
        finally
        {
            await using var cleanup = new WebPassDbContext(options);
            await cleanup.Database.EnsureDeletedAsync();
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(
                    temporaryDirectory,
                    recursive: true);
            }
        }
    }

    [Fact]
    public async Task Offline_build_rejects_an_incomplete_kit_without_output()
    {
        var kit = await NewInvalidKitAsync(
            offlineKit.RepositoryRoot,
            _ => { });
        var output = Path.Combine(kit, "output.exe");

        try
        {
            var build = await RunBuildAsync(offlineKit.RepositoryRoot, kit, output);

            Assert.NotEqual(0, build.ExitCode);
            Assert.Contains(
                "Offline migration kit file is missing:",
                build.Error + build.Output,
                StringComparison.Ordinal);
            Assert.False(File.Exists(output));
        }
        finally
        {
            Directory.Delete(kit, recursive: true);
        }
    }

    [Fact]
    public async Task Offline_build_rejects_the_wrong_tool_version_without_output()
    {
        var kit = await NewInvalidKitAsync(
            offlineKit.RepositoryRoot,
            manifest => manifest["dotnetEfVersion"] = "9.0.0");
        var output = Path.Combine(kit, "output.exe");
        await File.WriteAllTextAsync(Path.Combine(
            kit,
            "tools",
            "dotnet-ef.exe"), "not executed");

        try
        {
            var build = await RunBuildAsync(offlineKit.RepositoryRoot, kit, output);

            Assert.NotEqual(0, build.ExitCode);
            Assert.Contains(
                "Offline migration kit dotnet-ef version must be 10.0.0.",
                build.Error + build.Output,
                StringComparison.Ordinal);
            Assert.False(File.Exists(output));
        }
        finally
        {
            Directory.Delete(kit, recursive: true);
        }
    }

    [Fact]
    public async Task Offline_build_rejects_a_source_commit_mismatch_without_output()
    {
        var kit = await NewInvalidKitAsync(
            offlineKit.RepositoryRoot,
            manifest => manifest["sourceCommit"] = new string('0', 40));
        var output = Path.Combine(kit, "output.exe");
        await File.WriteAllTextAsync(Path.Combine(
            kit,
            "tools",
            "dotnet-ef.exe"), "not executed");

        try
        {
            var build = await RunBuildAsync(offlineKit.RepositoryRoot, kit, output);

            Assert.NotEqual(0, build.ExitCode);
            Assert.Contains(
                "Offline migration kit source commit does not match the current source.",
                build.Error + build.Output,
                StringComparison.Ordinal);
            Assert.False(File.Exists(output));
        }
        finally
        {
            Directory.Delete(kit, recursive: true);
        }
    }

    [Fact]
    public async Task Offline_build_fails_on_missing_packages_without_http_fallback()
    {
        var kit = await NewInvalidKitAsync(
            offlineKit.RepositoryRoot,
            _ => { });
        var output = Path.Combine(kit, "output.exe");
        CopyDirectory(
            Path.Combine(offlineKit.KitPath, "tools"),
            Path.Combine(kit, "tools"));

        try
        {
            var build = await RunBuildAsync(offlineKit.RepositoryRoot, kit, output);

            Assert.NotEqual(0, build.ExitCode);
            Assert.Contains(
                "Offline restore failed with exit code",
                build.Error + build.Output,
                StringComparison.Ordinal);
            Assert.False(File.Exists(output));
            Assert.DoesNotContain(
                "http://",
                build.Output,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                "https://",
                build.Output,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                "http://",
                build.Error,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                "https://",
                build.Error,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(kit, recursive: true);
        }
    }

    private static async Task<string> NewInvalidKitAsync(
        string repositoryRoot,
        Action<Dictionary<string, object?>> mutate)
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "WebPassInvalidMigrationKits",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(path, "tools"));
        Directory.CreateDirectory(Path.Combine(path, "packages"));
        Directory.CreateDirectory(Path.Combine(path, "feed"));
        await File.WriteAllTextAsync(
            Path.Combine(path, "NuGet.Config"),
            "<configuration><packageSources><clear />" +
            "<add key=\"WebPassOffline\" value=\"feed\" />" +
            "</packageSources></configuration>");
        var commit = (await MigrationOfflineKitFixture.RunAsync(
            "git",
            ["-C", repositoryRoot, "rev-parse", "HEAD"])).Output.Trim();
        var manifest = new Dictionary<string, object?>
        {
            ["formatVersion"] = 1,
            ["sourceCommit"] = commit,
            ["dotnetEfVersion"] = "10.0.0",
            ["sdkMajorVersion"] = 10,
            ["targetRuntime"] = "win-x64",
            ["createdAtUtc"] = DateTimeOffset.UtcNow,
        };
        mutate(manifest);
        await File.WriteAllTextAsync(
            Path.Combine(path, "manifest.json"),
            JsonSerializer.Serialize(manifest));
        return path;
    }

    private static Task<ProcessResult> RunBuildAsync(
        string repositoryRoot,
        string kit,
        string output)
    {
        return MigrationOfflineKitFixture.RunAsync(
            "powershell.exe",
            [
                "-NoProfile",
                "-File",
                Path.Combine(
                    repositoryRoot,
                    "scripts",
                    "Build-WebPassMigrationBundle.ps1"),
                "-OfflineKitPath",
                kit,
                "-OutputPath",
                output,
            ]);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(
                file,
                Path.Combine(destination, Path.GetFileName(file)));
        }
        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            var attributes = File.GetAttributes(directory);
            Assert.False(attributes.HasFlag(FileAttributes.ReparsePoint));
            CopyDirectory(
                directory,
                Path.Combine(destination, Path.GetFileName(directory)));
        }
    }
}

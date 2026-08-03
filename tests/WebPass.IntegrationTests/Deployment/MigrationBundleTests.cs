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
        var repositoryRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            ".."));
        var script = Path.Combine(
            repositoryRoot,
            "scripts",
            "Build-WebPassMigrationBundle.ps1");
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
                    script,
                    "-OutputPath",
                    bundle,
                ]);
            Assert.True(
                build.ExitCode == 0,
                $"Bundle build failed.{Environment.NewLine}{build.Error}{Environment.NewLine}{build.Output}");
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
}

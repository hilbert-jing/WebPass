using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using WebPass.Web.Data;
using Xunit;

namespace WebPass.IntegrationTests.Deployment;

public sealed class MigrationBundleTests
{
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
            var build = await RunAsync(
                "powershell.exe",
                "-NoProfile",
                "-File",
                script,
                "-OutputPath",
                bundle);
            Assert.True(
                build.ExitCode == 0,
                $"Bundle build failed.{Environment.NewLine}{build.Error}{Environment.NewLine}{build.Output}");
            Assert.True(
                File.Exists(bundle),
                $"Bundle missing: {bundle}");

            var migrate = await RunAsync(
                bundle,
                "--connection",
                connection);
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

    private static async Task<ProcessResult> RunAsync(
        string fileName,
        params string[] arguments)
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

        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new(
            process.ExitCode,
            await output,
            await error);
    }

    private sealed record ProcessResult(
        int ExitCode,
        string Output,
        string Error);
}

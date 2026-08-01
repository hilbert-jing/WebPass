using System.Diagnostics;
using Xunit;

namespace WebPass.IntegrationTests.Presentation;

public sealed class SecretRevealDomTests
{
    private static readonly TimeSpan HarnessTimeout = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task Reveal_script_enforces_sensitive_value_lifecycle_in_an_executable_dom()
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            ".."));
        var harness = Path.Combine(
            repositoryRoot,
            "tests",
            "WebPass.IntegrationTests",
            "Presentation",
            "secret-reveal.dom.test.js");
        var productionScript = Path.Combine(
            repositoryRoot,
            "src",
            "WebPass.Web",
            "wwwroot",
            "js",
            "secret-reveal.js");

        var result = await RunNodeAsync(harness, productionScript);

        Assert.True(
            result.ExitCode == 0,
            $"Secret reveal DOM harness failed.{Environment.NewLine}{result.Error}{Environment.NewLine}{result.Output}");
        Assert.Contains(
            "secret-reveal DOM tests passed",
            result.Output,
            StringComparison.Ordinal);
    }

    private static async Task<ProcessResult> RunNodeAsync(
        string harness,
        string productionScript)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "node",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        process.StartInfo.ArgumentList.Add(harness);
        process.StartInfo.ArgumentList.Add(productionScript);

        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(HarnessTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            return new(
                -1,
                await output,
                $"Node harness timed out after {HarnessTimeout.TotalSeconds:0} seconds.{Environment.NewLine}{await error}");
        }
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

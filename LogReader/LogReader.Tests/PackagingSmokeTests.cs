namespace LogReader.Tests;

using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;

public sealed class PackagingSmokeTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "LogReaderPackagingTests_" + Guid.NewGuid().ToString("N")[..8]);

    public PackagingSmokeTests()
    {
        Directory.CreateDirectory(_testRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, true);
        }
    }

    [Fact]
    public async Task BuildReleaseScript_WithPublishRoot_EmitsMsiAndPortableArtifacts()
    {
        var publishRoot = Path.Combine(_testRoot, "publish");
        var artifactsRoot = Path.Combine(_testRoot, "artifacts");
        Directory.CreateDirectory(publishRoot);
        File.WriteAllText(Path.Combine(publishRoot, "LogReader.App.exe"), "stub executable");
        File.WriteAllText(Path.Combine(publishRoot, "LogReader.App.dll"), "stub library");

        var scriptPath = Path.Combine(GetProductRoot(), "installer", "Build-Release.ps1");
        var result = await RunPowerShellAsync(
            $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" -PublishRoot \"{publishRoot}\" -ArtifactsRoot \"{artifactsRoot}\"");

        Assert.True(
            result.ExitCode == 0,
            $"Packaging script failed with exit code {result.ExitCode}.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.StandardOutput}{Environment.NewLine}STDERR:{Environment.NewLine}{result.StandardError}");

        var outputRoot = Path.Combine(artifactsRoot, "output");
        var msiPath = Assert.Single(Directory.GetFiles(outputRoot, "*.msi"));
        var zipPath = Assert.Single(Directory.GetFiles(outputRoot, "*.zip"));

        Assert.EndsWith(".msi", msiPath, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("-portable.zip", zipPath, StringComparison.OrdinalIgnoreCase);

        using var archive = ZipFile.OpenRead(zipPath);
        var runtimeConfigurationEntry = archive.Entries.Single(entry =>
            entry.FullName.EndsWith("LogReader.runtime.json", StringComparison.OrdinalIgnoreCase));

        using var runtimeConfigurationStream = runtimeConfigurationEntry.Open();
        using var document = await JsonDocument.ParseAsync(runtimeConfigurationStream);

        Assert.Equal(@".\LogReaderData", document.RootElement.GetProperty("storageRoot").GetString());
    }

    private static string GetProductRoot()
    {
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    }

    private static async Task<ProcessResult> RunPowerShellAsync(string arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = GetProductRoot()
        };

        using var process = new Process
        {
            StartInfo = startInfo
        };

        process.Start();

        var standardOutputTask = process.StandardOutput.ReadToEndAsync();
        var standardErrorTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();

        return new ProcessResult(
            process.ExitCode,
            await standardOutputTask,
            await standardErrorTask);
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}

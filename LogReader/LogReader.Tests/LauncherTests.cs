namespace LogReader.Tests;

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using LogReader.Launcher;

public sealed class LauncherTests
{
    [Theory]
    [InlineData("../1.2.3")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("1.2.3-beta")]
    [InlineData("1.2.3/other")]
    [InlineData("99999999999.2.3")]
    [InlineData("")]
    public void Resolver_RejectsUnsafeOrInvalidVersions(string version)
    {
        using var fixture = new Deployment();
        File.WriteAllText(fixture.Manifest, JsonSerializer.Serialize(new { version }));
        Assert.Throws<InvalidDataException>(() => ReleaseResolver.Resolve(fixture.Root));
    }

    [Theory]
    [InlineData("{invalid")]
    [InlineData("{}")]
    [InlineData("{\"version\":12}")]
    [InlineData("[]")]
    public void Resolver_RejectsMalformedManifests(string json)
    {
        using var fixture = new Deployment();
        File.WriteAllText(fixture.Manifest, json);
        Assert.ThrowsAny<Exception>(() => ReleaseResolver.Resolve(fixture.Root));
    }

    [Fact]
    public void Resolver_SelectsPointerAndPreservesArguments()
    {
        using var fixture = new Deployment();
        Directory.CreateDirectory(Path.Combine(fixture.Root, "releases", "9.9.9"));
        var target = ReleaseResolver.Resolve(fixture.Root);
        var arguments = new[] { "space here", "a\"b", @"C:\folder\", "" };
        var start = target.CreateStartInfo(arguments);
        Assert.Equal(fixture.Release, start.WorkingDirectory);
        Assert.Equal(Path.Combine(fixture.Release, "WeezTail.exe"), start.FileName);
        Assert.Equal(arguments, start.ArgumentList);
        Assert.False(start.UseShellExecute);
        Assert.EndsWith("WeezTail.Mcp.exe", ReleaseResolver.Resolve(fixture.Root, mcp: true).ExecutablePath);
    }

    [Theory]
    [InlineData("current.json")]
    [InlineData("releases/1.2.3/WeezTail.exe")]
    [InlineData("releases/1.2.3/WeezTail.install.json")]
    public void Resolver_RejectsIncompleteDeployment(string missing)
    {
        using var fixture = new Deployment();
        File.Delete(Path.Combine(fixture.Root, missing));
        Assert.ThrowsAny<IOException>(() => ReleaseResolver.Resolve(fixture.Root));
    }

    [Theory]
    [InlineData("ExeDirectory")]
    [InlineData("Absolute")]
    public void Resolver_RejectsWrongStorage(string mode)
    {
        using var fixture = new Deployment();
        File.WriteAllText(Path.Combine(fixture.Release, "WeezTail.install.json"),
            JsonSerializer.Serialize(new { installMode = "Portable", storageMode = mode }));
        Assert.Throws<InvalidDataException>(() => ReleaseResolver.Resolve(fixture.Root));
    }

    [Fact]
    public void Resolver_RejectsReleaseJunction()
    {
        using var fixture = new Deployment();
        var redirect = Path.Combine(fixture.Root, "releases", "2.0.0");
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("mklink");
        start.ArgumentList.Add("/J");
        start.ArgumentList.Add(redirect);
        start.ArgumentList.Add(fixture.Release);
        using var process = Process.Start(start)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        try
        {
            File.WriteAllText(fixture.Manifest, "{\"version\":\"2.0.0\"}");
            Assert.Throws<InvalidDataException>(() => ReleaseResolver.Resolve(fixture.Root));
        }
        finally
        {
            Directory.Delete(redirect);
        }
    }

    [Theory]
    [InlineData("missing manifest")]
    [InlineData("malformed manifest")]
    [InlineData("missing executable")]
    [InlineData("wrong storage")]
    public void Desktop_Run_ReportsDeploymentErrorsWithoutOpeningUI(string scenario)
    {
        using var fixture = new Deployment();
        switch (scenario)
        {
            case "missing manifest": File.Delete(fixture.Manifest); break;
            case "malformed manifest": File.WriteAllText(fixture.Manifest, "{invalid"); break;
            case "missing executable": File.Delete(Path.Combine(fixture.Release, "WeezTail.exe")); break;
            case "wrong storage":
                File.WriteAllText(Path.Combine(fixture.Release, "WeezTail.install.json"),
                    "{\"installMode\":\"Portable\",\"storageMode\":\"ExeDirectory\"}");
                break;
        }
        var cause = Record.Exception(() => ReleaseResolver.Resolve(fixture.Root));
        Assert.NotNull(cause);
        var reports = new List<(string Message, string Title)>();
        var started = false;
        var exit = LogReader.Launcher.Desktop.Program.Run([], fixture.Root,
            (message, title) => reports.Add((message, title)), _ =>
            {
                started = true;
                throw new InvalidOperationException("Invalid deployment must not start a process.");
            });

        Assert.Equal(1, exit);
        Assert.False(started);
        var report = Assert.Single(reports);
        Assert.Equal("WeezTail Launcher", report.Title);
        Assert.Equal($"WeezTail could not launch from:{Environment.NewLine}{fixture.Root}" +
            $"{Environment.NewLine}{Environment.NewLine}{cause.Message}", report.Message);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("io")]
    [InlineData("win32")]
    public void Desktop_Run_ReportsProcessStartFailuresWithoutOpeningUI(string scenario)
    {
        using var fixture = new Deployment();
        Exception? cause = scenario switch
        {
            "io" => new IOException("Synthetic process I/O failure"),
            "win32" => new System.ComponentModel.Win32Exception(5, "Synthetic process access failure"),
            _ => null
        };
        var reports = new List<(string Message, string Title)>();
        var exit = LogReader.Launcher.Desktop.Program.Run([], fixture.Root,
            (message, title) => reports.Add((message, title)), _ =>
            {
                if (cause != null) throw cause;
                return null;
            });

        Assert.Equal(1, exit);
        var report = Assert.Single(reports);
        Assert.Equal("WeezTail Launcher", report.Title);
        Assert.Contains(fixture.Root, report.Message);
        Assert.EndsWith(cause?.Message ?? $"Could not start '{Path.Combine(fixture.Release, "WeezTail.exe")}'.", report.Message);
    }

    [Fact]
    public void Desktop_Run_PassesLauncherPathOnlyToChildEnvironment()
    {
        using var fixture = new Deployment();
        var original = Environment.GetEnvironmentVariable("WEEZTAIL_DESKTOP_LAUNCHER_PATH");
        ProcessStartInfo? captured = null;
        var arguments = new[] { "space here", "a\"b", "" };
        var exit = LogReader.Launcher.Desktop.Program.Run(arguments, fixture.Root,
            (_, _) => { }, start => { captured = start; return new Process(); });

        Assert.Equal(0, exit);
        Assert.NotNull(captured);
        Assert.Equal(Path.Combine(fixture.Root, "WeezTail.Launcher.exe"),
            captured.Environment["WEEZTAIL_DESKTOP_LAUNCHER_PATH"]);
        Assert.Equal(original, Environment.GetEnvironmentVariable("WEEZTAIL_DESKTOP_LAUNCHER_PATH"));
        Assert.Equal(arguments, captured.ArgumentList);
    }

    [Fact]
    public async Task Desktop_DetachesAndPassesArgumentsAndWorkingDirectory()
    {
        using var fixture = new Deployment("desktop");
        var arguments = new[] { "space here", "a\"b", @"C:\folder\", "" };
        using var process = fixture.Start(mcp: false, arguments);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(0, process.ExitCode);
        var started = await fixture.ReadStartedAsync();
        Assert.Equal(fixture.Release, started.GetProperty("cwd").GetString());
        Assert.Equal(arguments, started.GetProperty("args").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(Path.Combine(fixture.Root, "WeezTail.Launcher.exe"), started.GetProperty("launcherPath").GetString());
        using var child = Process.GetProcessById(started.GetProperty("pid").GetInt32());
        Assert.False(child.HasExited);
        File.WriteAllText(Path.Combine(fixture.Release, "stop.txt"), "");
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task Desktop_StableRelaunchCommandSelectsPromotedRelease()
    {
        using var fixture = new Deployment("desktop");
        var launcher = Path.Combine(fixture.Root, "WeezTail.Launcher.exe");
        var firstInfo = LogReader.App.Services.TaskbarRelaunchInfo.TryCreate(launcher, fixture.Release)!;
        using var first = fixture.Start(mcp: false);
        await first.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(0, first.ExitCode);
        await fixture.ReadStartedAsync();

        var nextRelease = Path.Combine(fixture.Root, "releases", "2.0.0");
        Directory.CreateDirectory(nextRelease);
        foreach (var file in Directory.GetFiles(fixture.Release))
            if (Path.GetFileName(file) != "started.json")
                File.Copy(file, Path.Combine(nextRelease, Path.GetFileName(file)));
        var temporaryPointer = fixture.Manifest + ".tmp";
        File.WriteAllText(temporaryPointer, "{\"version\":\"2.0.0\"}");
        File.Replace(temporaryPointer, fixture.Manifest, null);

        // Windows stores the launcher command, rather than a versioned payload path.
        Assert.Equal($"\"{launcher}\"", firstInfo.Command);
        using var next = fixture.Start(mcp: false);
        await next.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(0, next.ExitCode);
        var started = await fixture.ReadStartedAsync(nextRelease);
        Assert.Equal(nextRelease, started.GetProperty("cwd").GetString());
        var nextInfo = LogReader.App.Services.TaskbarRelaunchInfo.TryCreate(
            started.GetProperty("launcherPath").GetString(), nextRelease)!;
        Assert.Equal(firstInfo.Command, nextInfo.Command);
        Assert.Equal(firstInfo.AppUserModelId, nextInfo.AppUserModelId);
    }

    [Fact]
    [Trait("Category", "InteractiveDesktop")]
    public async Task Desktop_ShowsDeploymentErrorAndReturnsFailure()
    {
        InteractiveDesktop.RequireAvailable();
        using var fixture = new Deployment();
        File.Delete(fixture.Manifest);
        using var process = fixture.Start(mcp: false);
        var deadline = DateTime.UtcNow.AddSeconds(20);
        IntPtr dialog = IntPtr.Zero;
        while (DateTime.UtcNow < deadline && dialog == IntPtr.Zero)
        {
            process.Refresh();
            dialog = process.MainWindowHandle;
            if (dialog == IntPtr.Zero) await Task.Delay(25);
        }
        Assert.NotEqual(IntPtr.Zero, dialog);
        var text = new StringBuilder();
        EnumChildWindows(dialog, (window, _) =>
        {
            var caption = new StringBuilder(4096);
            GetWindowText(window, caption, caption.Capacity);
            text.AppendLine(caption.ToString());
            return true;
        }, IntPtr.Zero);
        Assert.True(PostMessage(dialog, 0x0010, IntPtr.Zero, IntPtr.Zero)); // WM_CLOSE
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(1, process.ExitCode);
        Assert.Contains(fixture.Root, text.ToString());
        Assert.Contains("current.json", text.ToString());
    }

    [Fact]
    public async Task Mcp_PromotionAffectsNewLaunchesWhileExistingSessionContinues()
    {
        using var fixture = new Deployment("wait");
        using var first = fixture.Start();
        await fixture.ReadStartedAsync();
        var nextRelease = Path.Combine(fixture.Root, "releases", "2.0.0");
        Directory.CreateDirectory(nextRelease);
        foreach (var file in Directory.GetFiles(fixture.Release))
            if (Path.GetFileName(file) != "started.json")
                File.Copy(file, Path.Combine(nextRelease, Path.GetFileName(file)));
        File.WriteAllText(Path.Combine(nextRelease, "mode.txt"), "echo");
        var temporaryPointer = fixture.Manifest + ".tmp";
        File.WriteAllText(temporaryPointer, "{\"version\":\"2.0.0\"}");
        File.Replace(temporaryPointer, fixture.Manifest, null);
        using var second = fixture.Start();
        var started = await fixture.ReadStartedAsync(nextRelease);
        Assert.Equal(nextRelease, started.GetProperty("cwd").GetString());
        Assert.False(first.HasExited);
        second.StandardInput.Close();
        await second.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(0, second.ExitCode);
        first.Kill();
        await first.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Theory]
    [InlineData("echo")]
    [InlineData("flood")]
    public async Task Mcp_RelaysBinaryStreamsAndStopsOnEof(string mode)
    {
        using var fixture = new Deployment(mode);
        using var process = fixture.Start();
        var output = ReadBytesAsync(process.StandardOutput.BaseStream);
        var error = ReadBytesAsync(process.StandardError.BaseStream);
        var bytes = Enumerable.Range(0, 196608).Select(i => (byte)(i * 17)).ToArray();
        await process.StandardInput.BaseStream.WriteAsync(bytes).AsTask().WaitAsync(TimeSpan.FromSeconds(20));
        process.StandardInput.Close();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        var prefix = mode == "flood" ? Enumerable.Range(0, 131072).Select(i => (byte)i).ToArray() : [];
        Assert.Equal(prefix.Concat(bytes), await output);
        Assert.Equal(prefix, await error);
        Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    public async Task Mcp_PropagatesEarlyChildExitWithoutWaitingForClientEof()
    {
        using var fixture = new Deployment("earlyExit");
        using var process = fixture.Start();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(37, process.ExitCode);
        Assert.Equal("", await process.StandardOutput.ReadToEndAsync());
        Assert.Equal("", await process.StandardError.ReadToEndAsync());
    }

    [Fact]
    public async Task Mcp_KillsUnresponsiveChildAfterEof()
    {
        using var fixture = new Deployment("wait");
        using var process = fixture.Start();
        var started = await fixture.ReadStartedAsync();
        using var child = Process.GetProcessById(started.GetProperty("pid").GetInt32());
        process.StandardInput.Close();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(1, process.ExitCode);
        Assert.Contains("five seconds", await process.StandardError.ReadToEndAsync());
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Mcp_ForcedParentTerminationKillsOnlyItsChild()
    {
        using var first = new Deployment("wait");
        using var second = new Deployment("echo");
        using var parent = first.Start();
        using var other = second.Start();
        var started = await first.ReadStartedAsync();
        using var child = Process.GetProcessById(started.GetProperty("pid").GetInt32());
        await second.ReadStartedAsync();
        parent.Kill(); // Deliberately do not request a tree kill: the job owns cleanup.
        await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(other.HasExited);
        other.StandardInput.Close();
        await other.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(0, other.ExitCode);
    }

    [Fact]
    public async Task Mcp_RejectsArgumentsAndReportsDeploymentErrorsOnlyOnStderr()
    {
        using var fixture = new Deployment();
        using (var invalid = fixture.Start(arguments: ["invalid"]))
        {
            await invalid.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(2, invalid.ExitCode);
            Assert.Equal("", await invalid.StandardOutput.ReadToEndAsync());
            Assert.Contains("arguments", await invalid.StandardError.ReadToEndAsync());
        }
        File.Delete(fixture.Manifest);
        using var missing = fixture.Start();
        await missing.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(1, missing.ExitCode);
        Assert.Equal("", await missing.StandardOutput.ReadToEndAsync());
        Assert.Contains(fixture.Root, await missing.StandardError.ReadToEndAsync());
    }

    private static async Task<byte[]> ReadBytesAsync(Stream stream)
    {
        using var bytes = new MemoryStream();
        await stream.CopyToAsync(bytes);
        return bytes.ToArray();
    }

    private delegate bool WindowCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(IntPtr parent, WindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int maximumCount);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    private sealed class Deployment : IDisposable
    {
        private readonly List<Process> _processes = [];
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "WeezTail-launcher-tests", Guid.NewGuid().ToString("N"));
        public string Release => Path.Combine(Root, "releases", "1.2.3");
        public string Manifest => Path.Combine(Root, "current.json");

        public Deployment(string mode = "echo")
        {
            Directory.CreateDirectory(Release);
            File.WriteAllText(Manifest, "{\"version\":\"1.2.3\"}");
            File.WriteAllText(Path.Combine(Release, "WeezTail.install.json"),
                "{\"installMode\":\"Portable\",\"storageMode\":\"LocalAppData\"}");
            File.WriteAllText(Path.Combine(Release, "mode.txt"), mode);
            var source = Path.GetDirectoryName(typeof(LogReader.Launcher.TestHost.Program).Assembly.Location)!;
            CopyHost(source, "LogReader.Launcher.TestHost", Release);
            File.Copy(Path.Combine(source, "LogReader.Launcher.TestHost.exe"), Path.Combine(Release, "WeezTail.exe"));
            File.Copy(Path.Combine(source, "LogReader.Launcher.TestHost.exe"), Path.Combine(Release, "WeezTail.Mcp.exe"));
            CopyHost(source, "WeezTail.Launcher", Root);
            CopyHost(source, "WeezTail.Mcp.Launcher", Root);
            File.Copy(Path.Combine(source, "LogReader.Launcher.dll"), Path.Combine(Root, "LogReader.Launcher.dll"));
        }

        private static void CopyHost(string source, string name, string destination)
        {
            foreach (var suffix in new[] { ".exe", ".dll", ".deps.json", ".runtimeconfig.json" })
                File.Copy(Path.Combine(source, name + suffix), Path.Combine(destination, name + suffix));
        }

        public Process Start(bool mcp = true, string[]? arguments = null)
        {
            var info = new ProcessStartInfo(Path.Combine(Root, mcp ? "WeezTail.Mcp.Launcher.exe" : "WeezTail.Launcher.exe"))
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetTempPath(),
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var argument in arguments ?? []) info.ArgumentList.Add(argument);
            var process = Process.Start(info)!;
            TrackProcess(process.Id);
            return process;
        }

        private void TrackProcess(int pid)
        {
            var tracked = Process.GetProcessById(pid);
            _ = tracked.Handle; // Retain an independent handle even when the test disposes its wrapper.
            _processes.Add(tracked);
        }

        public async Task<JsonElement> ReadStartedAsync(string? releaseDirectory = null)
        {
            var path = Path.Combine(releaseDirectory ?? Release, "started.json");
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        using var document = JsonDocument.Parse(File.ReadAllText(path));
                        TrackProcess(document.RootElement.GetProperty("pid").GetInt32());
                        return document.RootElement.Clone();
                    }
                }
                catch (IOException) { }
                catch (JsonException) { }
                await Task.Delay(25);
            }
            throw new TimeoutException("Launcher child did not start.");
        }

        public void Dispose()
        {
            File.WriteAllText(Path.Combine(Release, "stop.txt"), "");
            foreach (var process in _processes)
            {
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    process.WaitForExit(10000);
                }
                catch (InvalidOperationException) { } // Process may have finished before cleanup.
                process.Dispose();
            }
            Directory.Delete(Root, recursive: true);
        }
    }
}

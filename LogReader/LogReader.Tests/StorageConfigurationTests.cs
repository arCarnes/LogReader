namespace LogReader.Tests;

using System.Text.Json;
using System.Text.Json.Serialization;
using LogReader.Core;

public sealed class StorageConfigurationTests : IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _testBaseDirectory = Path.Combine(
        Path.GetTempPath(),
        "LogReaderStorageConfigurationTests_" + Guid.NewGuid().ToString("N")[..8]);

    public StorageConfigurationTests()
    {
        Directory.CreateDirectory(_testBaseDirectory);
        AppPaths.SetRootPathForTests(null);
        AppPaths.SetBaseDirectoryForTests(_testBaseDirectory);
        AppPaths.SetAllowDebugFallbackForTests(null);
    }

    public void Dispose()
    {
        AppPaths.SetRootPathForTests(null);
        AppPaths.SetBaseDirectoryForTests(null);
        AppPaths.SetAllowDebugFallbackForTests(null);

        if (Directory.Exists(_testBaseDirectory))
        {
            Directory.Delete(_testBaseDirectory, true);
        }
    }

    [Fact]
    public void RootDirectory_PortableConfig_UsesBaseDirectory()
    {
        WriteConfig(new AppStorageConfiguration
        {
            InstallMode = AppInstallMode.Portable,
            StorageMode = StorageMode.ExeDirectory
        });

        Assert.Equal(Path.GetFullPath(_testBaseDirectory), AppPaths.RootDirectory);

        AppPaths.ValidateStorageConfiguration();

        Assert.True(Directory.Exists(Path.Combine(_testBaseDirectory, "Data")));
        Assert.True(Directory.Exists(Path.Combine(_testBaseDirectory, "Cache")));
    }

    [Fact]
    public void RootDirectory_MsiConfig_UsesAbsoluteStorageRoot()
    {
        var storageRoot = Path.Combine(_testBaseDirectory, "CustomStorageRoot");
        WriteConfig(new AppStorageConfiguration
        {
            InstallMode = AppInstallMode.Msi,
            StorageMode = StorageMode.Absolute,
            StorageRootPath = storageRoot
        });

        Assert.Equal(Path.GetFullPath(storageRoot), AppPaths.RootDirectory);
    }

    [Fact]
    public void RootDirectory_MissingConfig_WhenDebugFallbackDisabled_Throws()
    {
        AppPaths.SetAllowDebugFallbackForTests(false);

        var ex = Assert.Throws<InstallConfigurationException>(() => _ = AppPaths.RootDirectory);

        Assert.Equal(Path.Combine(_testBaseDirectory, AppPaths.InstallConfigFileName), ex.ConfigurationPath);
        Assert.Contains("missing", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RootDirectory_MissingConfig_WhenDebugFallbackEnabled_UsesLocalAppData()
    {
        AppPaths.SetAllowDebugFallbackForTests(true);

        var rootDirectory = AppPaths.RootDirectory;

        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LogReader"),
            rootDirectory);
    }

    [Fact]
    public void RootDirectory_InvalidEnumInConfig_Throws()
    {
        WriteRawConfig(
            """
            {
              "installMode": "Portable",
              "storageMode": "NotARealMode"
            }
            """);

        var ex = Assert.Throws<InstallConfigurationException>(() => _ = AppPaths.RootDirectory);

        Assert.Contains("not valid JSON", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RootDirectory_EmptyStorageRootPath_Throws()
    {
        WriteConfig(new AppStorageConfiguration
        {
            InstallMode = AppInstallMode.Msi,
            StorageMode = StorageMode.Absolute,
            StorageRootPath = ""
        });

        var ex = Assert.Throws<InstallConfigurationException>(() => _ = AppPaths.RootDirectory);

        Assert.Contains("storageRootPath", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateStorageRoot_ProtectedPath_IsRejected()
    {
        var protectedPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "LogReader");

        var ex = Assert.Throws<ProtectedStorageLocationException>(() => StoragePathValidator.ValidateStorageRoot(protectedPath));

        Assert.Equal(Path.GetFullPath(protectedPath), ex.StoragePath);
    }

    [Fact]
    public void ValidateStorageRoot_WritablePath_IsAccepted()
    {
        var storageRoot = Path.Combine(_testBaseDirectory, "WritableStorage");

        StoragePathValidator.ValidateStorageRoot(storageRoot);

        Assert.True(Directory.Exists(storageRoot));
    }

    [Fact]
    public void ValidateStorageRoot_WhenWriteProbeFails_Throws()
    {
        var storageRoot = Path.Combine(_testBaseDirectory, "ProbeFailureStorage");

        var ex = Assert.Throws<StorageValidationException>(() => StoragePathValidator.ValidateStorageRoot(
            storageRoot,
            writeProbe: static _ => throw new IOException("Simulated probe failure")));

        Assert.Equal(Path.GetFullPath(storageRoot), ex.StoragePath);
        Assert.IsType<IOException>(ex.InnerException);
    }

    private void WriteConfig(AppStorageConfiguration configuration)
        => WriteRawConfig(JsonSerializer.Serialize(configuration, SerializerOptions));

    private void WriteRawConfig(string json)
        => File.WriteAllText(Path.Combine(_testBaseDirectory, AppPaths.InstallConfigFileName), json);
}

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
    private readonly string _msiUserSelectionPath;
    private readonly IDisposable _appPathsScope;

    public StorageConfigurationTests()
    {
        _msiUserSelectionPath = Path.Combine(_testBaseDirectory, AppPaths.MsiUserStorageSelectionFileName);
        Directory.CreateDirectory(_testBaseDirectory);
        _appPathsScope = AppPaths.BeginTestScope(
            baseDirectory: _testBaseDirectory,
            msiUserStorageSelectionPath: _msiUserSelectionPath,
            allowDebugFallback: null);
    }

    public void Dispose()
    {
        _appPathsScope.Dispose();

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
    public void RootDirectory_DevConfig_UsesAbsoluteStorageRoot()
    {
        var storageRoot = Path.Combine(_testBaseDirectory, "DevStorageRoot");
        WriteConfig(new AppStorageConfiguration
        {
            InstallMode = AppInstallMode.Dev,
            StorageMode = StorageMode.Absolute,
            StorageRootPath = storageRoot
        });

        Assert.Equal(Path.GetFullPath(storageRoot), AppPaths.RootDirectory);
    }

    [Fact]
    public void RootDirectory_MsiPerUserChoiceWithoutSelection_ThrowsStorageSetupRequired()
    {
        WriteConfig(new AppStorageConfiguration
        {
            InstallMode = AppInstallMode.Msi,
            StorageMode = StorageMode.PerUserChoice
        });

        var ex = Assert.Throws<StorageSetupRequiredException>(() => _ = AppPaths.RootDirectory);

        Assert.Equal(_msiUserSelectionPath, ex.SelectionFilePath);
        Assert.Equal(AppPaths.GetDefaultStorageRoot(), ex.SuggestedStorageRootPath);
    }

    [Fact]
    public void RootDirectory_MsiPerUserChoiceWithSelection_UsesSelectedRoot()
    {
        var storageRoot = Path.Combine(_testBaseDirectory, "PerUserStorageRoot");
        WriteConfig(new AppStorageConfiguration
        {
            InstallMode = AppInstallMode.Msi,
            StorageMode = StorageMode.PerUserChoice
        });
        WriteUserSelection(storageRoot);

        Assert.Equal(Path.GetFullPath(storageRoot), AppPaths.RootDirectory);
    }

    [Fact]
    public void RootDirectory_MsiPerUserChoiceWithInvalidSelection_ThrowsStorageSetupRequired()
    {
        WriteConfig(new AppStorageConfiguration
        {
            InstallMode = AppInstallMode.Msi,
            StorageMode = StorageMode.PerUserChoice
        });
        WriteRawUserSelection(
            """
            {
              "storageRootPath": ""
            }
            """);

        var ex = Assert.Throws<StorageSetupRequiredException>(() => _ = AppPaths.RootDirectory);

        Assert.Equal(_msiUserSelectionPath, ex.SelectionFilePath);
        Assert.Contains("storageRootPath", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RootDirectory_MissingConfig_WhenDebugFallbackDisabled_Throws()
    {
        using var scope = AppPaths.BeginTestScope(
            baseDirectory: _testBaseDirectory,
            msiUserStorageSelectionPath: _msiUserSelectionPath,
            allowDebugFallback: false);

        var ex = Assert.Throws<InstallConfigurationException>(() => _ = AppPaths.RootDirectory);

        Assert.Equal(Path.Combine(_testBaseDirectory, AppPaths.InstallConfigFileName), ex.ConfigurationPath);
        Assert.Contains("missing", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RootDirectory_MissingConfig_WhenDebugFallbackEnabled_UsesLocalAppData()
    {
        using var scope = AppPaths.BeginTestScope(
            baseDirectory: _testBaseDirectory,
            msiUserStorageSelectionPath: _msiUserSelectionPath,
            allowDebugFallback: true,
            useLocalAppDataDefaultStorageRoot: true);

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
    public void IsProtectedPath_RecognizesCaseInsensitiveProtectedRootWithTrailingSeparator()
    {
        var protectedPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles).ToUpperInvariant(),
            "LogReader") + Path.DirectorySeparatorChar;

        Assert.True(StoragePathValidator.IsProtectedPath(protectedPath));
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

    private void WriteUserSelection(string storageRootPath)
        => WriteRawUserSelection(JsonSerializer.Serialize(new { storageRootPath }));

    private void WriteRawConfig(string json)
        => File.WriteAllText(Path.Combine(_testBaseDirectory, AppPaths.InstallConfigFileName), json);

    private void WriteRawUserSelection(string json)
        => File.WriteAllText(_msiUserSelectionPath, json);
}

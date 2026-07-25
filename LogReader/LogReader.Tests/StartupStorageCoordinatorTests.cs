namespace LogReader.Tests;

using System.Text.Json;
using System.Text.Json.Serialization;
using LogReader.App.Services;
using LogReader.App.ViewModels;
using LogReader.Core;

public sealed class StartupStorageCoordinatorTests : IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _testBaseDirectory = Path.Combine(
        Path.GetTempPath(),
        "WeezTailStartupStorageTests_" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _msiUserSelectionPath;
    private readonly string _legacyMsiUserSelectionPath;
    private readonly string _legacyDefaultStorageRoot;
    private readonly IDisposable _appPathsScope;

    public StartupStorageCoordinatorTests()
    {
        _msiUserSelectionPath = Path.Combine(_testBaseDirectory, AppPaths.MsiUserStorageSelectionFileName);
        _legacyMsiUserSelectionPath = Path.Combine(
            _testBaseDirectory,
            AppPaths.LegacySetupDirectoryName,
            AppPaths.LegacyMsiUserStorageSelectionFileName);
        _legacyDefaultStorageRoot = Path.Combine(
            _testBaseDirectory,
            AppPaths.LegacyDefaultStorageRootDirectoryName);
        Directory.CreateDirectory(_testBaseDirectory);
        _appPathsScope = AppPaths.BeginTestScope(
            baseDirectory: _testBaseDirectory,
            msiUserStorageSelectionPath: _msiUserSelectionPath,
            allowDebugFallback: false,
            legacyMsiUserStorageSelectionPath: _legacyMsiUserSelectionPath,
            legacyDefaultStorageRoot: _legacyDefaultStorageRoot);
    }

    public void Dispose()
    {
        _appPathsScope.Dispose();

        if (Directory.Exists(_testBaseDirectory))
            Directory.Delete(_testBaseDirectory, true);
    }

    [Fact]
    public void EnsureStorageReady_PortableConfig_ReturnsReadyWithoutShowingSetupDialog()
    {
        WriteInstallConfig(new AppStorageConfiguration
        {
            InstallMode = AppInstallMode.Portable,
            StorageMode = StorageMode.ExeDirectory
        });

        var showedDialog = false;
        var storageSetupDialogService = new StubStorageSetupDialogService
        {
            OnShowDialog = _ =>
            {
                showedDialog = true;
                return false;
            }
        };
        var coordinator = new StartupStorageCoordinator(storageSetupDialogService);

        var result = coordinator.EnsureStorageReady();

        Assert.Equal(StartupStorageResult.Ready, result);
        Assert.False(showedDialog);
    }

    [Fact]
    public void EnsureStorageReady_MsiPerUserChoiceWithoutSelection_ShowsSetupAndPersistsChoice()
    {
        var chosenStorageRoot = Path.Combine(_testBaseDirectory, "ChosenStorageRoot");
        WriteInstallConfig(new AppStorageConfiguration
        {
            InstallMode = AppInstallMode.Msi,
            StorageMode = StorageMode.PerUserChoice
        });

        var showedDialog = false;
        var storageSetupDialogService = new StubStorageSetupDialogService
        {
            OnShowDialog = viewModel =>
            {
                showedDialog = true;
                Assert.Equal(AppPaths.GetDefaultStorageRoot(), viewModel.StorageRootPath);

                viewModel.StorageRootPath = chosenStorageRoot;
                var completed = viewModel.TryComplete(out var errorMessage);

                Assert.True(completed, errorMessage);
                return completed;
            }
        };
        var coordinator = new StartupStorageCoordinator(storageSetupDialogService);

        var result = coordinator.EnsureStorageReady();

        Assert.Equal(StartupStorageResult.Ready, result);
        Assert.True(showedDialog);
        Assert.Equal(Path.GetFullPath(chosenStorageRoot), AppPaths.RootDirectory);
        Assert.True(Directory.Exists(Path.Combine(chosenStorageRoot, "Data")));
        Assert.True(Directory.Exists(Path.Combine(chosenStorageRoot, "Cache")));
        Assert.True(File.Exists(_msiUserSelectionPath));
    }

    [Fact]
    public void EnsureStorageReady_MsiPerUserChoiceWithLegacySelection_MigratesWithoutPrompting()
    {
        var legacyStorageRoot = Path.Combine(_testBaseDirectory, "LegacyStorageRoot");
        WriteInstallConfig(new AppStorageConfiguration
        {
            InstallMode = AppInstallMode.Msi,
            StorageMode = StorageMode.PerUserChoice
        });
        WriteLegacyUserStorageSelection(legacyStorageRoot);
        var showedDialog = false;
        var storageSetupDialogService = new StubStorageSetupDialogService
        {
            OnShowDialog = _ =>
            {
                showedDialog = true;
                return false;
            }
        };
        var coordinator = new StartupStorageCoordinator(storageSetupDialogService);

        var result = coordinator.EnsureStorageReady();

        Assert.Equal(StartupStorageResult.Ready, result);
        Assert.False(showedDialog);
        Assert.Equal(Path.GetFullPath(legacyStorageRoot), AppPaths.RootDirectory);
        Assert.True(File.Exists(_msiUserSelectionPath));
        Assert.True(File.Exists(_legacyMsiUserSelectionPath));
        Assert.True(Directory.Exists(Path.Combine(legacyStorageRoot, AppPaths.DataFolderName)));
        Assert.True(Directory.Exists(Path.Combine(legacyStorageRoot, AppPaths.CacheFolderName)));
    }

    [Fact]
    public void EnsureStorageReady_MsiPerUserChoiceWhenCanceled_ReturnsCanceled()
    {
        WriteInstallConfig(new AppStorageConfiguration
        {
            InstallMode = AppInstallMode.Msi,
            StorageMode = StorageMode.PerUserChoice
        });

        var storageSetupDialogService = new StubStorageSetupDialogService
        {
            OnShowDialog = _ => false
        };
        var coordinator = new StartupStorageCoordinator(storageSetupDialogService);

        var result = coordinator.EnsureStorageReady();

        Assert.Equal(StartupStorageResult.Canceled, result);
        Assert.False(File.Exists(_msiUserSelectionPath));
    }

    [Fact]
    public void EnsureStorageReady_MsiPerUserChoiceWithUnusableSavedSelection_RePromptsAndPersistsChoice()
    {
        var unusableStorageRoot = Path.Combine(_testBaseDirectory, "NotAFolder");
        var chosenStorageRoot = Path.Combine(_testBaseDirectory, "RecoveredStorageRoot");
        WriteInstallConfig(new AppStorageConfiguration
        {
            InstallMode = AppInstallMode.Msi,
            StorageMode = StorageMode.PerUserChoice
        });
        File.WriteAllText(unusableStorageRoot, "This file blocks directory creation.");
        WriteUserStorageSelection(unusableStorageRoot);

        var storageSetupDialogService = new StubStorageSetupDialogService
        {
            OnShowDialog = viewModel =>
            {
                Assert.Equal(Path.GetFullPath(unusableStorageRoot), viewModel.StorageRootPath);

                viewModel.StorageRootPath = chosenStorageRoot;
                var completed = viewModel.TryComplete(out var errorMessage);

                Assert.True(completed, errorMessage);
                return completed;
            }
        };
        var coordinator = new StartupStorageCoordinator(storageSetupDialogService);

        var result = coordinator.EnsureStorageReady();

        Assert.Equal(StartupStorageResult.Ready, result);
        Assert.Equal(Path.GetFullPath(chosenStorageRoot), AppPaths.RootDirectory);
        Assert.True(Directory.Exists(Path.Combine(chosenStorageRoot, "Data")));
        Assert.True(Directory.Exists(Path.Combine(chosenStorageRoot, "Cache")));
    }

    [Fact]
    public void EnsureStorageReady_MsiPerUserChoiceWithUnsafeSavedSelection_RePromptsAndPersistsChoice()
    {
        var unsafeStorageRoot = Path.GetTempPath();
        var chosenStorageRoot = Path.Combine(_testBaseDirectory, "RecoveredStorageRoot");
        WriteInstallConfig(new AppStorageConfiguration
        {
            InstallMode = AppInstallMode.Msi,
            StorageMode = StorageMode.PerUserChoice
        });
        WriteUserStorageSelection(unsafeStorageRoot);

        var storageSetupDialogService = new StubStorageSetupDialogService
        {
            OnShowDialog = viewModel =>
            {
                Assert.Equal(Path.GetFullPath(unsafeStorageRoot), viewModel.StorageRootPath);

                viewModel.StorageRootPath = chosenStorageRoot;
                var completed = viewModel.TryComplete(out var errorMessage);

                Assert.True(completed, errorMessage);
                return completed;
            }
        };
        var coordinator = new StartupStorageCoordinator(storageSetupDialogService);

        var result = coordinator.EnsureStorageReady();

        Assert.Equal(StartupStorageResult.Ready, result);
        Assert.Equal(Path.GetFullPath(chosenStorageRoot), AppPaths.RootDirectory);
        Assert.True(Directory.Exists(Path.Combine(chosenStorageRoot, "Data")));
        Assert.True(Directory.Exists(Path.Combine(chosenStorageRoot, "Cache")));
    }

    [Fact]
    public void StorageSetupViewModel_TryComplete_WithProtectedPath_ReturnsFalseWithoutSaving()
    {
        var savedStorageRoot = string.Empty;
        var protectedPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "WeezTail");
        var viewModel = new StorageSetupViewModel(protectedPath)
        {
            SaveStorageSelection = path => savedStorageRoot = path
        };

        var completed = viewModel.TryComplete(out var errorMessage);

        Assert.False(completed);
        Assert.Equal(string.Empty, savedStorageRoot);
        Assert.Contains("protected", errorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StorageSetupViewModel_TryComplete_WithUnsafeBroadRoot_ReturnsFalseWithoutSaving()
    {
        var savedStorageRoot = string.Empty;
        var viewModel = new StorageSetupViewModel(Path.GetTempPath())
        {
            SaveStorageSelection = path => savedStorageRoot = path
        };

        var completed = viewModel.TryComplete(out var errorMessage);

        Assert.False(completed);
        Assert.Equal(string.Empty, savedStorageRoot);
        Assert.Contains("WeezTail-specific", errorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StorageSetupViewModel_TryComplete_AfterUnsafeSelection_RemainsRetryable()
    {
        var savedStorageRoot = string.Empty;
        var safeStorageRoot = Path.Combine(_testBaseDirectory, "RetriedStorageRoot");
        var viewModel = new StorageSetupViewModel(Path.GetTempPath())
        {
            SaveStorageSelection = path => savedStorageRoot = path
        };

        Assert.False(viewModel.TryComplete(out _));

        viewModel.StorageRootPath = safeStorageRoot;
        var completed = viewModel.TryComplete(out var errorMessage);

        Assert.True(completed, errorMessage);
        Assert.Equal(Path.GetFullPath(safeStorageRoot), savedStorageRoot);
        Assert.Equal(Path.GetFullPath(safeStorageRoot), viewModel.StorageRootPath);
    }

    [Fact]
    public void StorageSetupViewModel_BrowseStorageRoot_UsesFolderDialogSelection()
    {
        var folderDialogService = new StubFolderDialogService
        {
            OnShowFolderDialog = request =>
            {
                Assert.Contains("Data and Cache", request.Description, StringComparison.Ordinal);
                return new FolderDialogResult(true, Path.Combine(_testBaseDirectory, "ChosenRoot"));
            }
        };
        var viewModel = new StorageSetupViewModel(Path.Combine(_testBaseDirectory, "DefaultRoot"), folderDialogService);

        viewModel.BrowseStorageRootCommand.Execute(null);

        Assert.Equal(Path.Combine(_testBaseDirectory, "ChosenRoot"), viewModel.StorageRootPath);
    }

    private void WriteInstallConfig(AppStorageConfiguration configuration)
        => File.WriteAllText(
            Path.Combine(_testBaseDirectory, AppPaths.InstallConfigFileName),
            JsonSerializer.Serialize(configuration, SerializerOptions));

    private void WriteUserStorageSelection(string storageRootPath)
        => File.WriteAllText(
            _msiUserSelectionPath,
            JsonSerializer.Serialize(
                new { StorageRootPath = storageRootPath },
                SerializerOptions));

    private void WriteLegacyUserStorageSelection(string storageRootPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_legacyMsiUserSelectionPath)!);
        File.WriteAllText(
            _legacyMsiUserSelectionPath,
            JsonSerializer.Serialize(
                new { StorageRootPath = storageRootPath },
                SerializerOptions));
    }
}

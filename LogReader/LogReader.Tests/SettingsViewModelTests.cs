using LogReader.App.ViewModels;
using LogReader.App.Services;
using LogReader.Core;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;
using LogReader.Infrastructure.Repositories;
using System.Windows;

namespace LogReader.Tests;

public class SettingsViewModelTests
{
    private sealed class StubSettingsRepository : ISettingsRepository
    {
        public AppSettings Settings { get; set; } = new();
        public Func<string, Task<AppSettings>> OnLoadFromFileAsync { get; set; }
            = _ => Task.FromResult(new AppSettings());
        public Func<string, AppSettings, Task> OnSaveToFileAsync { get; set; }
            = (_, _) => Task.CompletedTask;
        public string? LastLoadFromFilePath { get; private set; }
        public string? LastSaveToFilePath { get; private set; }
        public AppSettings? LastSavedToFileSettings { get; private set; }

        public Task<AppSettings> LoadAsync() => Task.FromResult(Settings);

        public Task SaveAsync(AppSettings settings)
        {
            Settings = settings;
            return Task.CompletedTask;
        }

        public Task<AppSettings> LoadFromFileAsync(string filePath)
        {
            LastLoadFromFilePath = filePath;
            return OnLoadFromFileAsync(filePath);
        }

        public Task SaveToFileAsync(string filePath, AppSettings settings)
        {
            LastSaveToFilePath = filePath;
            LastSavedToFileSettings = settings;
            return OnSaveToFileAsync(filePath, settings);
        }
    }

    private sealed class StubSettingsImportService : ISettingsImportService
    {
        public Func<string, Task<AppSettings>> OnImportSettingsAsync { get; set; }
            = _ => Task.FromResult(new AppSettings());
        public string? LastImportPath { get; private set; }

        public Task<AppSettings> ImportSettingsAsync(string importPath)
        {
            LastImportPath = importPath;
            return OnImportSettingsAsync(importPath);
        }
    }

    [Fact]
    public async Task BrowseDefaultDirectoryCommand_UsesFolderDialogSelection()
    {
        var repo = new StubSettingsRepository();
        var folderDialogService = new StubFolderDialogService
        {
            OnShowFolderDialog = request =>
            {
                Assert.Equal("Select default directory for opening log files", request.Description);
                return new FolderDialogResult(true, @"C:\logs");
            }
        };
        var vm = new SettingsViewModel(repo, folderDialogService);
        await vm.LoadAsync();

        vm.BrowseDefaultDirectoryCommand.Execute(null);

        Assert.Equal(@"C:\logs", vm.DefaultOpenDirectory);
    }

    [Fact]
    public async Task SaveAsync_PersistsHighlightRules()
    {
        var repo = new StubSettingsRepository { Settings = new AppSettings() };
        var vm = new SettingsViewModel(repo);
        await vm.LoadAsync();

        vm.HighlightRules.Add(new HighlightRuleViewModel
        {
            Pattern = "ERROR",
            IsRegex = false,
            CaseSensitive = true,
            Color = "#FFCCCC",
            IsEnabled = true
        });

        await vm.SaveAsync();

        var saved = Assert.Single(repo.Settings.HighlightRules);
        Assert.Equal("ERROR", saved.Pattern);
        Assert.True(saved.CaseSensitive);
        Assert.Equal("#FFCCCC", saved.Color);
        Assert.True(saved.IsEnabled);
    }

    [Fact]
    public async Task LoadAndSave_RoundTripsSearchMatchHighlightSettings()
    {
        var repo = new StubSettingsRepository
        {
            Settings = new AppSettings
            {
                EnableSearchMatchHighlighting = false,
                SearchMatchHighlightColor = "#ffe082"
            }
        };
        var vm = new SettingsViewModel(repo);

        await vm.LoadAsync();

        Assert.False(vm.EnableSearchMatchHighlighting);
        Assert.Equal("#FFE082", vm.SearchMatchHighlightColor);

        vm.EnableSearchMatchHighlighting = true;
        vm.SearchMatchHighlightColor = "#fff59d";
        await vm.SaveAsync();

        Assert.True(repo.Settings.EnableSearchMatchHighlighting);
        Assert.Equal("#FFF59D", repo.Settings.SearchMatchHighlightColor);
    }

    [Fact]
    public async Task SearchMatchHighlightColor_InvalidValue_NormalizesToDefault()
    {
        var repo = new StubSettingsRepository
        {
            Settings = new AppSettings
            {
                SearchMatchHighlightColor = "not-a-color"
            }
        };
        var vm = new SettingsViewModel(repo);

        await vm.LoadAsync();

        Assert.Equal("#FFF59D", vm.SearchMatchHighlightColor);

        vm.SearchMatchHighlightColor = "";
        await vm.SaveAsync();

        Assert.Equal("#FFF59D", repo.Settings.SearchMatchHighlightColor);
    }

    [Fact]
    public async Task AddRuleCommand_DefaultsColorToWhite()
    {
        var repo = new StubSettingsRepository { Settings = new AppSettings() };
        var vm = new SettingsViewModel(repo);
        await vm.LoadAsync();

        vm.AddRuleCommand.Execute(null);

        var rule = Assert.Single(vm.HighlightRules);
        Assert.Equal("#FFFFFF", rule.Color);
    }

    [Fact]
    public async Task LoadAndSave_RoundTripsColorPickerCustomColors()
    {
        var repo = new StubSettingsRepository
        {
            Settings = new AppSettings
            {
                ColorPickerCustomColors = new List<string> { "#ff4d4d", "#00AA66" }
            }
        };
        var vm = new SettingsViewModel(repo);

        await vm.LoadAsync();

        Assert.Equal(["#FF4D4D", "#00AA66"], vm.ColorPickerCustomColors);
        Assert.Equal(["#00AA66", "#FF4D4D"], vm.RecentHighlightColors);

        vm.ColorPickerCustomColors = new List<string> { "#112233", "not-a-color", "#445566" };
        await vm.SaveAsync();

        Assert.Equal(["#112233", "#445566"], repo.Settings.ColorPickerCustomColors);
    }

    [Fact]
    public async Task RememberHighlightColor_UpdatesRecentColorsNewestFirst()
    {
        var repo = new StubSettingsRepository
        {
            Settings = new AppSettings
            {
                ColorPickerCustomColors = new List<string> { "#ff4d4d", "#00AA66" }
            }
        };
        var vm = new SettingsViewModel(repo);

        await vm.LoadAsync();
        vm.RememberHighlightColor("#112233");

        Assert.Equal(["#FF4D4D", "#00AA66", "#112233"], vm.ColorPickerCustomColors);
        Assert.Equal(["#112233", "#00AA66", "#FF4D4D"], vm.RecentHighlightColors);
    }

    [Fact]
    public async Task RememberHighlightColor_MovesExistingColorToNewest()
    {
        var repo = new StubSettingsRepository
        {
            Settings = new AppSettings
            {
                ColorPickerCustomColors = new List<string> { "#ff4d4d", "#00AA66", "#112233" }
            }
        };
        var vm = new SettingsViewModel(repo);

        await vm.LoadAsync();
        vm.RememberHighlightColor("#ff4d4d");

        Assert.Equal(["#00AA66", "#112233", "#FF4D4D"], vm.ColorPickerCustomColors);
        Assert.Equal(["#FF4D4D", "#112233", "#00AA66"], vm.RecentHighlightColors);
    }

    [Fact]
    public async Task ClearRecentHighlightColorsCommand_ClearsCustomColors()
    {
        var repo = new StubSettingsRepository
        {
            Settings = new AppSettings
            {
                ColorPickerCustomColors = new List<string> { "#ff4d4d", "#00AA66" }
            }
        };
        var vm = new SettingsViewModel(repo);

        await vm.LoadAsync();
        vm.ClearRecentHighlightColorsCommand.Execute(null);
        await vm.SaveAsync();

        Assert.Empty(vm.ColorPickerCustomColors);
        Assert.Empty(vm.RecentHighlightColors);
        Assert.Empty(repo.Settings.ColorPickerCustomColors);
    }

    [Fact]
    public async Task LoadAsync_LoadsDateRollingPatternsInStoredOrder()
    {
        var repo = new StubSettingsRepository
        {
            Settings = new AppSettings
            {
                DateRollingPatterns = new List<ReplacementPattern>
                {
                    new() { Name = "First", FindPattern = ".log", ReplacePattern = ".log.{yyyy-MM-dd}" },
                    new() { Name = "Second", FindPattern = ".log", ReplacePattern = ".log{yyyyMMdd}" }
                }
            }
        };
        var vm = new SettingsViewModel(repo);

        await vm.LoadAsync();

        Assert.Equal(["First", "Second"], vm.DateRollingPatterns.Select(pattern => pattern.Name).ToArray());
    }

    [Fact]
    public async Task SaveAsync_PersistsDateRollingPatternsInCurrentOrder()
    {
        var repo = new StubSettingsRepository { Settings = new AppSettings() };
        var vm = new SettingsViewModel(repo);
        await vm.LoadAsync();

        vm.DateRollingPatterns.Add(new ReplacementPatternViewModel
        {
            Name = "First",
            FindPattern = ".log",
            ReplacePattern = ".log.{yyyy-MM-dd}"
        });
        vm.DateRollingPatterns.Add(new ReplacementPatternViewModel
        {
            Name = "Second",
            FindPattern = ".log",
            ReplacePattern = ".log{yyyyMMdd}"
        });
        vm.MoveDateRollingPatternUpCommand.Execute(vm.DateRollingPatterns[1]);

        await vm.SaveAsync();

        Assert.Equal(["Second", "First"], repo.Settings.DateRollingPatterns.Select(pattern => pattern.Name).ToArray());
    }

    [Fact]
    public async Task HasValidationErrors_ReflectsInvalidDateRollingPatterns()
    {
        var repo = new StubSettingsRepository { Settings = new AppSettings() };
        var vm = new SettingsViewModel(repo);
        await vm.LoadAsync();

        vm.DateRollingPatterns.Add(new ReplacementPatternViewModel
        {
            Name = "",
            FindPattern = ".log",
            ReplacePattern = ".txt"
        });

        Assert.True(vm.HasValidationErrors);
    }

    [Fact]
    public async Task SaveAsync_InvalidDateRollingPattern_Throws()
    {
        var repo = new StubSettingsRepository { Settings = new AppSettings() };
        var vm = new SettingsViewModel(repo);
        await vm.LoadAsync();
        vm.DateRollingPatterns.Add(new ReplacementPatternViewModel
        {
            Name = "",
            FindPattern = ".log",
            ReplacePattern = ".txt"
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => vm.SaveAsync());
    }

    [Fact]
    public async Task MoveDateRollingPatternDownCommand_ReordersPatterns()
    {
        var repo = new StubSettingsRepository { Settings = new AppSettings() };
        var vm = new SettingsViewModel(repo);
        await vm.LoadAsync();
        var first = new ReplacementPatternViewModel { Name = "First", FindPattern = ".log", ReplacePattern = "{yyyyMMdd}" };
        var second = new ReplacementPatternViewModel { Name = "Second", FindPattern = ".log", ReplacePattern = "{yyyyMMdd}" };
        vm.DateRollingPatterns.Add(first);
        vm.DateRollingPatterns.Add(second);

        vm.MoveDateRollingPatternDownCommand.Execute(first);

        Assert.Equal(["Second", "First"], vm.DateRollingPatterns.Select(pattern => pattern.Name).ToArray());
    }

    [Fact]
    public async Task AddAndRemoveDateRollingPatternCommands_UpdateCollection()
    {
        var repo = new StubSettingsRepository { Settings = new AppSettings() };
        var vm = new SettingsViewModel(repo);
        await vm.LoadAsync();

        vm.AddDateRollingPatternCommand.Execute(null);
        var addedPattern = Assert.Single(vm.DateRollingPatterns);

        vm.RemoveDateRollingPatternCommand.Execute(addedPattern);

        Assert.Empty(vm.DateRollingPatterns);
    }

    [Fact]
    public async Task LoadAndSave_NormalizesLogFontFamily()
    {
        var repo = new StubSettingsRepository
        {
            Settings = new AppSettings
            {
                LogFontFamily = "NotARealFont"
            }
        };
        var vm = new SettingsViewModel(repo);
        await vm.LoadAsync();

        Assert.Equal("Consolas", vm.LogFontFamily);

        vm.LogFontFamily = "Cascadia Mono";
        await vm.SaveAsync();

        Assert.Equal("Cascadia Mono", repo.Settings.LogFontFamily);
    }

    [Fact]
    public async Task LoadAsync_DefaultsMissingLogFontSizeToTwelve()
    {
        var repo = new StubSettingsRepository
        {
            Settings = new AppSettings()
        };
        var vm = new SettingsViewModel(repo);

        await vm.LoadAsync();

        Assert.Equal(12, vm.LogFontSize);
    }

    [Fact]
    public async Task LoadAsync_DefaultsInvalidLogFontSizeToTwelve()
    {
        var repo = new StubSettingsRepository
        {
            Settings = new AppSettings
            {
                LogFontSize = 0
            }
        };
        var vm = new SettingsViewModel(repo);

        await vm.LoadAsync();

        Assert.Equal(12, vm.LogFontSize);
    }

    [Fact]
    public async Task LoadAsync_ClampsOutOfRangeLogFontSize()
    {
        var lowRepo = new StubSettingsRepository
        {
            Settings = new AppSettings
            {
                LogFontSize = 7
            }
        };
        var lowVm = new SettingsViewModel(lowRepo);

        await lowVm.LoadAsync();

        Assert.Equal(8, lowVm.LogFontSize);

        var highRepo = new StubSettingsRepository
        {
            Settings = new AppSettings
            {
                LogFontSize = 19
            }
        };
        var highVm = new SettingsViewModel(highRepo);

        await highVm.LoadAsync();

        Assert.Equal(18, highVm.LogFontSize);
    }

    [Fact]
    public async Task SaveAsync_PersistsLogFontSize()
    {
        var repo = new StubSettingsRepository { Settings = new AppSettings() };
        var vm = new SettingsViewModel(repo);

        await vm.LoadAsync();

        vm.LogFontSize = 18;
        await vm.SaveAsync();

        Assert.Equal(18, repo.Settings.LogFontSize);
    }

    [Fact]
    public async Task LoadAndSave_RoundTripsDashboardPathPreference()
    {
        var repo = new StubSettingsRepository
        {
            Settings = new AppSettings
            {
                ShowFullPathsInDashboard = true
            }
        };
        var vm = new SettingsViewModel(repo);
        await vm.LoadAsync();

        Assert.True(vm.ShowFullPathsInDashboard);

        vm.ShowFullPathsInDashboard = false;
        await vm.SaveAsync();

        Assert.False(repo.Settings.ShowFullPathsInDashboard);
    }

    [Fact]
    public async Task ImportSettingsCommand_LoadsAllSettingsIntoDialogWithoutPersisting()
    {
        var repo = new StubSettingsRepository
        {
            Settings = new AppSettings { DefaultOpenDirectory = @"C:\old" }
        };
        var settingsImportService = new StubSettingsImportService
        {
            OnImportSettingsAsync = path =>
            {
                Assert.Equal(@"C:\exports\settings.json", path);
                return Task.FromResult(new AppSettings
                {
                    DefaultOpenDirectory = @"C:\logs",
                    LogFontFamily = "Cascadia Code",
                    LogFontSize = 16,
                    ShowFullPathsInDashboard = true,
                    EnableSearchMatchHighlighting = false,
                    SearchMatchHighlightColor = "#ffe082",
                    ColorPickerCustomColors = new List<string> { "#112233", "#445566" },
                    HighlightRules = new List<LineHighlightRule>
                    {
                        new()
                        {
                            Pattern = "ERROR",
                            IsRegex = true,
                            CaseSensitive = true,
                            Color = "#FFCCCC",
                            IsEnabled = false
                        }
                    },
                    DateRollingPatterns = new List<ReplacementPattern>
                    {
                        new() { Name = "Log4Net", FindPattern = ".log", ReplacePattern = ".log{yyyyMMdd}" }
                    }
                });
            }
        };
        var fileDialogService = new StubFileDialogService
        {
            OnShowOpenFileDialog = request =>
            {
                Assert.Equal("Import Settings", request.Title);
                Assert.Equal("LogReader Settings (*.json)|*.json", request.Filter);
                return new OpenFileDialogResult(true, new[] { @"C:\exports\settings.json" });
            }
        };
        var vm = new SettingsViewModel(repo, fileDialogService: fileDialogService, settingsImportService: settingsImportService);
        await vm.LoadAsync();

        await vm.ImportSettingsCommand.ExecuteAsync(null);

        Assert.Equal(@"C:\logs", vm.DefaultOpenDirectory);
        Assert.Equal("Cascadia Code", vm.LogFontFamily);
        Assert.Equal(16, vm.LogFontSize);
        Assert.True(vm.ShowFullPathsInDashboard);
        Assert.False(vm.EnableSearchMatchHighlighting);
        Assert.Equal("#FFE082", vm.SearchMatchHighlightColor);
        Assert.Equal(["#112233", "#445566"], vm.ColorPickerCustomColors);
        var importedRule = Assert.Single(vm.HighlightRules);
        Assert.Equal("ERROR", importedRule.Pattern);
        Assert.True(importedRule.IsRegex);
        Assert.True(importedRule.CaseSensitive);
        Assert.Equal("#FFCCCC", importedRule.Color);
        Assert.False(importedRule.IsEnabled);
        Assert.Equal("Log4Net", Assert.Single(vm.DateRollingPatterns).Name);
        Assert.Equal(@"C:\old", repo.Settings.DefaultOpenDirectory);
        Assert.Equal(@"C:\exports\settings.json", settingsImportService.LastImportPath);
    }

    [Fact]
    public async Task ImportSettingsCommand_WhenCanceled_DoesNotLoadSettings()
    {
        var repo = new StubSettingsRepository
        {
            Settings = new AppSettings { DefaultOpenDirectory = @"C:\current" }
        };
        var settingsImportService = new StubSettingsImportService
        {
            OnImportSettingsAsync = _ => throw new InvalidOperationException("Import should not be attempted.")
        };
        var fileDialogService = new StubFileDialogService
        {
            OnShowOpenFileDialog = _ => new OpenFileDialogResult(false, Array.Empty<string>())
        };
        var vm = new SettingsViewModel(repo, fileDialogService: fileDialogService, settingsImportService: settingsImportService);
        await vm.LoadAsync();

        await vm.ImportSettingsCommand.ExecuteAsync(null);

        Assert.Equal(@"C:\current", vm.DefaultOpenDirectory);
        Assert.Null(settingsImportService.LastImportPath);
    }

    [Fact]
    public async Task ImportSettingsCommand_WhenLoadFails_PreservesCurrentDialogValuesAndShowsError()
    {
        var repo = new StubSettingsRepository
        {
            Settings = new AppSettings { DefaultOpenDirectory = @"C:\current" }
        };
        var settingsImportService = new StubSettingsImportService
        {
            OnImportSettingsAsync = _ => Task.FromException<AppSettings>(new InvalidDataException("Bad settings file."))
        };
        var fileDialogService = new StubFileDialogService
        {
            OnShowOpenFileDialog = _ => new OpenFileDialogResult(true, new[] { @"C:\exports\bad.json" })
        };
        var messageBoxService = new StubMessageBoxService();
        var vm = new SettingsViewModel(
            repo,
            fileDialogService: fileDialogService,
            messageBoxService: messageBoxService,
            settingsImportService: settingsImportService);
        await vm.LoadAsync();
        vm.LogFontFamily = "Cascadia Mono";

        await vm.ImportSettingsCommand.ExecuteAsync(null);

        Assert.Equal(@"C:\current", vm.DefaultOpenDirectory);
        Assert.Equal("Cascadia Mono", vm.LogFontFamily);
        Assert.Equal("Import Settings Failed", messageBoxService.LastCaption);
        Assert.Equal(MessageBoxImage.Error, messageBoxService.LastImage);
    }

    [Fact]
    public async Task ImportSettingsCommand_CopiesImportToSettingsStorageWithoutPersistingActiveSettings()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "LogReaderSettingsImportTests_" + Guid.NewGuid().ToString("N")[..8]);
        using var appPathsScope = AppPaths.BeginTestScope(rootPath: testRoot);
        try
        {
            var sourceDirectory = Path.Combine(testRoot, "Source");
            Directory.CreateDirectory(sourceDirectory);
            var sourcePath = Path.Combine(sourceDirectory, "settings.json");
            var repo = new JsonSettingsRepository();
            await repo.SaveToFileAsync(sourcePath, new AppSettings
            {
                DefaultOpenDirectory = @"C:\imported",
                LogFontFamily = "Cascadia Mono",
                LogFontSize = 16,
                ShowFullPathsInDashboard = true
            });
            var fileDialogService = new StubFileDialogService
            {
                OnShowOpenFileDialog = _ => new OpenFileDialogResult(true, new[] { sourcePath })
            };
            var vm = new SettingsViewModel(repo, fileDialogService: fileDialogService);
            await vm.LoadAsync();

            await vm.ImportSettingsCommand.ExecuteAsync(null);

            var storedPath = Path.Combine(AppPaths.SettingsDirectory, Path.GetFileName(sourcePath));
            Assert.True(File.Exists(storedPath));
            Assert.False(File.Exists(storedPath + ".importing"));
            Assert.Equal(@"C:\imported", vm.DefaultOpenDirectory);
            Assert.Equal("Cascadia Mono", vm.LogFontFamily);
            Assert.Equal(16, vm.LogFontSize);
            Assert.True(vm.ShowFullPathsInDashboard);

            var activeSettings = await repo.LoadAsync();
            Assert.Null(activeSettings.DefaultOpenDirectory);
            Assert.Equal("Consolas", activeSettings.LogFontFamily);
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, true);
        }
    }

    [Fact]
    public async Task ImportSettingsCommand_WhenCopiedImportIsMalformed_PreservesDialogAndRemovesPendingCopy()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "LogReaderSettingsImportTests_" + Guid.NewGuid().ToString("N")[..8]);
        using var appPathsScope = AppPaths.BeginTestScope(rootPath: testRoot);
        try
        {
            var sourceDirectory = Path.Combine(testRoot, "Source");
            Directory.CreateDirectory(sourceDirectory);
            var sourcePath = Path.Combine(sourceDirectory, "bad-settings.json");
            await File.WriteAllTextAsync(sourcePath, "{ invalid json");
            var storedPath = Path.Combine(AppPaths.EnsureDirectory(AppPaths.SettingsDirectory), Path.GetFileName(sourcePath));
            await File.WriteAllTextAsync(storedPath, "existing retained copy");
            var repo = new JsonSettingsRepository();
            var fileDialogService = new StubFileDialogService
            {
                OnShowOpenFileDialog = _ => new OpenFileDialogResult(true, new[] { sourcePath })
            };
            var messageBoxService = new StubMessageBoxService();
            var vm = new SettingsViewModel(
                repo,
                fileDialogService: fileDialogService,
                messageBoxService: messageBoxService);
            await vm.LoadAsync();
            vm.DefaultOpenDirectory = @"C:\current";
            vm.LogFontFamily = "Cascadia Code";

            await vm.ImportSettingsCommand.ExecuteAsync(null);

            Assert.Equal(@"C:\current", vm.DefaultOpenDirectory);
            Assert.Equal("Cascadia Code", vm.LogFontFamily);
            Assert.Equal("Import Settings Failed", messageBoxService.LastCaption);
            Assert.Equal("existing retained copy", await File.ReadAllTextAsync(storedPath));
            Assert.False(File.Exists(storedPath + ".importing"));
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, true);
        }
    }

    [Fact]
    public async Task ExportSettingsCommand_ExportsCurrentUnsavedDialogValues()
    {
        var repo = new StubSettingsRepository { Settings = new AppSettings() };
        var fileDialogService = new StubFileDialogService
        {
            OnShowSaveFileDialog = request =>
            {
                Assert.Equal("Export Settings", request.Title);
                Assert.Equal("LogReader Settings (*.json)|*.json", request.Filter);
                Assert.Equal(".json", request.DefaultExt);
                Assert.True(request.AddExtension);
                Assert.StartsWith("logreader-settings-", request.FileName, StringComparison.Ordinal);
                return new SaveFileDialogResult(true, @"C:\exports\settings.json");
            }
        };
        var vm = new SettingsViewModel(repo, fileDialogService: fileDialogService);
        await vm.LoadAsync();
        vm.DefaultOpenDirectory = @"C:\logs";
        vm.LogFontFamily = "Cascadia Mono";
        vm.LogFontSize = 18;
        vm.ShowFullPathsInDashboard = true;
        vm.EnableSearchMatchHighlighting = false;
        vm.SearchMatchHighlightColor = "#ffe082";
        vm.ColorPickerCustomColors = new List<string> { "#112233" };
        vm.HighlightRules.Add(new HighlightRuleViewModel
        {
            Pattern = "WARN",
            IsRegex = false,
            CaseSensitive = true,
            Color = "#ABCDEF",
            IsEnabled = true
        });
        vm.DateRollingPatterns.Add(new ReplacementPatternViewModel
        {
            Name = "Daily",
            FindPattern = ".log",
            ReplacePattern = ".log{yyyyMMdd}"
        });

        await vm.ExportSettingsCommand.ExecuteAsync(null);

        Assert.Equal(@"C:\exports\settings.json", repo.LastSaveToFilePath);
        Assert.NotNull(repo.LastSavedToFileSettings);
        Assert.Equal(@"C:\logs", repo.LastSavedToFileSettings!.DefaultOpenDirectory);
        Assert.Equal("Cascadia Mono", repo.LastSavedToFileSettings.LogFontFamily);
        Assert.Equal(18, repo.LastSavedToFileSettings.LogFontSize);
        Assert.True(repo.LastSavedToFileSettings.ShowFullPathsInDashboard);
        Assert.False(repo.LastSavedToFileSettings.EnableSearchMatchHighlighting);
        Assert.Equal("#FFE082", repo.LastSavedToFileSettings.SearchMatchHighlightColor);
        Assert.Equal(["#112233"], repo.LastSavedToFileSettings.ColorPickerCustomColors);
        Assert.Equal("WARN", Assert.Single(repo.LastSavedToFileSettings.HighlightRules).Pattern);
        Assert.Equal("Daily", Assert.Single(repo.LastSavedToFileSettings.DateRollingPatterns).Name);
        Assert.Null(repo.Settings.DefaultOpenDirectory);
    }

    [Fact]
    public async Task ExportSettingsCommand_WhenDateRollingPatternInvalid_DoesNotExportAndShowsWarning()
    {
        var repo = new StubSettingsRepository { Settings = new AppSettings() };
        var fileDialogService = new StubFileDialogService
        {
            OnShowSaveFileDialog = _ => throw new InvalidOperationException("Save dialog should not open.")
        };
        var messageBoxService = new StubMessageBoxService();
        var vm = new SettingsViewModel(repo, fileDialogService: fileDialogService, messageBoxService: messageBoxService);
        await vm.LoadAsync();
        vm.DateRollingPatterns.Add(new ReplacementPatternViewModel
        {
            Name = "",
            FindPattern = ".log",
            ReplacePattern = ".txt"
        });

        await vm.ExportSettingsCommand.ExecuteAsync(null);

        Assert.Null(repo.LastSaveToFilePath);
        Assert.Equal("Export Settings Failed", messageBoxService.LastCaption);
        Assert.Equal(MessageBoxImage.Warning, messageBoxService.LastImage);
    }

}

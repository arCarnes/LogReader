using LogReader.App.ViewModels;
using LogReader.App.Services;
using LogReader.Core.Interfaces;
using LogReader.Core.Models;

namespace LogReader.Tests;

public class SettingsViewModelTests
{
    private sealed class StubSettingsRepository : ISettingsRepository
    {
        public AppSettings Settings { get; set; } = new();

        public Task<AppSettings> LoadAsync() => Task.FromResult(Settings);

        public Task SaveAsync(AppSettings settings)
        {
            Settings = settings;
            return Task.CompletedTask;
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
}

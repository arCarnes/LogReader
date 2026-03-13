using LogReader.App.ViewModels;
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
    public async Task LoadAsync_LoadsDefaultFileEncoding()
    {
        var repo = new StubSettingsRepository
        {
            Settings = new AppSettings
            {
                DefaultFileEncoding = FileEncoding.Utf16Be
            }
        };
        var vm = new SettingsViewModel(repo);

        await vm.LoadAsync();

        Assert.Equal(FileEncoding.Utf16Be, vm.DefaultFileEncoding);
    }

    [Fact]
    public async Task SaveAsync_PersistsDefaultFileEncoding()
    {
        var repo = new StubSettingsRepository
        {
            Settings = new AppSettings
            {
                DefaultFileEncoding = FileEncoding.Utf8
            }
        };
        var vm = new SettingsViewModel(repo);
        await vm.LoadAsync();

        vm.DefaultFileEncoding = FileEncoding.Utf16;

        await vm.SaveAsync();

        Assert.Equal(FileEncoding.Utf16, repo.Settings.DefaultFileEncoding);
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
}

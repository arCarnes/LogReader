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
    public async Task LoadAsync_NormalizesFallbackEncodings_ExcludesDefaultAndDuplicates()
    {
        var repo = new StubSettingsRepository
        {
            Settings = new AppSettings
            {
                DefaultFileEncoding = FileEncoding.Utf8,
                EnableTabOverflowDropdown = false,
                FileEncodingFallbacks = new List<FileEncoding>
                {
                    FileEncoding.Utf8,
                    FileEncoding.Ansi,
                    FileEncoding.Utf16,
                    FileEncoding.Ansi,
                    FileEncoding.Utf16Be
                }
            }
        };
        var vm = new SettingsViewModel(repo);

        await vm.LoadAsync();

        Assert.Equal(FileEncoding.Utf8, vm.DefaultFileEncoding);
        Assert.Equal(FileEncoding.Ansi, vm.FallbackEncoding1);
        Assert.Equal(FileEncoding.Utf16, vm.FallbackEncoding2);
        Assert.Equal(FileEncoding.Utf16Be, vm.FallbackEncoding3);
        Assert.False(vm.EnableTabOverflowDropdown);
    }

    [Fact]
    public async Task SaveAsync_PersistsNormalizedFallbackOrder()
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

        vm.DefaultFileEncoding = FileEncoding.Utf8;
        vm.EnableTabOverflowDropdown = false;
        vm.FallbackEncoding1 = FileEncoding.Utf8;   // should be dropped (same as default)
        vm.FallbackEncoding2 = FileEncoding.Ansi;
        vm.FallbackEncoding3 = FileEncoding.Ansi;   // should be deduped

        await vm.SaveAsync();

        Assert.Equal(FileEncoding.Utf8, repo.Settings.DefaultFileEncoding);
        Assert.False(repo.Settings.EnableTabOverflowDropdown);
        Assert.Equal(new[] { FileEncoding.Ansi }, repo.Settings.FileEncodingFallbacks);
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

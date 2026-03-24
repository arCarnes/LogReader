using LogReader.App.Services;
using LogReader.App.ViewModels;
using LogReader.Core.Models;

namespace LogReader.Tests;

public class PatternManagerViewModelTests
{
    [Fact]
    public async Task ImportPatternsCommand_MalformedJson_ShowsErrorAndPreservesPatterns()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"logreader-pattern-import-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(tempPath, "{ invalid json");

        try
        {
            var fileDialogService = new StubFileDialogService
            {
                OnShowOpenFileDialog = _ => new OpenFileDialogResult(true, new[] { tempPath })
            };
            var messageBoxService = new StubMessageBoxService();
            var vm = new PatternManagerViewModel(
                new StubReplacementPatternRepository(),
                fileDialogService,
                messageBoxService);
            vm.Patterns.Add(new ReplacementPatternViewModel
            {
                Name = "Existing",
                FindPattern = "app.log",
                ReplacePattern = "{yyyyMMdd}"
            });

            await vm.ImportPatternsCommand.ExecuteAsync(null);

            Assert.Single(vm.Patterns);
            Assert.Equal("Import Failed", messageBoxService.LastCaption);
            Assert.Contains("Could not import date rolling patterns", messageBoxService.LastMessage);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    [Fact]
    public async Task ImportPatternsCommand_IoFailure_ShowsErrorAndPreservesPatterns()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"missing-patterns-{Guid.NewGuid():N}.json");
        var fileDialogService = new StubFileDialogService
        {
            OnShowOpenFileDialog = _ => new OpenFileDialogResult(true, new[] { missingPath })
        };
        var messageBoxService = new StubMessageBoxService();
        var vm = new PatternManagerViewModel(
            new StubReplacementPatternRepository(),
            fileDialogService,
            messageBoxService);
        vm.Patterns.Add(new ReplacementPatternViewModel
        {
            Name = "Existing",
            FindPattern = "app.log",
            ReplacePattern = "{yyyyMMdd}"
        });

        await vm.ImportPatternsCommand.ExecuteAsync(null);

        Assert.Single(vm.Patterns);
        Assert.Equal("Import Failed", messageBoxService.LastCaption);
        Assert.Contains("Could not import date rolling patterns", messageBoxService.LastMessage);
    }

    [Fact]
    public async Task ExportPatternsCommand_WriteFailure_ShowsError()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"logreader-pattern-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var fileDialogService = new StubFileDialogService
            {
                OnShowSaveFileDialog = _ => new SaveFileDialogResult(true, tempDir)
            };
            var messageBoxService = new StubMessageBoxService();
            var vm = new PatternManagerViewModel(
                new StubReplacementPatternRepository(),
                fileDialogService,
                messageBoxService);
            vm.Patterns.Add(new ReplacementPatternViewModel
            {
                Name = "Existing",
                FindPattern = "app.log",
                ReplacePattern = "{yyyyMMdd}"
            });

            await vm.ExportPatternsCommand.ExecuteAsync(null);

            Assert.Equal("Export Failed", messageBoxService.LastCaption);
            Assert.Contains("Could not export date rolling patterns", messageBoxService.LastMessage);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task TrySaveAsync_SaveFailure_ShowsErrorAndReturnsFalse()
    {
        var repo = new StubReplacementPatternRepository
        {
            OnSaveAsync = static _ => throw new IOException("Disk full.")
        };
        var messageBoxService = new StubMessageBoxService();
        var vm = new PatternManagerViewModel(repo, messageBoxService: messageBoxService);
        vm.Patterns.Add(new ReplacementPatternViewModel
        {
            Name = "Existing",
            FindPattern = "app.log",
            ReplacePattern = "{yyyyMMdd}"
        });

        var saved = await vm.TrySaveAsync();

        Assert.False(saved);
        Assert.Equal("Save Failed", messageBoxService.LastCaption);
        Assert.Contains("Could not save date rolling patterns", messageBoxService.LastMessage);
    }

    [Fact]
    public async Task TrySaveAsync_ValidationError_ShowsWarningAndReturnsFalse()
    {
        var messageBoxService = new StubMessageBoxService();
        var vm = new PatternManagerViewModel(
            new StubReplacementPatternRepository(),
            messageBoxService: messageBoxService);
        vm.Patterns.Add(new ReplacementPatternViewModel
        {
            Name = "Invalid",
            FindPattern = "app.log",
            ReplacePattern = ".txt"
        });

        var saved = await vm.TrySaveAsync();

        Assert.False(saved);
        Assert.Equal("Validation Error", messageBoxService.LastCaption);
    }

    [Fact]
    public async Task TrySaveAsync_BlankName_ShowsWarningAndReturnsFalse()
    {
        var messageBoxService = new StubMessageBoxService();
        var vm = new PatternManagerViewModel(
            new StubReplacementPatternRepository(),
            messageBoxService: messageBoxService);
        vm.Patterns.Add(new ReplacementPatternViewModel
        {
            Name = "",
            FindPattern = "app.log",
            ReplacePattern = "{yyyyMMdd}"
        });

        var saved = await vm.TrySaveAsync();

        Assert.False(saved);
        Assert.Equal("Validation Error", messageBoxService.LastCaption);
    }
}

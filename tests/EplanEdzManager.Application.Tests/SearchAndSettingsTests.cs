using EplanEdzManager.Application;
using EplanEdzManager.Desktop.ViewModels;
using System.IO;
using System.Text.Json;
using Xunit;

namespace EplanEdzManager.Application.Tests;

public sealed class SearchAndSettingsTests
{
    [Fact]
    public async Task Debounce_cancels_the_previous_search_and_runs_the_latest()
    {
        using var coordinator = new SearchCoordinator(TimeSpan.FromMilliseconds(40));
        var calls = 0;
        var first = coordinator.ScheduleAsync(async token => { Interlocked.Increment(ref calls); await Task.Delay(20, token); return "first"; });
        // Submit the replacement synchronously. A real-time 5 ms delay can resume after the
        // 40 ms debounce under parallel CI load, in which case the first operation has already
        // started and two calls are the correct behavior rather than a debounce failure.
        var second = coordinator.ScheduleAsync(async token => { Interlocked.Increment(ref calls); await Task.Delay(1, token); return "second"; });

        Assert.Null(await first);
        Assert.Equal("second", await second);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Debounce_honors_external_cancellation()
    {
        using var coordinator = new SearchCoordinator(TimeSpan.FromSeconds(1));
        using var cancellation = new CancellationTokenSource();
        var task = coordinator.ScheduleAsync(_ => Task.FromResult(1), cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task Settings_round_trip_and_clamp_operational_limits()
    {
        using var workspace = new TestWorkspace();
        var path = Path.Combine(workspace.DirectoryPath, "settings.json");
        var store = new SettingsStore(path);
        var settings = new ApplicationSettings
        {
            DatabasePath = Path.Combine(workspace.DirectoryPath, "custom.db"),
            DefaultExportFolder = workspace.DirectoryPath,
            SearchPageSize = 2,
            ThumbnailCacheSize = 5000,
            RememberWindowSize = false
        };

        await store.SaveAsync(settings);
        var loaded = await store.LoadAsync();

        Assert.Equal(25, loaded.SearchPageSize);
        Assert.Equal(500, loaded.ThumbnailCacheSize);
        Assert.False(loaded.RememberWindowSize);
        Assert.Equal(Path.GetFullPath(settings.DatabasePath), loaded.DatabasePath);
    }

    [Fact]
    public async Task Settings_create_missing_unicode_directories_and_migrate_legacy_backup_default()
    {
        using var workspace = new TestWorkspace();
        var settingsPath = Path.Combine(workspace.DirectoryPath, "missing folder", "设置 中文 (Beta)-_", "settings.json");
        var legacyBackup = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "EplanEdzManager Backups");
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        await File.WriteAllTextAsync(settingsPath, JsonSerializer.Serialize(new
        {
            SettingsSchemaVersion = 2,
            FirstRunCompleted = true,
            DatabasePath = Path.Combine(workspace.DirectoryPath, "旧 index.db"),
            DefaultExportFolder = workspace.DirectoryPath,
            BackupFolder = legacyBackup
        }));

        var loaded = await new SettingsStore(settingsPath).LoadAsync();

        Assert.Equal(3, loaded.SettingsSchemaVersion);
        Assert.Equal(Path.GetFullPath(ApplicationPaths.DefaultBackupDirectory), loaded.BackupFolder, ignoreCase: true);
        Assert.True(loaded.FirstRunCompleted);
    }

    [Fact]
    public async Task Settings_save_creates_the_entire_missing_parent_path()
    {
        using var workspace = new TestWorkspace();
        var settingsPath = Path.Combine(workspace.DirectoryPath, "not-created", "Test User 中文 (portable)-_", "settings.json");

        await new SettingsStore(settingsPath).SaveAsync(new ApplicationSettings());

        Assert.True(File.Exists(settingsPath));
    }

    [Fact]
    public void View_model_starts_with_engineering_defaults_and_disabled_paging()
    {
        using var viewModel = new MainViewModel();

        Assert.Equal(SearchField.All, viewModel.SelectedSearchField.Value);
        Assert.Equal(SearchMatchMode.FullText, viewModel.SelectedMatchMode.Value);
        Assert.Equal(new[] { "全部", "部件编号", "型号", "厂商", "描述" }, viewModel.SearchFields.Select(item => item.Label));
        Assert.Equal("部件目录", viewModel.ViewTitle);
        Assert.Equal("独立模式 · 未连接 EPLAN", viewModel.EplanConnectionSummary);
        Assert.Equal("项目：未连接", viewModel.EplanProjectSummary);
        Assert.Equal("当前选择：未连接", viewModel.EplanSelectionSummary);
        Assert.Equal("所选部件：未连接", viewModel.EplanSelectedPartsSummary);
        Assert.False(viewModel.PreviousPageCommand.CanExecute(null));
        Assert.False(viewModel.NextPageCommand.CanExecute(null));
        Assert.False(viewModel.IsScanning);
    }

    [Fact]
    public void Source_candidate_uses_Chinese_availability_text()
    {
        var available = new SourceCandidate(1, null, "source.edz", null, null, null, 0, 0, false, true);
        var missing = available with { IsAvailable = false };

        Assert.Equal("可用", available.Availability);
        Assert.Equal("缺失", missing.Availability);
    }
}

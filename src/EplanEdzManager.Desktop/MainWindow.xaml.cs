using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using EplanEdzManager.Application;
using EplanEdzManager.Desktop.Integration;
using EplanEdzManager.Desktop.ViewModels;
using Microsoft.Win32;

namespace EplanEdzManager.Desktop;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
    }

    public void ApplyEplanConnection(EplanConnectionState state, IReadOnlyCollection<EplanConnectionState> connections) =>
        _viewModel.ApplyEplanConnection(state, connections);

    public void ActivateFromIntegration()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private async void SearchEplanSelection_Click(object sender, RoutedEventArgs e) =>
        await _viewModel.SearchEplanSelectedPartsAsync();

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.InitializeAsync();
        if (_viewModel.Settings.RememberWindowSize)
        {
            Width = _viewModel.Settings.WindowWidth;
            Height = _viewModel.Settings.WindowHeight;
        }
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        DesktopIntegrationLog.Write("MainWindowClosedStarted");
        _viewModel.CaptureWindowSize(ActualWidth, ActualHeight);
        try { Task.Run(() => _viewModel.PersistWindowStateAsync()).GetAwaiter().GetResult(); } catch (Exception) { }
        DesktopIntegrationLog.Write("WindowStatePersisted");
        _viewModel.Dispose();
        DesktopIntegrationLog.Write("ViewModelDisposed");
        DesktopIntegrationLog.Write("MainWindowClosedCompleted");
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择包含 EDZ 文件的目录",
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true) await _viewModel.AddFolderAsync(dialog.FolderName);
    }

    private async void ExportResource_Click(object sender, RoutedEventArgs e)
    {
        var resource = _viewModel.SelectedResource;
        if (resource is null) return;
        var suggestedName = Path.GetFileName(resource.ArchivePath ?? resource.RawLocator ?? resource.DisplayName);
        var dialog = new SaveFileDialog
        {
            Title = "导出单个 EDZ 资源",
            FileName = suggestedName,
            InitialDirectory = Directory.Exists(_viewModel.Settings.DefaultExportFolder) ? _viewModel.Settings.DefaultExportFolder : null,
            Filter = "所有文件|*.*"
        };
        if (dialog.ShowDialog(this) == true) await _viewModel.ExportSelectedResourceAsync(dialog.FileName);
    }

    private async void PartsGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        var column = e.Column.SortMemberPath switch
        {
            "Manufacturer" => SearchSortColumn.Manufacturer,
            "PartNumber" => SearchSortColumn.PartNumber,
            "TypeNumber" => SearchSortColumn.TypeNumber,
            "Description" => SearchSortColumn.Description,
            "SourceEdz" => SearchSortColumn.SourceEdz,
            _ => SearchSortColumn.Relevance
        };
        await _viewModel.ApplySortAsync(column);
        foreach (var dataGridColumn in PartsGrid.Columns) dataGridColumn.SortDirection = null;
        e.Column.SortDirection = _viewModel.SortDirection == SearchSortDirection.Ascending
            ? ListSortDirection.Ascending
            : ListSortDirection.Descending;
    }

    private async void MyPartsGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        var column = e.Column.SortMemberPath switch
        {
            "Manufacturer" => MyLibrarySort.Manufacturer,
            "PartNumber" => MyLibrarySort.PartNumber,
            "TypeNumber" => MyLibrarySort.TypeNumber,
            "Description" => MyLibrarySort.Description,
            "SourceStatus" => MyLibrarySort.SourceStatus,
            _ => MyLibrarySort.UpdatedUtc
        };
        e.Handled = true;
        await _viewModel.ApplyMyLibrarySortAsync(column);
        foreach (var dataGridColumn in MyPartsGrid.Columns) dataGridColumn.SortDirection = null;
        e.Column.SortDirection = _viewModel.MyLibrarySortDirection == SearchSortDirection.Ascending ? ListSortDirection.Ascending : ListSortDirection.Descending;
    }

    private async void ShowCatalog_Click(object sender, RoutedEventArgs e) => await _viewModel.ShowCatalogAsync();
    private async void ShowMyLibrary_Click(object sender, RoutedEventArgs e) => await _viewModel.ShowMyLibraryAsync();
    private async void ShowFavorites_Click(object sender, RoutedEventArgs e) => await _viewModel.ShowFavoritesAsync();
    private async void RebindSources_Click(object sender, RoutedEventArgs e) => await _viewModel.RebindSourcesAsync();

    private void PartsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateExportButtonState();

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsMyLibraryMode) or nameof(MainViewModel.CanExportMyLibrarySelection) or nameof(MainViewModel.SelectedSavedPartCount))
            UpdateExportButtonState();
    }

    private void UpdateExportButtonState()
    {
        if (_viewModel.IsMyLibraryMode)
        {
            ExportSelectedPartsButton.IsEnabled = _viewModel.CanExportMyLibrarySelection && _viewModel.IsEplanBridgeExecutableAvailable;
            ImportSelectedPartsButton.IsEnabled = _viewModel.CanExportMyLibrarySelection && _viewModel.IsEplanBridgeExecutableAvailable;
            return;
        }
        var selected = PartsGrid.SelectedItems.Cast<PartSummary>().ToArray();
        var canUseSelection = selected.Length > 0 && MainViewModel.PrepareCatalogExport(selected).CanExport;
        ExportSelectedPartsButton.IsEnabled = canUseSelection && _viewModel.IsEplanBridgeExecutableAvailable;
        ImportSelectedPartsButton.IsEnabled = canUseSelection && _viewModel.IsEplanBridgeExecutableAvailable;
    }

    private async void ImportSelectedParts_Click(object sender, RoutedEventArgs e)
    {
        SelectedPartsExportPreparation preparation;
        if (_viewModel.IsMyLibraryMode)
        {
            preparation = await _viewModel.PrepareMyLibraryExportAsync();
        }
        else
        {
            var selected = PartsGrid.SelectedItems.Cast<PartSummary>().ToArray();
            if (selected.Length == 0 && _viewModel.SelectedPart is not null) selected = new[] { _viewModel.SelectedPart };
            preparation = MainViewModel.PrepareCatalogExport(selected);
        }
        if (!preparation.CanExport)
        {
            MessageBox.Show(this, string.Join(Environment.NewLine, preparation.Issues.Select(issue => "• " + issue.Message)),
                "无法导入所选部件", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        new ImportPartsWindow(_viewModel, preparation) { Owner = this }.ShowDialog();
    }

    private async void ExportSelectedParts_Click(object sender, RoutedEventArgs e)
    {
        SelectedPartsExportPreparation preparation;
        if (_viewModel.IsMyLibraryMode)
        {
            preparation = await _viewModel.PrepareMyLibraryExportAsync();
        }
        else
        {
            var selected = PartsGrid.SelectedItems.Cast<PartSummary>().ToArray();
            if (selected.Length == 0 && _viewModel.SelectedPart is not null) selected = new[] { _viewModel.SelectedPart };
            preparation = MainViewModel.PrepareCatalogExport(selected);
        }
        if (!preparation.CanExport)
        {
            MessageBox.Show(this, string.Join(Environment.NewLine, preparation.Issues.Select(issue => "• " + issue.Message)),
                "无法导出所选部件", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var initial = Directory.Exists(_viewModel.Settings.DefaultExportFolder) ? _viewModel.Settings.DefaultExportFolder : null;
        var dialog = new SaveFileDialog
        {
            Title = "导出所选部件",
            Filter = "EPLAN 数据归档|*.edz",
            DefaultExt = ".edz",
            AddExtension = true,
            FileName = "所选部件.edz",
            InitialDirectory = initial,
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true) return;
        if (File.Exists(dialog.FileName)) File.Delete(dialog.FileName);
        var progressWindow = new ExportPartsWindow(_viewModel, preparation, dialog.FileName) { Owner = this };
        progressWindow.ShowDialog();
    }

    private async void CatalogFavorite_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PartSummary part) await _viewModel.ToggleCatalogFavoriteAsync(part);
    }

    private async void SavedFavorite_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SavedPartRow row) await _viewModel.ToggleSavedFavoriteAsync(row);
    }

    private async void AddCatalogPart_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedPart is not null) await _viewModel.AddCatalogPartAsync(_viewModel.SelectedPart);
    }

    private async void FavoriteCatalogPart_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedPart is not null) await _viewModel.ToggleCatalogFavoriteAsync(_viewModel.SelectedPart);
    }

    private async void AddCatalogToCollection_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedPart is null) return;
        var item = ChooseNamedItem("加入集合", _viewModel.Collections);
        if (item is null) return;
        await _viewModel.AddCatalogToCollectionAsync(_viewModel.SelectedPart, item.Id);
    }

    private async void AddCatalogTag_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedPart is null) return;
        var item = ChooseNamedItem("添加标签", _viewModel.Tags);
        if (item is null) return;
        await _viewModel.AddCatalogTagAsync(_viewModel.SelectedPart, item.Id);
    }

    private async void FavoriteSavedPart_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedSavedPart is not null) await _viewModel.ToggleSavedFavoriteAsync(_viewModel.SelectedSavedPart);
    }

    private async void AddSavedToCollection_Click(object sender, RoutedEventArgs e)
    {
        var item = ChooseNamedItem("加入集合", _viewModel.Collections);
        if (item is null || _viewModel.SelectedSavedPart is null) return;
        _viewModel.SelectedSavedPart.IsSelected = true;
        await _viewModel.AddSelectionToCollectionAsync(item.Id);
    }

    private async void AddSavedTag_Click(object sender, RoutedEventArgs e)
    {
        var item = ChooseNamedItem("添加标签", _viewModel.Tags);
        if (item is null || _viewModel.SelectedSavedPart is null) return;
        _viewModel.SelectedSavedPart.IsSelected = true;
        await _viewModel.AddTagToSelectionAsync(item.Id);
    }

    private async void SaveNote_Click(object sender, RoutedEventArgs e) => await _viewModel.SaveSelectedNoteAsync();
    private async void SetPreferredSource_Click(object sender, RoutedEventArgs e) => await _viewModel.SetPreferredSourceAsync();

    private async void CreateCollection_Click(object sender, RoutedEventArgs e)
    {
        var name = PromptText("新建集合", "集合名称");
        if (name is not null) await _viewModel.CreateCollectionAsync(name);
    }

    private async void CreateTag_Click(object sender, RoutedEventArgs e)
    {
        var name = PromptText("新建标签", "标签名称");
        if (name is not null) await _viewModel.CreateTagAsync(name);
    }

    private async void RenameCollection_Click(object sender, RoutedEventArgs e)
    {
        var item = _viewModel.SelectedCollection;
        if (item is null) return;
        var name = PromptText("重命名集合", "新名称", item.Name);
        if (name is not null) await _viewModel.RenameCollectionAsync(item, name);
    }

    private async void RenameTag_Click(object sender, RoutedEventArgs e)
    {
        var item = _viewModel.SelectedTag;
        if (item is null) return;
        var name = PromptText("重命名标签", "新名称", item.Name);
        if (name is not null) await _viewModel.RenameTagAsync(item, name);
    }

    private async void DeleteCollection_Click(object sender, RoutedEventArgs e)
    {
        var item = _viewModel.SelectedCollection;
        if (item is null) return;
        if (MessageBox.Show(this, $"删除集合“{item.Name}”？\n只删除集合关系，不删除已保存部件、索引或 EDZ。", "删除集合", MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK)
            await _viewModel.DeleteCollectionAsync(item);
    }

    private async void DeleteTag_Click(object sender, RoutedEventArgs e)
    {
        var item = _viewModel.SelectedTag;
        if (item is null) return;
        if (MessageBox.Show(this, $"删除标签“{item.Name}”？\n只删除标签关系。", "删除标签", MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK)
            await _viewModel.DeleteTagAsync(item);
    }

    private async void BulkAddCollection_Click(object sender, RoutedEventArgs e)
    {
        var item = ChooseNamedItem("加入集合", _viewModel.Collections);
        if (item is not null) await _viewModel.AddSelectionToCollectionAsync(item.Id);
    }

    private async void BulkAddTag_Click(object sender, RoutedEventArgs e)
    {
        var item = ChooseNamedItem("添加标签", _viewModel.Tags);
        if (item is not null) await _viewModel.AddTagToSelectionAsync(item.Id);
    }

    private async void BulkRemoveCollection_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedCollection is null)
        {
            MessageBox.Show(this, "请先在左侧选择一个集合。", "从集合移除部件", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        await _viewModel.RemoveSelectionFromCurrentCollectionAsync();
    }

    private async void BulkRemoveTag_Click(object sender, RoutedEventArgs e)
    {
        var item = ChooseNamedItem("移除标签", _viewModel.Tags);
        if (item is not null) await _viewModel.RemoveTagFromSelectionAsync(item.Id);
    }

    private async void BulkFavorite_Click(object sender, RoutedEventArgs e) => await _viewModel.FavoriteSelectionAsync();

    private async void BulkRemove_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedSavedPartCount == 0) return;
        if (MessageBox.Show(this, $"从我的部件库移除选中的 {_viewModel.SelectedSavedPartCount} 个部件？\n不会删除 EDZ 文件或部件目录索引。", "移除已保存部件", MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK)
            await _viewModel.RemoveSelectionFromMyLibraryAsync();
    }

    private async void RemoveCurrentSavedPart_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedSavedPart is null) return;
        _viewModel.SelectOnlySavedPart(_viewModel.SelectedSavedPart);
        await BulkRemoveConfirmedAsync();
    }

    private async Task BulkRemoveConfirmedAsync()
    {
        if (MessageBox.Show(this, "从我的部件库移除此部件？不会删除 EDZ。", "移除已保存部件", MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK)
            await _viewModel.RemoveSelectionFromMyLibraryAsync();
    }

    private async void ExportLibrary_Click(object sender, RoutedEventArgs e)
    {
        var initial = Directory.Exists(_viewModel.Settings.BackupFolder) ? _viewModel.Settings.BackupFolder : null;
        var dialog = new SaveFileDialog { Title = "导出我的部件库元数据", Filter = "JSON 备份|*.json", FileName = $"my-library-{DateTime.Now:yyyyMMdd-HHmmss}.json", InitialDirectory = initial };
        if (dialog.ShowDialog(this) == true && await _viewModel.ExportMyLibraryAsync(dialog.FileName) is not null)
            MessageBox.Show(this, "元数据备份已导出。备份不包含 EDZ、图片、宏或 EPLAN DLL。", "导出完成", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void ImportLibrary_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择我的部件库 JSON 备份", Filter = "JSON 备份|*.json|所有文件|*.*", Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        var preview = await _viewModel.PreviewImportAsync(dialog.FileName);
        if (preview is null) return;
        var text = $"格式版本：{preview.SchemaVersion}\n新增：{preview.NewParts}\n已存在：{preview.ExistingParts}\n重复：{preview.DuplicateParts}\n无效：{preview.InvalidParts}";
        if (!preview.CanApply) { MessageBox.Show(this, text + "\n\n此备份不能应用。", "恢复预览", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (MessageBox.Show(this, text + "\n\n确认以事务方式合并恢复？", "恢复预览", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
            await _viewModel.RestoreMyLibraryAsync(preview);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true;
        }
        else if (e.Key == Key.A && Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && _viewModel.IsMyLibraryMode && MyPartsGrid.IsKeyboardFocusWithin)
        {
            _viewModel.SelectAllVisibleSavedParts(true); e.Handled = true;
        }
    }

    private string? PromptText(string title, string label, string initial = "")
    {
        var input = new TextBox { Text = initial, Margin = new Thickness(10), MinWidth = 280 };
        var dialog = new Window { Owner = this, Title = title, SizeToContent = SizeToContent.WidthAndHeight, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
        var panel = new StackPanel { Margin = new Thickness(8) };
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(10, 6, 10, 0) }); panel.Children.Add(input);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = new Button { Content = "确定", IsDefault = true }; var cancel = new Button { Content = "取消", IsCancel = true };
        ok.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(input.Text)) dialog.DialogResult = true; };
        buttons.Children.Add(ok); buttons.Children.Add(cancel); panel.Children.Add(buttons); dialog.Content = panel;
        input.Focus(); input.SelectAll();
        return dialog.ShowDialog() == true ? input.Text.Trim() : null;
    }

    private NamedLibraryItem? ChooseNamedItem(string title, IEnumerable<NamedLibraryItem> items)
    {
        var values = items.ToArray();
        if (values.Length == 0) { MessageBox.Show(this, "请先创建至少一个项目。", title, MessageBoxButton.OK, MessageBoxImage.Information); return null; }
        var combo = new ComboBox { ItemsSource = values, DisplayMemberPath = "Name", SelectedIndex = 0, MinWidth = 280, Margin = new Thickness(10) };
        var dialog = new Window { Owner = this, Title = title, SizeToContent = SizeToContent.WidthAndHeight, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
        var panel = new StackPanel { Margin = new Thickness(8) }; panel.Children.Add(combo);
        var ok = new Button { Content = "确定", IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right };
        ok.Click += (_, _) => dialog.DialogResult = true; panel.Children.Add(ok); dialog.Content = panel;
        return dialog.ShowDialog() == true ? combo.SelectedItem as NamedLibraryItem : null;
    }

    private void Productization_Click(object sender, RoutedEventArgs e) => new ProductizationWindow(_viewModel) { Owner = this }.ShowDialog();

    private void About_Click(object sender, RoutedEventArgs e)
    {
        var window = new ProductizationWindow(_viewModel) { Owner = this };
        window.SelectAbout();
        window.ShowDialog();
    }
}

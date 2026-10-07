using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media.Imaging;
using EplanEdzManager.Application;
using EplanEdzManager.Desktop.Services;

namespace EplanEdzManager.Desktop.ViewModels;

public sealed partial class MainViewModel : BindableBase, IDisposable
{
    private readonly SettingsStore _settingsStore = new();
    private readonly SearchCoordinator _searchCoordinator = new(TimeSpan.FromMilliseconds(300));
    private readonly ThumbnailCache _thumbnailCache = new(150);
    private EdzManagerApplication? _application;
    private CancellationTokenSource? _scanCancellation;
    private CancellationTokenSource? _detailCancellation;
    private CancellationTokenSource? _previewCancellation;
    private int _searchGeneration;
    private bool _initialized;
    private bool _isBusy;
    private bool _isScanning;
    private bool _settingsVisible;
    private string _searchText = string.Empty;
    private Choice<SearchField> _selectedSearchField;
    private Choice<SearchMatchMode> _selectedMatchMode;
    private FilterCount? _selectedManufacturer;
    private FilterCount? _selectedProductGroup;
    private LibraryFolder? _selectedLibrary;
    private PartSummary? _selectedPart;
    private ResourceItem? _selectedResource;
    private BitmapImage? _selectedImage;
    private string _previewMessage = "选择图片资源后按需预览。";
    private string _rawMetadata = "点击“加载原始元数据”读取单个 XML 条目。";
    private int _currentPage = 1;
    private int _pageCount = 1;
    private int _totalCount;
    private SearchSortColumn _sortColumn = SearchSortColumn.Relevance;
    private SearchSortDirection _sortDirection = SearchSortDirection.Ascending;
    private ScanProgressInfo? _scanProgress;
    private ScanSummary? _lastScanSummary;
    private UserFriendlyError? _error;
    private string _statusText = "正在启动…";
    private string _startupMessage = "正在打开索引…";
    private bool _catalogFavoritesOnly;

    public MainViewModel()
    {
        SearchFields = new[]
        {
            new Choice<SearchField>(SearchField.All, "全部"),
            new Choice<SearchField>(SearchField.PartNumber, "部件编号"),
            new Choice<SearchField>(SearchField.TypeNumber, "型号"),
            new Choice<SearchField>(SearchField.Manufacturer, "厂商"),
            new Choice<SearchField>(SearchField.Description, "描述")
        };
        MatchModes = new[]
        {
            new Choice<SearchMatchMode>(SearchMatchMode.FullText, "全文 (FTS5)"),
            new Choice<SearchMatchMode>(SearchMatchMode.Contains, "包含"),
            new Choice<SearchMatchMode>(SearchMatchMode.Exact, "精确匹配")
        };
        _selectedSearchField = SearchFields[0];
        _selectedMatchMode = MatchModes[0];

        SearchCommand = new AsyncRelayCommand(() => SearchNowAsync(resetPage: true));
        PreviousPageCommand = new AsyncRelayCommand(PreviousPageAsync, () => CurrentPage > 1 && !IsBusy);
        NextPageCommand = new AsyncRelayCommand(NextPageAsync, () => CurrentPage < PageCount && !IsBusy);
        ScanAllCommand = new AsyncRelayCommand(() => ScanAsync(scanSelected: false), () => !IsScanning);
        ScanSelectedCommand = new AsyncRelayCommand(() => ScanAsync(scanSelected: true), () => SelectedLibrary is not null && !IsScanning);
        CancelScanCommand = new RelayCommand(CancelScan, () => IsScanning);
        RemoveLibraryCommand = new AsyncRelayCommand(RemoveSelectedLibraryAsync, () => SelectedLibrary is not null && !IsScanning);
        OpenLibraryFolderCommand = new RelayCommand(OpenSelectedLibraryFolder, () => SelectedLibrary is not null);
        OpenSourceFolderCommand = new RelayCommand(OpenSelectedSourceFolder, () => SelectedPart is not null);
        ClearManufacturerCommand = new AsyncRelayCommand(async () => { SelectedManufacturer = null; await Task.CompletedTask; });
        ClearProductGroupCommand = new AsyncRelayCommand(async () => { SelectedProductGroup = null; await Task.CompletedTask; });
        ToggleSettingsCommand = new RelayCommand(() => IsSettingsVisible = !IsSettingsVisible);
        SaveSettingsCommand = new AsyncRelayCommand(SaveSettingsAsync);
        LoadRawMetadataCommand = new AsyncRelayCommand(LoadRawMetadataAsync, () => SelectedPart is not null);
        ClearErrorCommand = new RelayCommand(() => Error = null);
        RefreshCommand = new AsyncRelayCommand(() => RunGuardedAsync(RefreshAllAsync, "REFRESH", "无法刷新索引视图。", setBusy: true));
    }

    public ObservableCollection<PartSummary> Results { get; } = new();
    public ObservableCollection<LibraryFolder> Libraries { get; } = new();
    public ObservableCollection<FilterCount> Manufacturers { get; } = new();
    public ObservableCollection<FilterCount> ProductGroups { get; } = new();
    public ObservableCollection<DiagnosticItem> Diagnostics { get; } = new();
    public ObservableCollection<ResourceItem> PictureResources { get; } = new();
    public ObservableCollection<ResourceItem> MacroResources { get; } = new();
    public ObservableCollection<ResourceItem> DocumentResources { get; } = new();
    public ObservableCollection<ResourceItem> OtherResources { get; } = new();
    public ObservableCollection<ResourceItem> AllResources { get; } = new();

    public IReadOnlyList<Choice<SearchField>> SearchFields { get; }
    public IReadOnlyList<Choice<SearchMatchMode>> MatchModes { get; }
    public IReadOnlyList<int> PageSizes { get; } = new[] { 100, 200, 500 };
    public ApplicationSettings Settings { get; private set; } = new();
    internal EdzManagerApplication ProductizationApplication => _application ?? throw new InvalidOperationException("Application is not initialized.");

    public AsyncRelayCommand SearchCommand { get; }
    public AsyncRelayCommand PreviousPageCommand { get; }
    public AsyncRelayCommand NextPageCommand { get; }
    public AsyncRelayCommand ScanAllCommand { get; }
    public AsyncRelayCommand ScanSelectedCommand { get; }
    public RelayCommand CancelScanCommand { get; }
    public AsyncRelayCommand RemoveLibraryCommand { get; }
    public RelayCommand OpenLibraryFolderCommand { get; }
    public RelayCommand OpenSourceFolderCommand { get; }
    public AsyncRelayCommand ClearManufacturerCommand { get; }
    public AsyncRelayCommand ClearProductGroupCommand { get; }
    public RelayCommand ToggleSettingsCommand { get; }
    public AsyncRelayCommand SaveSettingsCommand { get; }
    public AsyncRelayCommand LoadRawMetadataCommand { get; }
    public RelayCommand ClearErrorCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value)) return;
            if (_initialized) _ = DebouncedSearchAsync();
        }
    }

    public Choice<SearchField> SelectedSearchField
    {
        get => _selectedSearchField;
        set { if (SetProperty(ref _selectedSearchField, value) && _initialized) _ = SearchNowAsync(true); }
    }

    public Choice<SearchMatchMode> SelectedMatchMode
    {
        get => _selectedMatchMode;
        set { if (SetProperty(ref _selectedMatchMode, value) && _initialized) _ = SearchNowAsync(true); }
    }

    public FilterCount? SelectedManufacturer
    {
        get => _selectedManufacturer;
        set { if (SetProperty(ref _selectedManufacturer, value) && _initialized) _ = SearchNowAsync(true); }
    }

    public FilterCount? SelectedProductGroup
    {
        get => _selectedProductGroup;
        set { if (SetProperty(ref _selectedProductGroup, value) && _initialized) _ = SearchNowAsync(true); }
    }

    public LibraryFolder? SelectedLibrary
    {
        get => _selectedLibrary;
        set
        {
            if (!SetProperty(ref _selectedLibrary, value)) return;
            RaiseCommandStates();
        }
    }

    public PartSummary? SelectedPart
    {
        get => _selectedPart;
        set
        {
            if (!SetProperty(ref _selectedPart, value)) return;
            RaiseCommandStates();
            _ = LoadSelectedPartAsync();
        }
    }

    public ResourceItem? SelectedResource
    {
        get => _selectedResource;
        set
        {
            if (!SetProperty(ref _selectedResource, value)) return;
            _ = LoadPreviewAsync();
        }
    }

    public BitmapImage? SelectedImage { get => _selectedImage; private set => SetProperty(ref _selectedImage, value); }
    public string PreviewMessage { get => _previewMessage; private set => SetProperty(ref _previewMessage, value); }
    public string RawMetadata { get => _rawMetadata; private set => SetProperty(ref _rawMetadata, value); }
    public int CurrentPage { get => _currentPage; private set { if (SetProperty(ref _currentPage, value)) RaiseCommandStates(); } }
    public int PageCount { get => _pageCount; private set { if (SetProperty(ref _pageCount, value)) RaiseCommandStates(); } }
    public int TotalCount { get => _totalCount; private set { if (SetProperty(ref _totalCount, value)) { OnPropertyChanged(nameof(HasNoResults)); OnPropertyChanged(nameof(ResultSummary)); } } }
    public bool HasNoResults => _initialized && !IsBusy && TotalCount == 0;
    public string ResultSummary => $"第 {CurrentPage}/{PageCount} 页，共 {TotalCount:N0} 条";
    public bool IsBusy { get => _isBusy; private set { if (SetProperty(ref _isBusy, value)) { OnPropertyChanged(nameof(HasNoResults)); RaiseCommandStates(); } } }
    public bool IsScanning { get => _isScanning; private set { if (SetProperty(ref _isScanning, value)) { OnPropertyChanged(nameof(HasScanSummary)); RaiseCommandStates(); } } }
    public bool IsSettingsVisible { get => _settingsVisible; set => SetProperty(ref _settingsVisible, value); }
    public ScanProgressInfo? ScanProgress { get => _scanProgress; private set { if (SetProperty(ref _scanProgress, value)) OnPropertyChanged(nameof(ScanPercent)); } }
    public double ScanPercent => ScanProgress is null || ScanProgress.TotalFiles <= 0 ? 0 : ScanProgress.CompletedFiles * 100d / ScanProgress.TotalFiles;
    public ScanSummary? LastScanSummary { get => _lastScanSummary; private set { if (SetProperty(ref _lastScanSummary, value)) OnPropertyChanged(nameof(HasScanSummary)); } }
    public bool HasScanSummary => LastScanSummary is not null && !IsScanning;
    public UserFriendlyError? Error { get => _error; private set { if (SetProperty(ref _error, value)) OnPropertyChanged(nameof(HasError)); } }
    public bool HasError => Error is not null;
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public string StartupMessage { get => _startupMessage; private set => SetProperty(ref _startupMessage, value); }
    public SearchSortColumn SortColumn => _sortColumn;
    public SearchSortDirection SortDirection => _sortDirection;
    public bool CatalogFavoritesOnly
    {
        get => _catalogFavoritesOnly;
        set { if (SetProperty(ref _catalogFavoritesOnly, value) && _initialized && IsCatalogMode) _ = SearchNowAsync(true); }
    }

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        await RunGuardedAsync(async () =>
        {
            Settings = await _settingsStore.LoadAsync();
            OnPropertyChanged(nameof(Settings));
            _thumbnailCache.Resize(Settings.ThumbnailCacheSize);
            _application = new EdzManagerApplication(Settings.DatabasePath, new JsonLineLogger(), Settings);
            await _application.InitializeAsync();
            _initialized = true;
            StartupMessage = string.Empty;
            await RefreshNavigationAsync();
            await ExecuteSearchAsync(CreateRequest(1), ++_searchGeneration);
        }, "APP-START", "无法打开本地 EDZ 索引。", setBusy: true);
    }

    public async Task AddFolderAsync(string path)
    {
        if (_application is null) return;
        await RunGuardedAsync(async () =>
        {
            await _application.AddLibraryAsync(path, recursive: true);
            await RefreshNavigationAsync();
            SelectedLibrary = Libraries.FirstOrDefault(item => string.Equals(item.Path, Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), StringComparison.OrdinalIgnoreCase));
        }, "LIB-ADD", "无法添加此 EDZ 库目录。");
    }

    public async Task ExportSelectedResourceAsync(string destinationPath)
    {
        if (_application is null || SelectedResource is null) return;
        await RunGuardedAsync(
            () => _application.ExportResourceAsync(SelectedResource, destinationPath),
            "RES-EXPORT", "无法导出此资源。");
    }

    public async Task<PartPreviewResult> LoadSelectedPartPreviewAsync()
    {
        if (_application is null) return PartPreviewResult.Unavailable("应用尚未完成初始化。");

        try
        {
            PartDetail? detail;
            string displayName;
            if (IsMyLibraryMode)
            {
                var savedPart = SelectedSavedPart;
                if (savedPart is null) return PartPreviewResult.Unavailable("请先选择一个部件。");
                displayName = savedPart.PartNumber ?? savedPart.TypeNumber ?? "未命名部件";
                detail = await _application.GetSavedPartDetailAsync(savedPart.Summary);
            }
            else
            {
                var part = SelectedPart;
                if (part is null) return PartPreviewResult.Unavailable("请先选择一个部件。");
                displayName = part.PartNumber ?? part.TypeNumber ?? "未命名部件";
                detail = await _application.GetPartDetailAsync(part);
            }

            if (detail is null) return PartPreviewResult.Unavailable("当前首选来源不可用，无法读取预览资源。", displayName);
            var resource = PartPreviewSelector.SelectPreferred(detail.Resources);
            if (resource is null)
            {
                var hasMacroOrMechanical = detail.Resources.Any(item => item.Category is ResourceCategory.Macro or ResourceCategory.Mechanical);
                var message = hasMacroOrMechanical
                    ? "该部件含宏或 3D 数据，但 EDZ 中没有可直接显示的预览图片。当前版本不会猜测或伪造宏渲染结果。"
                    : "该部件的 EDZ 中没有可显示的图片资源。";
                return PartPreviewResult.Unavailable(message, displayName);
            }

            var key = resource.EdzPath + "|" + resource.ArchivePath;
            if (!_thumbnailCache.TryGet(key, out var image) || image is null)
            {
                var bytes = await _application.ReadResourceAsync(resource);
                image = ThumbnailDecoder.Decode(bytes, 1400);
                _thumbnailCache.Add(key, image);
            }
            return new PartPreviewResult(image, displayName, resource,
                "EDZ 内嵌图片；它可能是 2D 产品图，也可能是厂商提供的 3D 渲染图。");
        }
        catch (Exception exception)
        {
            Error = UserFriendlyError.FromException("PART-PREVIEW", "无法读取此部件的预览图。", exception);
            return PartPreviewResult.Unavailable("预览读取失败：" + exception.Message);
        }
    }

    public async Task ApplySortAsync(SearchSortColumn column)
    {
        if (_sortColumn == column) _sortDirection = _sortDirection == SearchSortDirection.Ascending ? SearchSortDirection.Descending : SearchSortDirection.Ascending;
        else { _sortColumn = column; _sortDirection = SearchSortDirection.Ascending; }
        OnPropertyChanged(nameof(SortColumn));
        OnPropertyChanged(nameof(SortDirection));
        await SearchNowAsync(resetPage: true);
    }

    public void CaptureWindowSize(double width, double height)
    {
        if (!Settings.RememberWindowSize) return;
        Settings.WindowWidth = width;
        Settings.WindowHeight = height;
    }

    public Task PersistWindowStateAsync() => Settings.RememberWindowSize
        ? _settingsStore.SaveAsync(Settings)
        : Task.CompletedTask;

    public async Task ResetApplicationSettingsAsync()
    {
        var reset = new ApplicationSettings
        {
            DatabasePath = Settings.DatabasePath,
            BackupFolder = Settings.BackupFolder,
            FirstRunCompleted = false
        };
        await _settingsStore.SaveAsync(reset);
        Settings = await _settingsStore.LoadAsync();
        OnPropertyChanged(nameof(Settings));
        StatusText = "应用设置已重置；index.db 与我的部件库未删除。下次启动将重新打开首次运行设置。";
    }

    public async Task RebuildCatalogIndexAsync()
    {
        if (_application is null) return;
        await RunGuardedAsync(async () =>
        {
            await _application.ResetCatalogIndexAsync();
            await RefreshNavigationAsync();
        }, "CATALOG-RESET", "无法重置部件目录索引。", setBusy: true);
        await ScanAsync(scanSelected: false);
    }

    public async Task RecordBackupAsync(string path)
    {
        Settings.LastBackupPath = Path.GetFullPath(path);
        Settings.LastBackupUtc = DateTimeOffset.UtcNow;
        await _settingsStore.SaveAsync(Settings);
        OnPropertyChanged(nameof(Settings));
    }

    private async Task DebouncedSearchAsync()
    {
        if (_application is null) return;
        var generation = ++_searchGeneration;
        try
        {
            if (IsMyLibraryMode)
            {
                var result = await _searchCoordinator.ScheduleAsync(token => _application.SearchMyLibraryAsync(CreateMyLibraryRequest(1), token));
                if (result is not null && generation == _searchGeneration) ApplyMyLibraryResult(result);
            }
            else
            {
                var result = await _searchCoordinator.ScheduleAsync(token => _application.SearchAsync(CreateRequest(1), token));
                if (result is not null && generation == _searchGeneration) ApplySearchResult(result);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Error = UserFriendlyError.FromException("SEARCH", "搜索索引时发生错误。", exception);
        }
    }

    private async Task SearchNowAsync(bool resetPage)
    {
        if (_application is null) return;
        _searchCoordinator.Cancel();
        var page = resetPage ? 1 : CurrentPage;
        var generation = ++_searchGeneration;
        await RunGuardedAsync(
            () => IsMyLibraryMode ? ExecuteMyLibrarySearchAsync(CreateMyLibraryRequest(page), generation) : ExecuteSearchAsync(CreateRequest(page), generation),
            "SEARCH", IsMyLibraryMode ? "搜索我的部件库时发生错误。" : "搜索索引时发生错误。", setBusy: true);
    }

    private async Task ExecuteSearchAsync(SearchRequest request, int generation)
    {
        if (_application is null) return;
        var result = await _application.SearchAsync(request);
        if (generation == _searchGeneration) ApplySearchResult(result);
    }

    private void ApplySearchResult(PagedResult<PartSummary> result)
    {
        Results.Clear();
        foreach (var item in result.Items) Results.Add(item);
        CurrentPage = result.Page;
        PageCount = result.PageCount;
        TotalCount = result.TotalCount;
        SelectedPart = Results.FirstOrDefault();
        _ = UpdateStatusAsync();
        OnPropertyChanged(nameof(ResultSummary));
    }

    private SearchRequest CreateRequest(int page) => new(
        SearchText,
        SelectedSearchField.Value,
        SelectedMatchMode.Value,
        SelectedManufacturer?.Value,
        SelectedProductGroup?.Value,
        _sortColumn,
        _sortDirection,
        page,
        Settings.SearchPageSize,
        CatalogFavoritesOnly);

    private async Task PreviousPageAsync()
    {
        if (CurrentPage <= 1) return;
        CurrentPage--;
        await SearchNowAsync(resetPage: false);
    }

    private async Task NextPageAsync()
    {
        if (CurrentPage >= PageCount) return;
        CurrentPage++;
        await SearchNowAsync(resetPage: false);
    }

    private async Task LoadSelectedPartAsync()
    {
        _detailCancellation?.Cancel();
        _detailCancellation?.Dispose();
        _detailCancellation = new CancellationTokenSource();
        PictureResources.Clear();
        MacroResources.Clear();
        DocumentResources.Clear();
        OtherResources.Clear();
        AllResources.Clear();
        SelectedResource = null;
        SelectedImage = null;
        RawMetadata = "点击“加载原始元数据”读取单个 XML 条目。";
        var part = SelectedPart;
        if (_application is null || part is null) return;
        try
        {
            var detail = await _application.GetPartDetailAsync(part, _detailCancellation.Token);
            foreach (var resource in detail.Resources)
            {
                AllResources.Add(resource);
                var destination = resource.Category switch
                {
                    ResourceCategory.Picture => PictureResources,
                    ResourceCategory.Macro => MacroResources,
                    ResourceCategory.Document => DocumentResources,
                    _ => OtherResources
                };
                destination.Add(resource);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            Error = UserFriendlyError.FromException("PART-DETAIL", "无法加载此部件的详情。", exception);
        }
    }

    private async Task LoadPreviewAsync()
    {
        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        _previewCancellation = new CancellationTokenSource();
        SelectedImage = null;
        var resource = SelectedResource;
        if (_application is null || resource is null)
        {
            PreviewMessage = "选择图片资源后按需预览。";
            return;
        }
        if (!resource.CanPreview)
        {
            PreviewMessage = resource.ExistsInArchive ? "此资源不支持预览，可单独导出。" : "资源在源 EDZ 中缺失。";
            return;
        }
        var key = resource.EdzPath + "|" + resource.ArchivePath;
        if (_thumbnailCache.TryGet(key, out var cached))
        {
            SelectedImage = cached;
            PreviewMessage = string.Empty;
            return;
        }
        PreviewMessage = "正在读取单个图片条目…";
        try
        {
            var bytes = await _application.ReadResourceAsync(resource, cancellationToken: _previewCancellation.Token);
            var image = ThumbnailDecoder.Decode(bytes);
            _thumbnailCache.Add(key, image);
            SelectedImage = image;
            PreviewMessage = string.Empty;
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            PreviewMessage = "无法预览图片。";
            Error = UserFriendlyError.FromException("IMG-PREVIEW", "无法读取此图片资源。", exception);
        }
    }

    private async Task LoadRawMetadataAsync()
    {
        if (_application is null || SelectedPart is null) return;
        await RunGuardedAsync(async () =>
        {
            RawMetadata = await _application.ReadRawMetadataAsync(SelectedPart);
        }, "RAW-METADATA", "无法读取此部件的原始元数据。");
    }

    private async Task ScanAsync(bool scanSelected)
    {
        if (_application is null || IsScanning) return;
        if (scanSelected && SelectedLibrary is null) return;
        if (!scanSelected && Libraries.Count == 0)
        {
            Error = new UserFriendlyError("SCAN-NO-LIBRARY", "请先添加至少一个 EDZ 库目录。", "No indexed_directories row is registered.");
            return;
        }
        _scanCancellation = new CancellationTokenSource();
        IsScanning = true;
        LastScanSummary = null;
        ScanProgress = new ScanProgressInfo(string.Empty, 0, 0, 0, 0, 0, 0, 0, "准备扫描");
        var progress = new Progress<ScanProgressInfo>(value => ScanProgress = value);
        try
        {
            LastScanSummary = scanSelected
                ? await _application.ScanLibraryAsync(SelectedLibrary!.Path, progress, _scanCancellation.Token)
                : await _application.ScanAllAsync(progress, _scanCancellation.Token);
            await RefreshAllAsync();
        }
        catch (OperationCanceledException)
        {
            ScanProgress = ScanProgress is null ? null : ScanProgress with { State = "已取消" };
        }
        catch (Exception exception)
        {
            Error = UserFriendlyError.FromException("SCAN", "扫描 EDZ 库时发生错误。", exception);
        }
        finally
        {
            IsScanning = false;
            _scanCancellation.Dispose();
            _scanCancellation = null;
            await UpdateStatusAsync();
        }
    }

    private void CancelScan() => _scanCancellation?.Cancel();

    private async Task RemoveSelectedLibraryAsync()
    {
        if (_application is null || SelectedLibrary is null) return;
        var path = SelectedLibrary.Path;
        await RunGuardedAsync(async () =>
        {
            await _application.RemoveLibraryAsync(path);
            SelectedLibrary = null;
            await RefreshAllAsync();
        }, "LIB-REMOVE", "无法移除此目录的索引注册。源 EDZ 文件没有被删除。");
    }

    private void OpenSelectedLibraryFolder()
    {
        if (_application is null || SelectedLibrary is null) return;
        try
        {
            var firstEdz = Directory.Exists(SelectedLibrary.Path)
                ? Directory.EnumerateFiles(SelectedLibrary.Path, "*.edz", SearchOption.TopDirectoryOnly).FirstOrDefault()
                : null;
            if (firstEdz is not null) _application.OpenContainingFolder(firstEdz);
            else if (Directory.Exists(SelectedLibrary.Path)) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(SelectedLibrary.Path) { UseShellExecute = true });
            else throw new DirectoryNotFoundException(SelectedLibrary.Path);
        }
        catch (Exception exception) { Error = UserFriendlyError.FromException("OPEN-FOLDER", "无法打开此库目录。", exception); }
    }

    private void OpenSelectedSourceFolder()
    {
        if (_application is null || SelectedPart is null) return;
        try { _application.OpenContainingFolder(SelectedPart.EdzPath); }
        catch (Exception exception) { Error = UserFriendlyError.FromException("OPEN-SOURCE", "源 EDZ 已移动、删除或无法访问。", exception); }
    }

    private async Task SaveSettingsAsync()
    {
        await RunGuardedAsync(async () =>
        {
            await _settingsStore.SaveAsync(Settings);
            _thumbnailCache.Resize(Settings.ThumbnailCacheSize);
            IsSettingsVisible = false;
            if (_application is not null && !string.Equals(_application.DatabasePath, Path.GetFullPath(Settings.DatabasePath), StringComparison.OrdinalIgnoreCase))
            {
                Error = new UserFriendlyError("SETTINGS-RESTART", "数据库位置已保存，将在下次启动时生效。", "The active SQLite connection remains on " + _application.DatabasePath);
            }
            await SearchNowAsync(resetPage: true);
        }, "SETTINGS", "无法保存设置。");
    }

    private async Task RefreshAllAsync()
    {
        await RefreshNavigationAsync();
        await SearchNowAsync(resetPage: false);
    }

    private async Task RefreshNavigationAsync()
    {
        if (_application is null) return;
        var librariesTask = _application.GetLibrariesAsync();
        var manufacturersTask = _application.GetManufacturersAsync();
        var groupsTask = _application.GetProductGroupsAsync();
        var diagnosticsTask = _application.GetDiagnosticsAsync();
        var myNavigationTask = RefreshMyLibraryNavigationAsync();
        await Task.WhenAll(librariesTask, manufacturersTask, groupsTask, diagnosticsTask, myNavigationTask);
        Replace(Libraries, librariesTask.Result);
        Replace(Manufacturers, manufacturersTask.Result);
        Replace(ProductGroups, groupsTask.Result);
        Replace(Diagnostics, diagnosticsTask.Result);
        await UpdateStatusAsync();
    }

    private async Task UpdateStatusAsync()
    {
        if (_application is null) return;
        try
        {
            var state = IsScanning ? "正在扫描" : "就绪";
            var status = await _application.GetStatusAsync(TotalCount, state);
            StatusText = $"{status.IndexedEdz:N0} 个 EDZ  |  {status.IndexedParts:N0} 个部件  |  {TotalCount:N0} 条结果  |  {FormatBytes(status.DatabaseBytes)}  |  {state}";
        }
        catch (Exception exception)
        {
            StatusText = "状态读取失败：" + exception.Message;
        }
    }

    private async Task RunGuardedAsync(Func<Task> action, string code, string message, bool setBusy = false)
    {
        if (setBusy) IsBusy = true;
        try
        {
            Error = null;
            await action();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            Error = UserFriendlyError.FromException(code, message, exception);
        }
        finally
        {
            if (setBusy) IsBusy = false;
        }
    }

    private void RaiseCommandStates()
    {
        PreviousPageCommand.RaiseCanExecuteChanged();
        NextPageCommand.RaiseCanExecuteChanged();
        ScanAllCommand.RaiseCanExecuteChanged();
        ScanSelectedCommand.RaiseCanExecuteChanged();
        CancelScanCommand.RaiseCanExecuteChanged();
        RemoveLibraryCommand.RaiseCanExecuteChanged();
        OpenLibraryFolderCommand.RaiseCanExecuteChanged();
        OpenSourceFolderCommand.RaiseCanExecuteChanged();
        LoadRawMetadataCommand.RaiseCanExecuteChanged();
    }

    private static void Replace<T>(ObservableCollection<T> collection, IEnumerable<T> values)
    {
        collection.Clear();
        foreach (var value in values) collection.Add(value);
    }

    private static string FormatBytes(long value)
    {
        if (value < 1024) return value + " B";
        if (value < 1024 * 1024) return (value / 1024d).ToString("0.0") + " KiB";
        return (value / 1024d / 1024d).ToString("0.0") + " MiB";
    }

    public void Dispose()
    {
        _searchCoordinator.Dispose();
        _scanCancellation?.Cancel();
        _scanCancellation?.Dispose();
        _detailCancellation?.Cancel();
        _detailCancellation?.Dispose();
        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        _thumbnailCache.Clear();
    }
}

public sealed record PartPreviewResult(
    BitmapImage? Image,
    string PartDisplayName,
    ResourceItem? Resource,
    string Message)
{
    public static PartPreviewResult Unavailable(string message, string partDisplayName = "部件") =>
        new(null, partDisplayName, null, message);
}

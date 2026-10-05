using System.Collections.ObjectModel;
using EplanEdzManager.Application;

namespace EplanEdzManager.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    private bool _isMyLibraryMode;
    private bool _favoritesOnly;
    private int _favoriteCount;
    private NamedLibraryItem? _selectedCollection;
    private NamedLibraryItem? _selectedTag;
    private SavedPartRow? _selectedSavedPart;
    private SourceCandidate? _selectedSourceCandidate;
    private MyLibrarySort _myLibrarySort = MyLibrarySort.UpdatedUtc;
    private SearchSortDirection _myLibrarySortDirection = SearchSortDirection.Descending;

    public ObservableCollection<SavedPartRow> SavedParts { get; } = new();
    public ObservableCollection<NamedLibraryItem> Collections { get; } = new();
    public ObservableCollection<NamedLibraryItem> Tags { get; } = new();
    public ObservableCollection<SourceCandidate> SourceCandidates { get; } = new();

    public bool IsMyLibraryMode
    {
        get => _isMyLibraryMode;
        private set
        {
            if (!SetProperty(ref _isMyLibraryMode, value)) return;
            OnPropertyChanged(nameof(IsCatalogMode));
            OnPropertyChanged(nameof(ViewTitle));
        }
    }
    public bool IsCatalogMode => !IsMyLibraryMode;
    public string ViewTitle => IsMyLibraryMode ? (_favoritesOnly ? "收藏" : "我的部件库") : "部件目录";
    public int FavoriteCount { get => _favoriteCount; private set => SetProperty(ref _favoriteCount, value); }
    public int SelectedSavedPartCount => _application?.Selection.Count ?? 0;
    public SearchSortDirection MyLibrarySortDirection => _myLibrarySortDirection;

    public NamedLibraryItem? SelectedCollection
    {
        get => _selectedCollection;
        set
        {
            if (!SetProperty(ref _selectedCollection, value)) return;
            if (value is not null) { _selectedTag = null; OnPropertyChanged(nameof(SelectedTag)); _ = ActivateMyLibraryAsync(false); }
        }
    }

    public NamedLibraryItem? SelectedTag
    {
        get => _selectedTag;
        set
        {
            if (!SetProperty(ref _selectedTag, value)) return;
            if (value is not null) { _selectedCollection = null; OnPropertyChanged(nameof(SelectedCollection)); _ = ActivateMyLibraryAsync(false); }
        }
    }

    public SavedPartRow? SelectedSavedPart
    {
        get => _selectedSavedPart;
        set
        {
            var previous = _selectedSavedPart;
            if (!SetProperty(ref _selectedSavedPart, value)) return;
            if (previous is not null && previous.Note != previous.OriginalNote) _ = SaveNoteForAsync(previous);
            _ = LoadSelectedSavedPartAsync();
        }
    }

    public SourceCandidate? SelectedSourceCandidate
    {
        get => _selectedSourceCandidate;
        set => SetProperty(ref _selectedSourceCandidate, value);
    }

    public Task ShowCatalogAsync()
    {
        IsMyLibraryMode = false;
        _favoritesOnly = false;
        SelectedCollection = null;
        SelectedTag = null;
        return SearchNowAsync(true);
    }

    public Task ShowMyLibraryAsync()
    {
        _selectedCollection = null;
        _selectedTag = null;
        OnPropertyChanged(nameof(SelectedCollection));
        OnPropertyChanged(nameof(SelectedTag));
        return ActivateMyLibraryAsync(false);
    }

    public Task ShowFavoritesAsync() => ActivateMyLibraryAsync(true);

    private async Task ActivateMyLibraryAsync(bool favoritesOnly)
    {
        _favoritesOnly = favoritesOnly;
        IsMyLibraryMode = true;
        if (favoritesOnly)
        {
            _selectedCollection = null;
            _selectedTag = null;
            OnPropertyChanged(nameof(SelectedCollection));
            OnPropertyChanged(nameof(SelectedTag));
        }
        OnPropertyChanged(nameof(ViewTitle));
        await SearchNowAsync(true);
    }

    private MyLibraryRequest CreateMyLibraryRequest(int page) => new(SearchText, _favoritesOnly,
        SelectedCollection?.Id, SelectedTag?.Id, _myLibrarySort, _myLibrarySortDirection,
        page, Settings.SearchPageSize);

    public async Task ApplyMyLibrarySortAsync(MyLibrarySort column)
    {
        if (_myLibrarySort == column) _myLibrarySortDirection = _myLibrarySortDirection == SearchSortDirection.Ascending ? SearchSortDirection.Descending : SearchSortDirection.Ascending;
        else { _myLibrarySort = column; _myLibrarySortDirection = SearchSortDirection.Ascending; }
        OnPropertyChanged(nameof(MyLibrarySortDirection));
        await SearchNowAsync(true);
    }

    private async Task ExecuteMyLibrarySearchAsync(MyLibraryRequest request, int generation)
    {
        if (_application is null) return;
        var result = await _application.SearchMyLibraryAsync(request);
        if (generation == _searchGeneration) ApplyMyLibraryResult(result);
    }

    private void ApplyMyLibraryResult(PagedResult<SavedPartSummary> result)
    {
        SavedParts.Clear();
        foreach (var item in result.Items)
        {
            var row = new SavedPartRow(item, _application?.Selection.Contains(item.Id) == true, OnSavedPartSelectionChanged);
            SavedParts.Add(row);
            if (row.IsSelected) TrackExportReadiness(row);
        }
        CurrentPage = result.Page;
        PageCount = result.PageCount;
        TotalCount = result.TotalCount;
        SelectedSavedPart = SavedParts.FirstOrDefault();
        SelectedPart = null;
        _ = UpdateStatusAsync();
        OnPropertyChanged(nameof(ResultSummary));
    }

    private void OnSavedPartSelectionChanged(SavedPartRow row)
    {
        if (_application is null) return;
        if (row.IsSelected) _application.Selection.Select(new PartSelectionItem(row.Id, row.StableIdentity));
        else _application.Selection.Deselect(row.Id);
        TrackExportReadiness(row);
        OnPropertyChanged(nameof(SelectedSavedPartCount));
    }

    public async Task ToggleCatalogFavoriteAsync(PartSummary part)
    {
        if (_application is null) return;
        await RunGuardedAsync(async () =>
        {
            await _application.SetCatalogFavoriteAsync(part, !part.IsFavorite);
            await RefreshMyLibraryNavigationAsync();
            await SearchNowAsync(false);
        }, "FAVORITE", "无法更新收藏状态。");
    }

    public async Task AddCatalogPartAsync(PartSummary part)
    {
        if (_application is null) return;
        await RunGuardedAsync(async () =>
        {
            var saved = await _application.AddToMyLibraryAsync(part.Id, Settings.AutoAddFavoriteWhenSaving);
            if (!string.IsNullOrWhiteSpace(Settings.DefaultCollection))
            {
                var collection = (await _application.GetCollectionsAsync()).FirstOrDefault(x => string.Equals(x.Name, Settings.DefaultCollection, StringComparison.OrdinalIgnoreCase));
                if (collection is not null) await _application.AddToCollectionAsync(collection.Id, new[] { saved.SavedPartId });
            }
            await RefreshMyLibraryNavigationAsync();
            await SearchNowAsync(false);
        }, "MY-LIBRARY-ADD", "无法将部件加入我的部件库。");
    }

    public async Task AddCatalogToCollectionAsync(PartSummary part, long collectionId)
    {
        if (_application is null) return;
        await RunGuardedAsync(async () =>
        {
            var saved = await _application.AddToMyLibraryAsync(part.Id, Settings.AutoAddFavoriteWhenSaving);
            await _application.AddToCollectionAsync(collectionId, new[] { saved.SavedPartId });
            await RefreshAllAsync();
        }, "CATALOG-COLLECTION", "无法将目录部件加入集合。");
    }

    public async Task AddCatalogTagAsync(PartSummary part, long tagId)
    {
        if (_application is null) return;
        await RunGuardedAsync(async () =>
        {
            var saved = await _application.AddToMyLibraryAsync(part.Id, Settings.AutoAddFavoriteWhenSaving);
            await _application.AddTagAsync(tagId, new[] { saved.SavedPartId });
            await RefreshAllAsync();
        }, "CATALOG-TAG", "无法为目录部件添加标签。");
    }

    public async Task ToggleSavedFavoriteAsync(SavedPartRow row)
    {
        if (_application is null) return;
        await RunGuardedAsync(async () =>
        {
            await _application.SetFavoriteAsync(row.Id, !row.IsFavorite);
            row.SetFavorite(!row.IsFavorite);
            await RefreshMyLibraryNavigationAsync();
            if (_favoritesOnly && !row.IsFavorite) await SearchNowAsync(false);
        }, "FAVORITE", "无法更新收藏状态。");
    }

    public Task SaveSelectedNoteAsync() => SelectedSavedPart is null ? Task.CompletedTask : SaveNoteForAsync(SelectedSavedPart);

    private async Task SaveNoteForAsync(SavedPartRow row)
    {
        if (_application is null) return;
        await RunGuardedAsync(async () =>
        {
            await _application.SaveNoteAsync(row.Id, row.Note);
            row.MarkNoteSaved();
        }, "NOTE", "无法保存备注。");
    }

    public async Task<long?> CreateCollectionAsync(string name)
    {
        if (_application is null) return null;
        long? created = null;
        await RunGuardedAsync(async () =>
        {
            var selected = _application.Selection.Snapshot().Select(x => x.SavedPartId).ToArray();
            created = await _application.CreateCollectionAsync(name, selected);
            await RefreshMyLibraryNavigationAsync();
        }, "COLLECTION-CREATE", "无法创建集合。");
        return created;
    }

    public async Task<long?> CreateTagAsync(string name)
    {
        if (_application is null) return null;
        long? created = null;
        await RunGuardedAsync(async () =>
        {
            var selected = _application.Selection.Snapshot().Select(x => x.SavedPartId).ToArray();
            created = await _application.CreateTagAsync(name, selected);
            await RefreshMyLibraryNavigationAsync();
        }, "TAG-CREATE", "无法创建标签。");
        return created;
    }

    public async Task RenameCollectionAsync(NamedLibraryItem item, string name)
    {
        if (_application is null) return;
        await RunGuardedAsync(async () => { await _application.RenameCollectionAsync(item.Id, name); await RefreshMyLibraryNavigationAsync(); }, "COLLECTION-RENAME", "无法重命名集合。");
    }

    public async Task RenameTagAsync(NamedLibraryItem item, string name)
    {
        if (_application is null) return;
        await RunGuardedAsync(async () => { await _application.RenameTagAsync(item.Id, name); await RefreshMyLibraryNavigationAsync(); }, "TAG-RENAME", "无法重命名标签。");
    }

    public async Task DeleteCollectionAsync(NamedLibraryItem item)
    {
        if (_application is null) return;
        await RunGuardedAsync(async () => { await _application.DeleteCollectionAsync(item.Id); SelectedCollection = null; await RefreshMyLibraryNavigationAsync(); await SearchNowAsync(true); }, "COLLECTION-DELETE", "无法删除集合关系。");
    }

    public async Task DeleteTagAsync(NamedLibraryItem item)
    {
        if (_application is null) return;
        await RunGuardedAsync(async () => { await _application.DeleteTagAsync(item.Id); SelectedTag = null; await RefreshMyLibraryNavigationAsync(); await SearchNowAsync(true); }, "TAG-DELETE", "无法删除标签关系。");
    }

    public async Task AddSelectionToCollectionAsync(long collectionId)
    {
        if (_application is null) return;
        var ids = _application.Selection.Snapshot().Select(x => x.SavedPartId).ToArray();
        await RunGuardedAsync(async () => { await _application.AddToCollectionAsync(collectionId, ids); await RefreshAllAsync(); }, "COLLECTION-BULK", "批量加入集合失败。");
    }

    public async Task RemoveSelectionFromCurrentCollectionAsync()
    {
        if (_application is null || SelectedCollection is null) return;
        var ids = _application.Selection.Snapshot().Select(x => x.SavedPartId).ToArray();
        await RunGuardedAsync(async () => { await _application.RemoveFromCollectionAsync(SelectedCollection.Id, ids); await RefreshAllAsync(); }, "COLLECTION-BULK", "从集合移除部件失败。");
    }

    public async Task AddTagToSelectionAsync(long tagId)
    {
        if (_application is null) return;
        var ids = _application.Selection.Snapshot().Select(x => x.SavedPartId).ToArray();
        await RunGuardedAsync(async () => { await _application.AddTagAsync(tagId, ids); await RefreshAllAsync(); }, "TAG-BULK", "批量添加标签失败。");
    }

    public async Task RemoveTagFromSelectionAsync(long tagId)
    {
        if (_application is null) return;
        var ids = _application.Selection.Snapshot().Select(x => x.SavedPartId).ToArray();
        await RunGuardedAsync(async () => { await _application.RemoveTagAsync(tagId, ids); await RefreshAllAsync(); }, "TAG-BULK", "批量移除标签失败。");
    }

    public async Task FavoriteSelectionAsync()
    {
        if (_application is null) return;
        var ids = _application.Selection.Snapshot().Select(x => x.SavedPartId).ToArray();
        await RunGuardedAsync(async () =>
        {
            await _application.SetFavoritesAsync(ids, true);
            await RefreshAllAsync();
        }, "FAVORITE-BULK", "批量收藏失败。");
    }

    public async Task RemoveSelectionFromMyLibraryAsync()
    {
        if (_application is null) return;
        var ids = _application.Selection.Snapshot().Select(x => x.SavedPartId).ToArray();
        await RunGuardedAsync(async () =>
        {
            await _application.RemoveFromMyLibraryAsync(ids);
            _application.Selection.Clear();
            ClearExportReadiness();
            OnPropertyChanged(nameof(SelectedSavedPartCount));
            await RefreshAllAsync();
        }, "MY-LIBRARY-REMOVE", "从我的部件库移除失败。EDZ 和部件目录索引未被删除。");
    }

    public async Task SetPreferredSourceAsync()
    {
        if (_application is null || SelectedSavedPart is null || SelectedSourceCandidate is null) return;
        await RunGuardedAsync(async () =>
        {
            await _application.SetPreferredSourceAsync(SelectedSavedPart.Id, SelectedSourceCandidate.Id);
            await SearchNowAsync(false);
        }, "SOURCE-PREFERRED", "无法设置首选来源。");
    }

    public async Task RebindSourcesAsync()
    {
        if (_application is null) return;
        await RunGuardedAsync(async () => { await _application.RebindSavedPartsAsync(); await RefreshAllAsync(); }, "SOURCE-REBIND", "来源重新绑定失败。", true);
    }

    public Task<string?> ExportMyLibraryAsync(string path) => _application is null ? Task.FromResult<string?>(null) : ExportBackupCoreAsync(path);
    private async Task<string?> ExportBackupCoreAsync(string path)
    {
        string? result = null;
        await RunGuardedAsync(async () => result = await _application!.ExportMyLibraryAsync(path), "BACKUP-EXPORT", "导出我的部件库备份失败。", true);
        return result;
    }
    public Task<BackupPreview?> PreviewImportAsync(string path) => _application is null ? Task.FromResult<BackupPreview?>(null) : PreviewBackupCoreAsync(path);
    private async Task<BackupPreview?> PreviewBackupCoreAsync(string path)
    {
        BackupPreview? result = null;
        await RunGuardedAsync(async () => result = await _application!.PreviewMyLibraryImportAsync(path), "BACKUP-PREVIEW", "无法预览此备份。", true);
        return result;
    }
    public async Task RestoreMyLibraryAsync(BackupPreview preview)
    {
        if (_application is null) return;
        await RunGuardedAsync(async () => { await _application.RestoreMyLibraryAsync(preview); await RefreshAllAsync(); }, "BACKUP-RESTORE", "恢复我的部件库失败。", true);
    }

    public void SelectAllVisibleSavedParts(bool selected)
    {
        foreach (var row in SavedParts) row.IsSelected = selected;
    }

    public void SelectOnlySavedPart(SavedPartRow row)
    {
        if (_application is null) return;
        _application.Selection.Clear();
        ClearExportReadiness();
        foreach (var item in SavedParts) item.IsSelected = ReferenceEquals(item, row);
        if (!row.IsSelected) row.IsSelected = true;
        _application.Selection.Select(new PartSelectionItem(row.Id, row.StableIdentity));
        OnPropertyChanged(nameof(SelectedSavedPartCount));
    }

    private async Task RefreshMyLibraryNavigationAsync()
    {
        if (_application is null) return;
        var collectionsTask = _application.GetCollectionsAsync();
        var tagsTask = _application.GetTagsAsync();
        var favoritesTask = _application.SearchMyLibraryAsync(new MyLibraryRequest(FavoritesOnly: true, PageSize: 25));
        await Task.WhenAll(collectionsTask, tagsTask, favoritesTask);
        var selectedCollectionId = _selectedCollection?.Id;
        var selectedTagId = _selectedTag?.Id;
        Replace(Collections, collectionsTask.Result);
        Replace(Tags, tagsTask.Result);
        _selectedCollection = selectedCollectionId is null ? null : Collections.FirstOrDefault(x => x.Id == selectedCollectionId.Value);
        _selectedTag = selectedTagId is null ? null : Tags.FirstOrDefault(x => x.Id == selectedTagId.Value);
        OnPropertyChanged(nameof(SelectedCollection));
        OnPropertyChanged(nameof(SelectedTag));
        FavoriteCount = favoritesTask.Result.TotalCount;
    }

    private async Task LoadSelectedSavedPartAsync()
    {
        _detailCancellation?.Cancel();
        _detailCancellation?.Dispose();
        _detailCancellation = new CancellationTokenSource();
        ClearResources();
        SourceCandidates.Clear();
        var row = SelectedSavedPart;
        if (_application is null || row is null) return;
        try
        {
            var sourcesTask = _application.GetSourceCandidatesAsync(row.Id, _detailCancellation.Token);
            var detailTask = _application.GetSavedPartDetailAsync(row.Summary, _detailCancellation.Token);
            await Task.WhenAll(sourcesTask, detailTask);
            foreach (var source in sourcesTask.Result) SourceCandidates.Add(source);
            SelectedSourceCandidate = SourceCandidates.FirstOrDefault(x => x.IsPreferred) ?? SourceCandidates.FirstOrDefault();
            if (detailTask.Result is not null) PopulateResources(detailTask.Result);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { Error = UserFriendlyError.FromException("MY-LIBRARY-DETAIL", "无法加载已保存部件详情。", exception); }
    }

    private void ClearResources()
    {
        PictureResources.Clear(); MacroResources.Clear(); DocumentResources.Clear(); OtherResources.Clear(); AllResources.Clear();
        SelectedResource = null; SelectedImage = null;
    }

    private void PopulateResources(PartDetail detail)
    {
        foreach (var resource in detail.Resources)
        {
            AllResources.Add(resource);
            (resource.Category switch
            {
                ResourceCategory.Picture => PictureResources,
                ResourceCategory.Macro => MacroResources,
                ResourceCategory.Document => DocumentResources,
                _ => OtherResources
            }).Add(resource);
        }
    }
}

public sealed class SavedPartRow : BindableBase
{
    private readonly Action<SavedPartRow> _selectionChanged;
    private bool _isSelected;
    private bool _isFavorite;
    private string _note;

    public SavedPartRow(SavedPartSummary summary, bool selected, Action<SavedPartRow> selectionChanged)
    {
        Summary = summary;
        _isSelected = selected;
        _isFavorite = summary.IsFavorite;
        _note = summary.Note;
        OriginalNote = summary.Note;
        _selectionChanged = selectionChanged;
    }

    public SavedPartSummary Summary { get; }
    public long Id => Summary.Id;
    public string StableIdentity => Summary.StableIdentity;
    public string? Manufacturer => Summary.Manufacturer;
    public string? PartNumber => Summary.PartNumber;
    public string? TypeNumber => Summary.TypeNumber;
    public string? Description => Summary.Description;
    public string Collections => Summary.Collections;
    public string Tags => Summary.Tags;
    public string SourceStatusText => Summary.SourceStatusText;
    public string ResourceStatus => Summary.ResourceCount == 0 ? "无资源" : Summary.ExistingResourceCount == Summary.ResourceCount ? $"完整 {Summary.ResourceCount}" : $"缺失 {Summary.ResourceCount - Summary.ExistingResourceCount}/{Summary.ResourceCount}";
    public int TotalSourceCount => Summary.TotalSourceCount;
    public string PreferredSourceName => Summary.PreferredSourceName;
    public string OriginalNote { get; private set; }
    public string Note { get => _note; set => SetProperty(ref _note, value); }
    public bool IsFavorite => _isFavorite;
    public string FavoriteGlyph => IsFavorite ? "★" : "☆";
    public bool IsSelected
    {
        get => _isSelected;
        set { if (SetProperty(ref _isSelected, value)) _selectionChanged(this); }
    }
    public void SetFavorite(bool value) { if (_isFavorite == value) return; _isFavorite = value; OnPropertyChanged(nameof(IsFavorite)); OnPropertyChanged(nameof(FavoriteGlyph)); }
    public void MarkNoteSaved() => OriginalNote = Note;
}

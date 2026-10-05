using EplanEdzManager.AddIn.Protocol;
using EplanEdzManager.Application;
using EplanEdzManager.Desktop.Integration;
using System.IO;

namespace EplanEdzManager.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    private EplanConnectionState? _eplanConnection;
    private string _eplanConnectionSummary = "独立模式 · 未连接 EPLAN";
    private string _eplanProjectSummary = "项目：未连接";
    private string _eplanSelectionSummary = "当前选择：未连接";
    private string _eplanSelectedPartsSummary = "所选部件：未连接";
    private string _eplanInstancesSummary = "EPLAN 实例：0 个已连接";

    public bool IsEplanConnected => _eplanConnection?.Connected == true;
    public string AddInStatusText => _eplanConnection is null ? GetRecentAddInStatus() : _eplanConnection.Connected ? "已连接" : "已断开";
    public string EplanConnectionSummary { get => _eplanConnectionSummary; private set => SetProperty(ref _eplanConnectionSummary, value); }
    public string EplanProjectSummary { get => _eplanProjectSummary; private set => SetProperty(ref _eplanProjectSummary, value); }
    public string EplanSelectionSummary { get => _eplanSelectionSummary; private set => SetProperty(ref _eplanSelectionSummary, value); }
    public string EplanSelectedPartsSummary { get => _eplanSelectedPartsSummary; private set => SetProperty(ref _eplanSelectedPartsSummary, value); }
    public string EplanInstancesSummary { get => _eplanInstancesSummary; private set => SetProperty(ref _eplanInstancesSummary, value); }
    public bool CanSearchEplanSelection => IsEplanConnected &&
        _eplanConnection?.Context?.SelectedParts.Status == ContextAvailability.Available &&
        _eplanConnection.Context.SelectedParts.Items.Any(item => !string.IsNullOrWhiteSpace(item.PartNumber));

    public void ApplyEplanConnection(EplanConnectionState state, IReadOnlyCollection<EplanConnectionState> connections)
    {
        _eplanConnection = state;
        EplanInstancesSummary = FormatInstances(connections);
        OnPropertyChanged(nameof(IsEplanConnected));
        OnPropertyChanged(nameof(AddInStatusText));
        OnPropertyChanged(nameof(CanSearchEplanSelection));
        if (!state.Connected)
        {
            EplanConnectionSummary = "已断开 · " + (string.IsNullOrWhiteSpace(state.DisconnectReason) ? "EPLAN 已关闭" : state.DisconnectReason);
            EplanProjectSummary = "项目：已断开";
            EplanSelectionSummary = "当前选择：已断开";
            EplanSelectedPartsSummary = "所选部件：已断开";
            return;
        }

        EplanConnectionSummary = $"已连接 · EPLAN {Display(state.Context?.EplanVersion, state.EplanVersion)} · 进程 ID {state.EplanProcessId}";
        var context = state.Context;
        if (context is null)
        {
            EplanProjectSummary = "项目：等待上下文";
            EplanSelectionSummary = "当前选择：等待上下文";
            EplanSelectedPartsSummary = "所选部件：等待上下文";
            return;
        }

        EplanProjectSummary = "项目：" + Display(context.ProjectName);
        EplanSelectionSummary = "当前选择：" + Display(context.SelectionSummary);
        EplanSelectedPartsSummary = context.SelectedParts.Status == ContextAvailability.Available
            ? "所选部件：" + string.Join(", ", context.SelectedParts.Items.Select(item =>
                string.IsNullOrWhiteSpace(item.Variant) ? item.PartNumber : item.PartNumber + "/" + item.Variant))
            : "所选部件：" + LocalizeAvailability(context.SelectedParts.Status) +
              (string.IsNullOrWhiteSpace(context.SelectedParts.Detail) ? string.Empty : " (" + context.SelectedParts.Detail + ")");
    }

    public async Task SearchEplanSelectedPartsAsync()
    {
        var partNumbers = _eplanConnection?.Context?.SelectedParts.Items
            .Select(item => item.PartNumber.Trim())
            .Where(item => item.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? Array.Empty<string>();
        if (_application is null || partNumbers.Length == 0) return;

        _isMyLibraryMode = false;
        _favoritesOnly = false;
        _selectedManufacturer = null;
        _selectedProductGroup = null;
        _selectedSearchField = SearchFields.First(item => item.Value == SearchField.PartNumber);
        _selectedMatchMode = MatchModes.First(item => item.Value == SearchMatchMode.Exact);
        _searchText = string.Join(", ", partNumbers);
        OnPropertyChanged(nameof(IsMyLibraryMode));
        OnPropertyChanged(nameof(IsCatalogMode));
        OnPropertyChanged(nameof(ViewTitle));
        OnPropertyChanged(nameof(SelectedManufacturer));
        OnPropertyChanged(nameof(SelectedProductGroup));
        OnPropertyChanged(nameof(SelectedSearchField));
        OnPropertyChanged(nameof(SelectedMatchMode));
        OnPropertyChanged(nameof(SearchText));

        await RunGuardedAsync(async () =>
        {
            var found = new Dictionary<string, PartSummary>(StringComparer.Ordinal);
            foreach (var partNumber in partNumbers)
            {
                var result = await _application.SearchAsync(new SearchRequest(
                    partNumber,
                    SearchField.PartNumber,
                    SearchMatchMode.Exact,
                    null,
                    null,
                    SearchSortColumn.PartNumber,
                    SearchSortDirection.Ascending,
                    1,
                    Settings.SearchPageSize,
                    false));
                foreach (var item in result.Items)
                    found[item.PackageKey + "\u001f" + item.PartNumber + "\u001f" + item.Variant] = item;
            }

            Results.Clear();
            foreach (var item in found.Values.OrderBy(item => item.Manufacturer).ThenBy(item => item.PartNumber).ThenBy(item => item.Variant))
                Results.Add(item);
            CurrentPage = 1;
            PageCount = 1;
            TotalCount = Results.Count;
            SelectedPart = Results.FirstOrDefault();
            StatusText = $"EPLAN 所选部件搜索：请求 {partNumbers.Length} 个，目录匹配 {Results.Count} 个。";
        }, "EPLAN-CONTEXT-SEARCH", "无法搜索 EPLAN 当前选择中的部件。", setBusy: true);
    }

    private static string Display(ContextValue? value, string fallback = "未知")
    {
        if (value is null) return string.IsNullOrWhiteSpace(fallback) ? "未知" : fallback;
        if (value.Status == ContextAvailability.Available) return value.Value;
        return LocalizeAvailability(value.Status) + (string.IsNullOrWhiteSpace(value.Detail) ? string.Empty : " (" + value.Detail + ")");
    }

    private static string LocalizeAvailability(string status) => status switch
    {
        ContextAvailability.Available => "可用",
        ContextAvailability.Unavailable => "不可用",
        ContextAvailability.Unsupported => "不支持",
        _ => "未知"
    };

    private static string FormatInstances(IReadOnlyCollection<EplanConnectionState> connections)
    {
        var connected = connections.Where(item => item.Connected)
            .OrderBy(item => item.EplanProcessId)
            .ToArray();
        if (connected.Length == 0) return "EPLAN 实例：0 个已连接";
        if (connected.Length == 1) return "EPLAN 实例：1 个已连接";

        return $"EPLAN 实例：{connected.Length} 个已连接 · " + string.Join(" | ", connected.Select(item =>
            $"进程 ID {item.EplanProcessId} · {Display(item.Context?.EplanVersion, item.EplanVersion)} · 项目 {Display(item.Context?.ProjectName)}"));
    }

    private static string GetRecentAddInStatus()
    {
        var directory = Path.Combine(ApplicationPaths.LocalDataRoot, "Logs", "AddIn");
        if (!Directory.Exists(directory)) return "未检测到";
        var newest = Directory.EnumerateFiles(directory).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        if (newest is null) return "未检测到";
        return File.GetLastWriteTimeUtc(newest) >= DateTime.UtcNow.AddDays(-7) ? "最近已连接" : "已断开";
    }
}

namespace EplanEdzManager.Application;

public static class InstallationLayout
{
    public static string? FindAddInAssembly(string? startDirectory = null)
    {
        var start = Path.GetFullPath(startDirectory ?? AppContext.BaseDirectory);
        var installed = Path.GetFullPath(Path.Combine(start, "..", "AddIn", "EplanEdzManager.EplanAddIn.dll"));
        if (File.Exists(installed)) return installed;
        var direct = Path.Combine(start, "EplanEdzManager.EplanAddIn.dll");
        if (File.Exists(direct)) return direct;
        return null;
    }

    public static string? FindBridgeExecutable(string? startDirectory = null)
    {
        var start = Path.GetFullPath(startDirectory ?? AppContext.BaseDirectory);
        var installed = Path.GetFullPath(Path.Combine(start, "..", "Bridge", "EplanEdzManager.EplanBridge.exe"));
        return File.Exists(installed) ? installed : null;
    }
}

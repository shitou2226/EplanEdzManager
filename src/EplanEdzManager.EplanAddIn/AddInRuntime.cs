using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using EplanEdzManager.AddIn.Protocol;
using Newtonsoft.Json;

namespace EplanEdzManager.EplanAddIn;

internal static class AddInRuntime
{
    private static readonly object Sync = new object();
    private static string _originalAssemblyPath = string.Empty;

    public static string SessionId { get; } = "eplan-" + Process.GetCurrentProcess().Id + "-" + Guid.NewGuid().ToString("N");

    public static string EplanVersion
    {
        get
        {
            try
            {
                return Process.GetCurrentProcess().MainModule?.FileVersionInfo.FileVersion ?? string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }
    }

    public static bool IsSupportedRuntime => EplanVersionCompatibility.IsSupported(EplanVersion);

    public static void SetOriginalAssemblyPath(string path)
    {
        lock (Sync) _originalAssemblyPath = path ?? string.Empty;
    }

    public static AddInConfiguration LoadConfiguration()
    {
        string registeredPath;
        lock (Sync) registeredPath = _originalAssemblyPath;
        var assemblyPath = string.IsNullOrWhiteSpace(registeredPath) ? Assembly.GetExecutingAssembly().Location : registeredPath;
        var directory = Path.GetDirectoryName(assemblyPath) ?? string.Empty;
        var configPath = Path.Combine(directory, "EplanEdzManager.AddIn.json");
        if (!File.Exists(configPath))
            throw new FileNotFoundException("The Add-In configuration file was not found. Use the installer-generated file or copy EplanEdzManager.AddIn.example.json beside the Add-In DLL.", configPath);
        var config = JsonConvert.DeserializeObject<AddInConfiguration>(File.ReadAllText(configPath))
            ?? throw new InvalidDataException("The Add-In configuration file is empty or invalid.");
        var validation = config.Validate(directory);
        if (!validation.IsValid) throw new InvalidDataException(validation.Error);
        config.DesktopExecutablePath = validation.DesktopExecutablePath;
        if (!File.Exists(config.DesktopExecutablePath))
            throw new FileNotFoundException("The configured Desktop executable does not exist.", config.DesktopExecutablePath);
        if ((File.GetAttributes(config.DesktopExecutablePath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The configured Desktop executable must not be a symbolic link or reparse point.");
        return config;
    }
}

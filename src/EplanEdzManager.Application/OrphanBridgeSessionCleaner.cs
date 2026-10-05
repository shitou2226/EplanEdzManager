using System.Diagnostics;
using System.Text.Json;

namespace EplanEdzManager.Application;

public sealed class OrphanBridgeSessionCleaner
{
    public const string SessionMarkerFileName = ".eplan-edz-manager-session.json";
    public static TimeSpan DefaultMinimumAge { get; } = TimeSpan.FromHours(24);

    private readonly string _root;
    private readonly TimeProvider _timeProvider;
    private readonly Func<int, bool> _isMatchingBridgeProcess;

    public OrphanBridgeSessionCleaner(string? root = null, TimeProvider? timeProvider = null, Func<int, bool>? isMatchingBridgeProcess = null)
    {
        _root = NormalizeRoot(root ?? Path.Combine(Path.GetTempPath(), "EplanEdzManager", "Bridge"));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _isMatchingBridgeProcess = isMatchingBridgeProcess ?? IsMatchingBridgeProcess;
    }

    public OrphanSessionScan Scan(TimeSpan? minimumAge = null)
    {
        var threshold = minimumAge ?? DefaultMinimumAge;
        if (threshold < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(minimumAge));
        var now = _timeProvider.GetUtcNow();
        var sessions = new List<OrphanSessionInfo>();
        if (!Directory.Exists(_root)) return new OrphanSessionScan(_root, now, threshold, sessions);
        if (IsReparsePoint(_root))
        {
            sessions.Add(new OrphanSessionInfo(string.Empty, _root, 0, default, string.Empty, TimeSpan.Zero, 0, false,
                "Expected Bridge root is a reparse point; scan refused."));
            return new OrphanSessionScan(_root, now, threshold, sessions);
        }

        string[] directories;
        try
        {
            directories = Directory.GetDirectories(_root, "*", SearchOption.TopDirectoryOnly);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            sessions.Add(new OrphanSessionInfo(string.Empty, _root, 0, default, string.Empty, TimeSpan.Zero, 0, false,
                "Bridge root could not be enumerated; scan skipped: " + exception.GetType().Name + "."));
            return new OrphanSessionScan(_root, now, threshold, sessions);
        }

        foreach (var directory in directories)
        {
            try { sessions.Add(InspectDirectory(directory, threshold, now)); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                sessions.Add(new OrphanSessionInfo(Path.GetFileName(directory), directory, 0, default, string.Empty,
                    TimeSpan.Zero, 0, false, "Session could not be inspected; skipped: " + exception.GetType().Name + "."));
            }
        }
        return new OrphanSessionScan(_root, now, threshold, sessions);
    }

    public OrphanCleanupResult Clean(OrphanSessionScan dryRun)
    {
        ArgumentNullException.ThrowIfNull(dryRun);
        if (!string.Equals(NormalizeRoot(dryRun.RootPath), _root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The dry-run scan belongs to a different Bridge root.");

        var deleted = 0;
        long deletedBytes = 0;
        var skipped = new List<string>();
        var errors = new List<string>();
        foreach (var proposed in dryRun.Sessions.Where(item => item.CanDelete))
        {
            var current = InspectDirectory(proposed.DirectoryPath, dryRun.MinimumAge, _timeProvider.GetUtcNow());
            if (!current.CanDelete || !string.Equals(current.SessionId, proposed.SessionId, StringComparison.OrdinalIgnoreCase))
            {
                skipped.Add(proposed.DirectoryPath + ": safety conditions changed after dry run.");
                continue;
            }
            try
            {
                Directory.Delete(current.DirectoryPath, recursive: true);
                deleted++;
                deletedBytes += current.SizeBytes;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                errors.Add(current.DirectoryPath + ": " + exception.GetType().Name + ": " + exception.Message);
            }
        }
        return new OrphanCleanupResult(deleted, deletedBytes, skipped, errors);
    }

    private OrphanSessionInfo InspectDirectory(string directory, TimeSpan threshold, DateTimeOffset now)
    {
        var full = NormalizeChild(directory);
        var name = Path.GetFileName(full);
        if (!Guid.TryParseExact(name, "N", out var sessionGuid)) return Rejected(name, full, "Directory name is not a session GUID.");
        if (IsReparsePoint(full) || ContainsReparsePoint(full)) return Rejected(name, full, "A reparse point exists in the session path.");
        var markerPath = Path.Combine(full, SessionMarkerFileName);
        if (!File.Exists(markerPath)) return Rejected(name, full, "Session marker is missing.");

        SessionMarker? marker;
        try
        {
            marker = JsonSerializer.Deserialize<SessionMarker>(File.ReadAllText(markerPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return Rejected(name, full, "Session marker is invalid: " + exception.GetType().Name + ".");
        }
        if (marker is null || !Guid.TryParseExact(marker.SessionId, "N", out var markerGuid) || markerGuid != sessionGuid)
            return Rejected(name, full, "Session marker ID does not match the directory name.");
        if (marker.Pid <= 0 || marker.CreatedUtc == default || string.IsNullOrWhiteSpace(marker.AppVersion))
            return Rejected(name, full, "Session marker fields are incomplete.");
        var age = now - marker.CreatedUtc;
        var size = CalculateSize(full);
        if (age < threshold)
            return new OrphanSessionInfo(marker.SessionId, full, marker.Pid, marker.CreatedUtc, marker.AppVersion, age, size, false, "Session is newer than the safety threshold.");
        if (_isMatchingBridgeProcess(marker.Pid))
            return new OrphanSessionInfo(marker.SessionId, full, marker.Pid, marker.CreatedUtc, marker.AppVersion, age, size, false, "Matching EplanBridge process is still active.");
        return new OrphanSessionInfo(marker.SessionId, full, marker.Pid, marker.CreatedUtc, marker.AppVersion, age, size, true, "Validated orphan session.");
    }

    private OrphanSessionInfo Rejected(string sessionId, string path, string reason) =>
        new(sessionId, path, 0, default, string.Empty, TimeSpan.Zero, CalculateSize(path), false, reason);

    private string NormalizeChild(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var parent = Directory.GetParent(full)?.FullName;
        if (!string.Equals(Path.TrimEndingDirectorySeparator(parent ?? string.Empty), _root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Cleanup safety gate rejected a path that is not a direct child of the Bridge root.");
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Cleanup safety gate rejected a path outside the Bridge root.");
        return full;
    }

    private static string NormalizeRoot(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool IsMatchingBridgeProcess(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return string.Equals(process.ProcessName, "EplanEdzManager.EplanBridge", StringComparison.OrdinalIgnoreCase)
                || string.Equals(process.ProcessName, "EplanBridge", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return true; }
    }

    private static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Unknown attributes must fail closed so cleanup never escalates AccessDenied into deletion.
            return true;
        }
    }

    private static bool ContainsReparsePoint(string root)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
                .Any(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static long CalculateSize(string root)
    {
        try { return Directory.Exists(root) ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length) : 0; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return 0; }
    }

    private sealed class SessionMarker
    {
        public string SessionId { get; set; } = string.Empty;
        public int Pid { get; set; }
        public DateTimeOffset CreatedUtc { get; set; }
        public string AppVersion { get; set; } = string.Empty;
    }
}

using System.Text.Json;

namespace EplanEdzManager.Application;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
    private readonly string _path;

    public SettingsStore(string? path = null)
    {
        _path = Path.GetFullPath(path ?? ApplicationPaths.DefaultSettingsPath);
    }

    public async Task<ApplicationSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path)) return Normalize(new ApplicationSettings());
        await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, FileOptions.Asynchronous);
        var settings = await JsonSerializer.DeserializeAsync<ApplicationSettings>(stream, SerializerOptions, cancellationToken).ConfigureAwait(false);
        return Normalize(settings ?? new ApplicationSettings());
    }

    public async Task SaveAsync(ApplicationSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings = Normalize(settings);
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = _path + ".tmp";
        await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 16 * 1024, FileOptions.Asynchronous))
        {
            await JsonSerializer.SerializeAsync(stream, settings, SerializerOptions, cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporaryPath, _path, overwrite: true);
    }

    private static ApplicationSettings Normalize(ApplicationSettings settings)
    {
        var previousSchemaVersion = settings.SettingsSchemaVersion;
        var legacyBackupFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "EplanEdzManager Backups");
        settings.SettingsSchemaVersion = 3;
        settings.DatabasePath = Path.GetFullPath(string.IsNullOrWhiteSpace(settings.DatabasePath) ? ApplicationPaths.DefaultDatabasePath : settings.DatabasePath);
        settings.DefaultExportFolder = Path.GetFullPath(string.IsNullOrWhiteSpace(settings.DefaultExportFolder)
            ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            : settings.DefaultExportFolder);
        settings.BackupFolder = Path.GetFullPath(
            string.IsNullOrWhiteSpace(settings.BackupFolder)
            || previousSchemaVersion < 3 && PathsEqual(settings.BackupFolder, legacyBackupFolder)
                ? ApplicationPaths.DefaultBackupDirectory
                : settings.BackupFolder);
        settings.DefaultCollection = string.IsNullOrWhiteSpace(settings.DefaultCollection) ? null : settings.DefaultCollection.Trim();
        settings.ThumbnailCacheSize = Math.Clamp(settings.ThumbnailCacheSize, 25, 500);
        settings.SearchPageSize = Math.Clamp(settings.SearchPageSize, 25, 1000);
        settings.WindowWidth = Math.Clamp(settings.WindowWidth, 1000, 3840);
        settings.WindowHeight = Math.Clamp(settings.WindowHeight, 650, 2160);
        settings.LogRetentionDays = Math.Clamp(settings.LogRetentionDays, 1, 365);
        settings.EplanPlatformBinDirectory = NormalizeOptionalDirectory(settings.EplanPlatformBinDirectory);
        settings.EplanVariantBinDirectory = NormalizeOptionalDirectory(settings.EplanVariantBinDirectory);
        settings.InitialEdzLibraryFolder = string.IsNullOrWhiteSpace(settings.InitialEdzLibraryFolder)
            ? null
            : Path.GetFullPath(settings.InitialEdzLibraryFolder);
        settings.LastBackupPath = string.IsNullOrWhiteSpace(settings.LastBackupPath)
            ? null
            : Path.GetFullPath(settings.LastBackupPath);
        return settings;
    }

    private static string NormalizeOptionalDirectory(string? path) =>
        string.IsNullOrWhiteSpace(path) ? string.Empty : Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}

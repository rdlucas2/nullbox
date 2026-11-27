using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Nullbox.Web.Backup;

/// <summary>What one backup run wrote.</summary>
public sealed record BackupResult(string Directory, IReadOnlyList<string> Databases, IReadOnlyList<string> Files, IReadOnlyList<string> Pruned);

/// <summary>
/// Snapshots the data directory into a timestamped folder: each SQLite database through SQLite's online backup
/// (consistent while the app is running), plus the Google token files and the login key ring. Everything written
/// is owner-only (0700 folders, 0600 files). Older snapshots beyond <c>keep</c> are deleted.
/// </summary>
public static class DataBackup
{
    /// <summary><c>dotnet Nullbox.Web.dll --backup [--dest DIR] [--keep N]</c>.</summary>
    public const string Flag = "--backup";

    public const string Prefix = "nullbox-";

    /// <summary>Folders under the data directory that are copied file by file.</summary>
    public static readonly IReadOnlyList<string> CopiedFolders = ["google", "keys"];

    private const string StampFormat = "yyyyMMdd'T'HHmmss'Z'";

    public static async Task<BackupResult> CreateAsync(string dataDirectory, string destinationRoot, int keep, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(keep, 1);
        dataDirectory = Path.GetFullPath(dataDirectory);
        destinationRoot = Path.GetFullPath(destinationRoot);
        if (!Directory.Exists(dataDirectory))
        {
            throw new DirectoryNotFoundException($"Data directory '{dataDirectory}' does not exist.");
        }

        if (IsInside(destinationRoot, dataDirectory))
        {
            throw new InvalidOperationException("Backups must go outside the data directory, or a lost disk takes both.");
        }

        CreatePrivateDirectory(destinationRoot);
        var name = Prefix + now.UtcDateTime.ToString(StampFormat, CultureInfo.InvariantCulture);
        var target = Path.Combine(destinationRoot, name);
        var partial = target + ".partial";
        if (Directory.Exists(target))
        {
            throw new IOException($"Backup '{target}' already exists.");
        }

        if (Directory.Exists(partial))
        {
            Directory.Delete(partial, recursive: true);
        }

        CreatePrivateDirectory(partial);

        var databases = new List<string>();
        foreach (var db in Directory.EnumerateFiles(dataDirectory, "*.db").Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = Path.GetFileName(db);
            var dest = Path.Combine(partial, file);
            await using (var source = new SqliteConnection(ConnectionString(db, SqliteOpenMode.ReadOnly)))
            await using (var copy = new SqliteConnection(ConnectionString(dest, SqliteOpenMode.ReadWriteCreate)))
            {
                await source.OpenAsync(cancellationToken);
                await copy.OpenAsync(cancellationToken);
                source.BackupDatabase(copy);
            }

            MakePrivateFile(dest);
            databases.Add(file);
        }

        var files = new List<string>();
        foreach (var folder in CopiedFolders)
        {
            var from = Path.Combine(dataDirectory, folder);
            if (!Directory.Exists(from))
            {
                continue;
            }

            foreach (var source in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(dataDirectory, source);
                var dest = Path.Combine(partial, relative);
                CreatePrivateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(source, dest);
                MakePrivateFile(dest);
                files.Add(relative.Replace(Path.DirectorySeparatorChar, '/'));
            }
        }

        Directory.Move(partial, target);
        var pruned = Prune(destinationRoot, keep);
        return new BackupResult(target, databases, files, pruned);
    }

    /// <summary>Deletes all but the newest <paramref name="keep"/> finished snapshots; returns the deleted names.</summary>
    public static IReadOnlyList<string> Prune(string destinationRoot, int keep)
    {
        var snapshots = Directory.EnumerateDirectories(destinationRoot, Prefix + "*")
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(n => TryParseStamp(n, out _))
            .OrderDescending(StringComparer.Ordinal)
            .ToList();

        var pruned = snapshots.Skip(keep).ToList();
        foreach (var name in pruned)
        {
            Directory.Delete(Path.Combine(destinationRoot, name), recursive: true);
        }

        return pruned;
    }

    /// <summary>
    /// Copies a snapshot back into the data directory, replacing the databases and token/key files it holds.
    /// Stop the app first: SQLite files are swapped in place and their stale WAL files removed.
    /// </summary>
    public static IReadOnlyList<string> Restore(string snapshotDirectory, string dataDirectory)
    {
        snapshotDirectory = Path.GetFullPath(snapshotDirectory);
        if (!TryParseStamp(Path.GetFileName(snapshotDirectory), out _) || !Directory.Exists(snapshotDirectory))
        {
            throw new DirectoryNotFoundException($"'{snapshotDirectory}' is not a nullbox backup folder.");
        }

        Directory.CreateDirectory(dataDirectory);
        var restored = new List<string>();
        foreach (var source in Directory.EnumerateFiles(snapshotDirectory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(snapshotDirectory, source);
            var dest = Path.Combine(dataDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            if (relative.EndsWith(".db", StringComparison.Ordinal) && !relative.Contains(Path.DirectorySeparatorChar))
            {
                File.Delete(dest + "-wal");
                File.Delete(dest + "-shm");
            }

            File.Copy(source, dest, overwrite: true);
            MakePrivateFile(dest);
            restored.Add(relative.Replace(Path.DirectorySeparatorChar, '/'));
        }

        return restored;
    }

    public static bool TryParseStamp(string? name, out DateTimeOffset stamp)
    {
        stamp = default;
        return name is not null
            && name.StartsWith(Prefix, StringComparison.Ordinal)
            && DateTimeOffset.TryParseExact(name[Prefix.Length..], StampFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out stamp);
    }

    /// <summary>Entry point for <see cref="Flag"/>; returns the process exit code.</summary>
    public static async Task<int> RunCliAsync(string[] args, Func<string, string?> env, TextWriter output, TextWriter error, DateTimeOffset now)
    {
        string? dest = env("NULLBOX_BACKUP_DIR");
        var keepText = env("NULLBOX_BACKUP_KEEP");
        var dataDir = env("NULLBOX_DATA_DIR") is { Length: > 0 } d ? d : "data";
        var restore = (string?)null;

        for (var i = 1; i < args.Length; i++)
        {
            var value = i + 1 < args.Length ? args[i + 1] : null;
            switch (args[i])
            {
                case "--dest" when value is not null:
                    dest = value;
                    i++;
                    break;
                case "--keep" when value is not null:
                    keepText = value;
                    i++;
                    break;
                case "--data" when value is not null:
                    dataDir = value;
                    i++;
                    break;
                case "--restore" when value is not null:
                    restore = value;
                    i++;
                    break;
                default:
                    await error.WriteLineAsync($"backup: unknown or incomplete argument '{args[i]}'. Usage: --backup [--dest DIR] [--keep N] [--data DIR] | --backup --restore SNAPSHOT [--data DIR]");
                    return 64;
            }
        }

        try
        {
            if (restore is not null)
            {
                var restored = Restore(restore, dataDir);
                await output.WriteLineAsync($"restored {restored.Count} files from {Path.GetFullPath(restore)} into {Path.GetFullPath(dataDir)}");
                return 0;
            }

            if (string.IsNullOrWhiteSpace(dest))
            {
                await error.WriteLineAsync("backup: set --dest or NULLBOX_BACKUP_DIR.");
                return 64;
            }

            var keep = 7;
            if (!string.IsNullOrWhiteSpace(keepText) && (!int.TryParse(keepText, NumberStyles.None, CultureInfo.InvariantCulture, out keep) || keep < 1))
            {
                await error.WriteLineAsync($"backup: keep must be a whole number of at least 1 (got '{keepText}').");
                return 64;
            }

            var result = await CreateAsync(dataDir, dest, keep, now);
            await output.WriteLineAsync($"backup {result.Directory}: {result.Databases.Count} databases ({string.Join(", ", result.Databases)}), {result.Files.Count} files; pruned {result.Pruned.Count}");
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException or InvalidOperationException)
        {
            await error.WriteLineAsync($"backup failed: {ex.Message}");
            return 1;
        }
    }

    private static string ConnectionString(string path, SqliteOpenMode mode)
        => new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false }.ToString();

    private static bool IsInside(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative == "." || (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative));
    }

    private static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            return;
        }

        Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void MakePrivateFile(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}

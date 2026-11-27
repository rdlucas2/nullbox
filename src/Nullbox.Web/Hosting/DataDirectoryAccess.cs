using System.Globalization;

namespace Nullbox.Web.Hosting;

/// <summary>Turns "permission denied" on the data volume into a start-up error that says how to fix it.</summary>
public static class DataDirectoryAccess
{
    /// <summary>Creates <paramref name="path"/> if needed and proves the process can write a file in it.</summary>
    /// <exception cref="DataDirectoryNotWritableException">The directory can't be created or written.</exception>
    public static DirectoryInfo EnsureWritable(string path)
    {
        try
        {
            var dir = Directory.CreateDirectory(path);
            var probe = Path.Combine(dir.FullName, $".write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return dir;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            throw new DataDirectoryNotWritableException(path, CurrentUserId(), ex);
        }
    }

    /// <summary>The effective user id on Linux (from /proc), or null elsewhere.</summary>
    public static int? CurrentUserId()
    {
        try
        {
            foreach (var line in File.ReadLines("/proc/self/status"))
            {
                if (line.StartsWith("Uid:", StringComparison.Ordinal))
                {
                    var fields = line.Split((char[])['\t', ' '], StringSplitOptions.RemoveEmptyEntries);
                    return int.Parse(fields[2], CultureInfo.InvariantCulture);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or IndexOutOfRangeException)
        {
            // Not Linux, or /proc unavailable: the message just leaves the id out.
        }

        return null;
    }
}

public sealed class DataDirectoryNotWritableException(string path, int? uid, Exception inner)
    : InvalidOperationException(Describe(path, uid), inner)
{
    public string Path { get; } = path;

    private static string Describe(string path, int? uid)
    {
        var who = uid is { } id ? $"user {id}" : "the app's user";
        var owner = uid is { } u ? $"{u}:{u}" : "<uid>:<gid>";
        return $"nullbox cannot write to {path} (running as {who}). "
            + $"The data volume must be writable by that user. With docker compose, the container fixes this itself "
            + $"unless it is started with --user or compose 'user:'. Otherwise, on the host run: sudo chown -R {owner} ./data";
    }
}

using System.Text.RegularExpressions;
using Nullbox.Core.Cleanup;

namespace Nullbox.Web.Cleanup;

/// <summary>
/// Cleanup rule files in <c>{data}/cleanup/</c>, in emailBlaster's format, so its <c>cleanup_rules.json</c> and
/// friends can be dropped in as they are. The id is the file name without <c>.json</c>. Files are read on every call,
/// so an edited file is picked up without a restart.
/// </summary>
public sealed partial class FileCleanupConfigStore(string directory) : ICleanupConfigStore
{
    public const string FolderName = "cleanup";

    /// <summary>Rule files are small; anything bigger is refused rather than read.</summary>
    public const long MaxFileBytes = 256 * 1024;

    public string Directory { get; } = directory;

    public IReadOnlyList<CleanupConfigEntry> List()
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            return [];
        }

        return [.. System.IO.Directory.EnumerateFiles(Directory, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(id => id is not null && IdPattern().IsMatch(id))
            .Order(StringComparer.Ordinal)
            .Select(id => Read(id!))];
    }

    public CleanupConfigEntry? Get(string id)
        => IsValidId(id) && File.Exists(PathFor(id)) ? Read(id) : null;

    public string? GetText(string id)
        => IsValidId(id) && File.Exists(PathFor(id)) && new FileInfo(PathFor(id)).Length <= MaxFileBytes ? File.ReadAllText(PathFor(id)) : null;

    public CleanupConfigEntry Save(string id, string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (!IsValidId(id))
        {
            throw new CleanupConfigException("A cleanup file name is letters, digits, - and _ (at most 100), starting with a letter or digit.");
        }

        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxFileBytes)
        {
            throw new CleanupConfigException($"Cleanup files can be at most {MaxFileBytes / 1024} KB.");
        }

        var config = CleanupConfig.Parse(json);
        System.IO.Directory.CreateDirectory(Directory);
        var path = PathFor(id);
        var temp = path + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, path, overwrite: true);
        return new CleanupConfigEntry(id, config, null);
    }

    public static bool IsValidId(string? id) => id is not null && IdPattern().IsMatch(id);

    private string PathFor(string id) => Path.Combine(Directory, id + ".json");

    private CleanupConfigEntry Read(string id)
    {
        var path = PathFor(id);
        try
        {
            if (new FileInfo(path).Length > MaxFileBytes)
            {
                return new CleanupConfigEntry(id, null, $"{id}.json is larger than {MaxFileBytes / 1024} KB.");
            }

            return new CleanupConfigEntry(id, CleanupConfig.Parse(File.ReadAllText(path)), null);
        }
        catch (CleanupConfigException ex)
        {
            return new CleanupConfigEntry(id, null, ex.Message);
        }
        catch (IOException ex)
        {
            return new CleanupConfigEntry(id, null, $"{id}.json could not be read ({ex.GetType().Name}).");
        }
        catch (UnauthorizedAccessException)
        {
            return new CleanupConfigEntry(id, null, $"{id}.json is not readable by the app user.");
        }
    }

    // A plain file name: no separators, no "..", no leading dot.
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]{0,99}$")]
    private static partial Regex IdPattern();
}

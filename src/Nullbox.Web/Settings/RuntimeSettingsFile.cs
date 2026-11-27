using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Nullbox.Web.Settings;

public enum RuntimeSettingKind
{
    Flag,

    Number,

    Cron,
}

/// <summary>One key the settings pages may write, and the values it takes.</summary>
public sealed record RuntimeSettingSpec(string Key, RuntimeSettingKind Kind, long Min = 0, long Max = long.MaxValue);

/// <summary>
/// <c>{data}/settings.json</c>: the values saved from the Settings and Jobs pages. It is read as the last
/// configuration source when the app starts, so it wins over appsettings.json and environment variables. Only the
/// keys in <see cref="Specs"/> are written. Most settings are read once at start-up, so a saved value applies after a
/// restart; the poll schedule is also applied to the running scheduler when it is saved.
/// </summary>
public sealed partial class RuntimeSettingsFile(string path)
{
    public const string FileName = "settings.json";

    public const string AllowDelete = "Nullbox:Gmail:AllowDelete";
    public const string AllowPermanentDelete = "Nullbox:Gmail:AllowPermanentDelete";
    public const string AllowFilterSync = "Nullbox:Gmail:AllowFilterSync";
    public const string JevMaxInputTokensPerDay = "Nullbox:Jev:MaxInputTokensPerDay";
    public const string JevMaxRequestsPerHour = "Nullbox:Jev:MaxRequestsPerHour";
    public const string AuditRetentionDays = "Nullbox:Audit:RetentionDays";
    public const string InboxPollCron = "Nullbox:Jobs:InboxPollCron";

    public static IReadOnlyDictionary<string, RuntimeSettingSpec> Specs { get; } = new RuntimeSettingSpec[]
    {
        new(AllowDelete, RuntimeSettingKind.Flag),
        new(AllowPermanentDelete, RuntimeSettingKind.Flag),
        new(AllowFilterSync, RuntimeSettingKind.Flag),
        new(JevMaxInputTokensPerDay, RuntimeSettingKind.Number, 0, 1_000_000_000),
        new(JevMaxRequestsPerHour, RuntimeSettingKind.Number, 1, 1_000_000),
        new(AuditRetentionDays, RuntimeSettingKind.Number, 0, 3650),
        new(InboxPollCron, RuntimeSettingKind.Cron),
    }.ToDictionary(s => s.Key, StringComparer.Ordinal);

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly Lock _gate = new();

    public string Path { get; } = path;

    public static string In(string dataDirectory) => System.IO.Path.Combine(System.IO.Path.GetFullPath(dataDirectory), FileName);

    /// <summary>The saved values of the known keys, as configuration strings (<c>true</c>, <c>90</c>).</summary>
    public IReadOnlyDictionary<string, string> Load()
    {
        lock (_gate)
        {
            return Read() is { } root ? Flatten(root) : new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Saves or (with a null value) clears each key, keeping the others. Every value is checked first; on any error
    /// nothing is written.
    /// </summary>
    /// <exception cref="ArgumentException">An unknown key or a value it can't take; the message says which.</exception>
    public void Save(IReadOnlyDictionary<string, string?> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        foreach (var (key, value) in changes)
        {
            if (value is not null && Check(key, value) is { } error)
            {
                throw new ArgumentException(error, nameof(changes));
            }

            if (!Specs.ContainsKey(key))
            {
                throw new ArgumentException($"{key} can't be set here.", nameof(changes));
            }
        }

        lock (_gate)
        {
            var root = Read() ?? new JsonObject();
            foreach (var (key, value) in changes)
            {
                Set(root, key, value is null ? null : ToNode(Specs[key], value));
            }

            var directory = System.IO.Path.GetDirectoryName(Path)!;
            Directory.CreateDirectory(directory);
            var temp = Path + ".tmp";
            File.WriteAllText(temp, root.ToJsonString(WriteOptions));
            File.Move(temp, Path, overwrite: true);
        }
    }

    /// <summary>Why <paramref name="value"/> can't be saved for <paramref name="key"/>, or null when it can.</summary>
    public static string? Check(string key, string value)
    {
        if (!Specs.TryGetValue(key, out var spec))
        {
            return $"{key} can't be set here.";
        }

        return spec.Kind switch
        {
            RuntimeSettingKind.Flag => bool.TryParse(value, out _) ? null : $"{key} must be true or false.",
            RuntimeSettingKind.Number => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= spec.Min && n <= spec.Max
                ? null
                : $"{key} must be a whole number from {spec.Min:N0} to {spec.Max:N0}.",
            _ => CronPattern().IsMatch(value.Trim()) ? null : $"{key} must be a cron expression with 5 or 6 fields, e.g. */5 * * * *.",
        };
    }

    private JsonObject? Read()
    {
        if (!File.Exists(Path))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(File.ReadAllText(Path)) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Dictionary<string, string> Flatten(JsonObject root)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in Specs.Keys)
        {
            JsonNode? node = root;
            foreach (var part in key.Split(':'))
            {
                node = node is JsonObject obj && obj.TryGetPropertyValue(part, out var child) ? child : null;
            }

            if (node is JsonValue value)
            {
                values[key] = value.GetValueKind() switch
                {
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    JsonValueKind.Number => value.GetValue<long>().ToString(CultureInfo.InvariantCulture),
                    _ => value.ToString(),
                };
            }
        }

        return values;
    }

    private static JsonNode ToNode(RuntimeSettingSpec spec, string value) => spec.Kind switch
    {
        RuntimeSettingKind.Flag => JsonValue.Create(bool.Parse(value)),
        RuntimeSettingKind.Number => JsonValue.Create(long.Parse(value, CultureInfo.InvariantCulture)),
        _ => JsonValue.Create(value.Trim()),
    };

    private static void Set(JsonObject root, string key, JsonNode? value)
    {
        var parts = key.Split(':');
        var parent = root;
        foreach (var part in parts[..^1])
        {
            if (parent[part] is not JsonObject child)
            {
                if (value is null)
                {
                    return;
                }

                child = new JsonObject();
                parent[part] = child;
            }

            parent = child;
        }

        if (value is null)
        {
            parent.Remove(parts[^1]);
        }
        else
        {
            parent[parts[^1]] = value;
        }
    }

    // Five or six fields of digits, names, * , - / ? L W #.
    [GeneratedRegex(@"^[0-9A-Za-z*,/?#\-]+(\s+[0-9A-Za-z*,/?#\-]+){4,5}$")]
    private static partial Regex CronPattern();
}

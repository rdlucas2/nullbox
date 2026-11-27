using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nullbox.Core.Cleanup;

/// <summary>
/// A cleanup rule file in emailBlaster's <c>cleanup_rules.json</c> format: a <c>label</c> for archived mail, a
/// <c>keep</c> section excluded from every rule, and <c>rules</c>, each a Gmail search. Cleanup only marks read and
/// archives; it never deletes.
/// </summary>
public sealed record CleanupConfig
{
    /// <summary>Label added to archived mail; null means <see cref="DefaultLabel"/> for the day it runs.</summary>
    public string? Label { get; init; }

    public CleanupKeep Keep { get; init; } = new();

    public IReadOnlyList<CleanupRule> Rules { get; init; } = [];

    /// <summary><c>cleanup_yyyyMMdd</c> for the UTC date (cleanup.py uses the local date).</summary>
    public static string DefaultLabel(DateTimeOffset now)
        => "cleanup_" + now.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    /// <summary>
    /// Reads a rule file. Missing keys take cleanup.py's defaults (<c>rule.get("archive", True)</c> and so on), and
    /// present values follow Python truthiness, so the same file behaves the same in both tools. Unknown keys are ignored.
    /// </summary>
    /// <exception cref="CleanupConfigException">The file is not valid; the message names the key, never a value.</exception>
    public static CleanupConfig Parse(string json)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException)
        {
            throw new CleanupConfigException("The cleanup file is not valid JSON.");
        }

        if (root is not JsonObject config)
        {
            throw new CleanupConfigException("The cleanup file must be a JSON object with \"rules\".");
        }

        if (config["rules"] is not JsonArray rules || rules.Count == 0)
        {
            throw new CleanupConfigException("The cleanup file needs a non-empty \"rules\" list.");
        }

        var keep = config["keep"] as JsonObject;
        return new CleanupConfig
        {
            Label = Text(config["label"]) is { Length: > 0 } label ? label : null,
            Keep = new CleanupKeep
            {
                Starred = Flag(keep, "starred", true),
                Important = Flag(keep, "important", true),
                Senders = Senders(keep?["senders"]),
            },
            Rules = [.. rules.Select(ParseRule)],
        };
    }

    private static CleanupRule ParseRule(JsonNode? node, int index)
    {
        if (node is not JsonObject rule)
        {
            throw new CleanupConfigException($"rules[{index}] must be an object.");
        }

        var name = Text(rule["name"]);
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new CleanupConfigException($"rules[{index}].name is missing.");
        }

        var query = Text(rule["query"]);
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new CleanupConfigException($"rules[{index}].query is missing.");
        }

        return new CleanupRule
        {
            Name = name,
            Query = query,
            UnreadOnly = Flag(rule, "unread_only", true),
            MarkRead = Flag(rule, "mark_read", true),
            Archive = Flag(rule, "archive", true),
        };
    }

    private static IReadOnlyList<string> Senders(JsonNode? node) => node switch
    {
        null => [],
        JsonArray array => [.. array.Select(s => Text(s) ?? string.Empty).Where(s => s.Length > 0)],
        _ => throw new CleanupConfigException("keep.senders must be a list of senders or domains."),
    };

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary>Python's <c>obj.get(key, fallback)</c> used as a condition: missing gives the fallback, present values are truthy or not.</summary>
    private static bool Flag(JsonObject? obj, string key, bool fallback)
        => obj is null || !obj.TryGetPropertyValue(key, out var node) ? fallback : Truthy(node);

    private static bool Truthy(JsonNode? node) => node switch
    {
        null => false,
        JsonArray array => array.Count > 0,
        JsonObject obj => obj.Count > 0,
        JsonValue value when value.TryGetValue<bool>(out var b) => b,
        JsonValue value when value.TryGetValue<double>(out var d) => d != 0,
        JsonValue value when value.TryGetValue<string>(out var s) => s.Length > 0,
        _ => true,
    };
}

/// <summary>Mail no rule may touch: starred, important, and these senders (any Gmail <c>from:</c> term).</summary>
public sealed record CleanupKeep
{
    public bool Starred { get; init; } = true;

    public bool Important { get; init; } = true;

    public IReadOnlyList<string> Senders { get; init; } = [];
}

/// <summary>One cleanup rule: a Gmail search, and whether matches are marked read and archived (both by default).</summary>
public sealed record CleanupRule
{
    public required string Name { get; init; }

    /// <summary>The rule's own search, e.g. <c>category:promotions older_than:30d</c>; see <see cref="CleanupQuery"/>.</summary>
    public required string Query { get; init; }

    /// <summary>Only unread mail (adds <c>is:unread</c>). On by default.</summary>
    public bool UnreadOnly { get; init; } = true;

    public bool MarkRead { get; init; } = true;

    public bool Archive { get; init; } = true;

    /// <summary>What the rule does, as cleanup.py prints it: <c>mark read + archive</c>, <c>archive</c>, or <c>nothing</c>.</summary>
    public string Describe()
    {
        var actions = new List<string>(2);
        if (MarkRead)
        {
            actions.Add("mark read");
        }

        if (Archive)
        {
            actions.Add("archive");
        }

        return actions.Count == 0 ? "nothing" : string.Join(" + ", actions);
    }
}

/// <summary>emailBlaster's <c>build_query</c>.</summary>
public static class CleanupQuery
{
    /// <summary>
    /// <c>in:inbox</c>, then <c>is:unread</c> unless the rule includes read mail, then the rule's query in
    /// parentheses, then the keep exclusions: <c>-is:starred</c>, <c>-is:important</c>, <c>-from:(a OR b)</c>.
    /// </summary>
    public static string Build(CleanupRule rule, CleanupKeep keep)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(keep);

        var parts = new List<string> { "in:inbox" };
        if (rule.UnreadOnly)
        {
            parts.Add("is:unread");
        }

        parts.Add($"({rule.Query})");
        if (keep.Starred)
        {
            parts.Add("-is:starred");
        }

        if (keep.Important)
        {
            parts.Add("-is:important");
        }

        if (keep.Senders.Count > 0)
        {
            parts.Add("-from:(" + string.Join(" OR ", keep.Senders) + ")");
        }

        return string.Join(' ', parts);
    }
}

public sealed class CleanupConfigException(string message) : FormatException(message);

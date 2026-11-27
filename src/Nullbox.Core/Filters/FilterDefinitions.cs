using System.Text.Json;
using System.Text.Json.Nodes;
using Nullbox.Core.Gmail;

namespace Nullbox.Core.Filters;

/// <summary>
/// One rule from emailBlaster's <c>filters.json</c>: a Gmail filter that adds <see cref="Label"/> to matching new
/// mail and skips the inbox, and with <see cref="MarkRead"/> also marks it read.
/// </summary>
public sealed record FilterDefinition(string Label, string Query, bool MarkRead)
{
    /// <summary>INBOX, plus UNREAD when the rule marks mail read, as create_filters.py builds it.</summary>
    public IReadOnlyList<string> RemoveLabelIds => MarkRead ? [GmailLabels.Inbox, GmailLabels.Unread] : [GmailLabels.Inbox];

    /// <summary>create_filters.py's summary line, e.g. <c>from:(a.com) -&gt; Promos, skip inbox, mark read</c>.</summary>
    public string Summary => $"{Query} -> {Label}, skip inbox" + (MarkRead ? ", mark read" : string.Empty);
}

public static class FilterDefinitions
{
    /// <summary>
    /// Reads <c>{"filters": [{"label", "query", "mark_read"}]}</c>. <c>mark_read</c> is off unless present and truthy,
    /// as <c>rule.get("mark_read")</c>; other keys such as <c>_comment</c> are ignored.
    /// </summary>
    /// <exception cref="FilterDefinitionsException">The file is not valid; the message names the key, never a value.</exception>
    public static IReadOnlyList<FilterDefinition> Parse(string json)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException)
        {
            throw new FilterDefinitionsException("filters.json is not valid JSON.");
        }

        if (root is not JsonObject obj || obj["filters"] is not JsonArray filters)
        {
            throw new FilterDefinitionsException("filters.json needs a \"filters\" list.");
        }

        return [.. filters.Select((node, i) =>
        {
            if (node is not JsonObject rule)
            {
                throw new FilterDefinitionsException($"filters[{i}] must be an object.");
            }

            var label = Text(rule["label"]);
            if (string.IsNullOrWhiteSpace(label))
            {
                throw new FilterDefinitionsException($"filters[{i}].label is missing.");
            }

            var query = Text(rule["query"]);
            if (string.IsNullOrWhiteSpace(query))
            {
                throw new FilterDefinitionsException($"filters[{i}].query is missing.");
            }

            return new FilterDefinition(label, query, Truthy(rule["mark_read"]));
        })];
    }

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

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

public enum FilterSyncStatus
{
    Created,
    WouldCreate,

    /// <summary>A filter with the same query and the same label changes is already in place.</summary>
    Exists,
}

public sealed record FilterSyncItem(string Label, string Query, bool MarkRead, FilterSyncStatus Status);

/// <summary>
/// What a filter sync did (or, for a dry run, would do). <see cref="BlockedReason"/> is set when it was refused before
/// touching Gmail.
/// </summary>
public sealed record FilterSyncResult(bool DryRun, IReadOnlyList<string> LabelsCreated, IReadOnlyList<FilterSyncItem> Filters, string? BlockedReason = null)
{
    public int Created => Filters.Count(f => f.Status is FilterSyncStatus.Created or FilterSyncStatus.WouldCreate);

    public int Existing => Filters.Count(f => f.Status == FilterSyncStatus.Exists);

    public static FilterSyncResult Blocked(bool dryRun, string reason) => new(dryRun, [], [], reason);
}

public sealed class FilterDefinitionsException(string message) : FormatException(message);

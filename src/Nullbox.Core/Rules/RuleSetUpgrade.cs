using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nullbox.Core.Rules;

/// <summary>
/// Converts rule-set JSON written before the Archive | Delete switch (#36), when <c>archive</c> and <c>trash</c> were
/// actions. Both become <c>"remove": "archive"</c>: a rule that trashed mail goes back to archive-only until its switch
/// is flipped to Delete on purpose. Nothing is ever upgraded to delete.
/// </summary>
public static class RuleSetUpgrade
{
    private static readonly string[] LegacyActions = ["archive", "trash"];

    /// <summary>The converted JSON, or null when <paramref name="json"/> has no legacy actions (or is not JSON).</summary>
    public static string? UpgradeLegacyActions(string json)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException)
        {
            return null;
        }

        if (root?["decisions"] is not JsonArray decisions)
        {
            return null;
        }

        var changed = false;
        foreach (var decision in decisions.OfType<JsonObject>())
        {
            if (decision["actions"] is not JsonArray actions)
            {
                continue;
            }

            var legacy = actions.Where(a => a is JsonValue v && v.TryGetValue<string>(out var s) && LegacyActions.Contains(s.Trim())).ToList();
            if (legacy.Count == 0)
            {
                continue;
            }

            foreach (var action in legacy)
            {
                actions.Remove(action);
            }

            if (actions.Count == 0)
            {
                decision.Remove("actions");
            }

            decision["remove"] ??= "archive";
            changed = true;
        }

        return changed ? root.ToJsonString(RuleJson.Options) : null;
    }
}

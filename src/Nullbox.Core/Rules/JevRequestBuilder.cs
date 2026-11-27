using System.Text.Json.Nodes;
using Nullbox.Core.Gmail;
using Nullbox.Core.Jev;

namespace Nullbox.Core.Rules;

/// <summary>Turns a rule set and one email into the Jev request: only the fields the state template names.</summary>
public static class JevRequestBuilder
{
    public const string Ellipsis = "…";

    public static JevRequest Build(RuleSetDocument ruleSet, GmailMessage message, DateTimeOffset now)
        => new()
        {
            Model = ruleSet.Model,
            State = BuildState(ruleSet.State, message, now),
            Questions = ruleSet.Questions,
        };

    public static JsonObject BuildState(StateTemplate template, GmailMessage message, DateTimeOffset now)
    {
        var state = new JsonObject();
        foreach (var field in template.Fields)
        {
            JsonNode? value = field switch
            {
                StateFields.From => message.From,
                StateFields.Subject => message.Subject,
                StateFields.Snippet => message.Snippet,
                StateFields.Body => Truncate(message.Body, template.BodyMaxChars),
                StateFields.Labels => new JsonArray([.. message.LabelIds.Select(l => (JsonNode?)JsonValue.Create(l))]),
                StateFields.AgeDays => message.InternalDate is { } sent ? (int)Math.Max(0, Math.Floor((now - sent).TotalDays)) : null,
                StateFields.HasUnsubscribe => message.HasUnsubscribe,
                StateFields.Date => message.Date,
                _ => null,
            };
            state[field] = value;
        }

        return state;
    }

    public static string? Truncate(string? text, int maxChars)
        => text is null || text.Length <= maxChars ? text : text[..Math.Max(0, maxChars - Ellipsis.Length)] + Ellipsis;
}

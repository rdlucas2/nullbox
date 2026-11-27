using System.Text.Json;
using Json.Schema;
using Nullbox.Core.Rules;

namespace Nullbox.Core.SavedRules;

/// <summary>Reads and writes saved-rules JSON (snake_case, like rule sets) against <c>savedrules.schema.json</c>.</summary>
public static class SavedRuleJson
{
    public const string SchemaUrl = "/schemas/savedrules.schema.json";

    /// <summary>The published schema document.</summary>
    public static string SchemaText { get; } = RuleSetSchema.ReadResource("Nullbox.Core.SavedRules.savedrules.schema.json");

    private static readonly HashSet<string> ApplicatorKeywords = ["properties", "additionalProperties", "items", "$ref"];

    private static readonly Lazy<JsonSchema> Schema = new(() => JsonSchema.FromText(SchemaText));

    public static string Serialize(SavedRulesDocument document)
        => JsonSerializer.Serialize(document with { Schema = SchemaUrl }, RuleJson.Options);

    public static string Serialize(SavedRule rule) => JsonSerializer.Serialize(rule, RuleJson.Options);

    /// <summary>Checks a whole document (an export or a hand-written file); returns it only when there are no errors.</summary>
    public static SavedRulesDocument? Parse(string json, out IReadOnlyList<RuleSetError> errors)
    {
        errors = CheckSchema(json, null);
        if (errors.Count > 0)
        {
            return null;
        }

        var document = Deserialize<SavedRulesDocument>(json, out errors);
        if (document is null)
        {
            return null;
        }

        errors = SavedRuleValidator.Validate(document);
        return errors.Count == 0 ? document : null;
    }

    /// <summary>Checks one rule as the editor's JSON view holds it.</summary>
    public static SavedRule? ParseRule(string json, out IReadOnlyList<RuleSetError> errors)
    {
        errors = CheckSchema(json, "rule");
        if (errors.Count > 0)
        {
            return null;
        }

        var rule = Deserialize<SavedRule>(json, out errors);
        if (rule is null)
        {
            return null;
        }

        errors = SavedRuleValidator.Validate(rule);
        return errors.Count == 0 ? rule : null;
    }

    private static T? Deserialize<T>(string json, out IReadOnlyList<RuleSetError> errors)
        where T : class
    {
        try
        {
            var value = JsonSerializer.Deserialize<T>(json, RuleJson.Options);
            errors = value is null ? [new RuleSetError(string.Empty, "Expected an object.")] : [];
            return value;
        }
        catch (JsonException ex)
        {
            errors = [new RuleSetError(ex.Path?.TrimStart('$').Replace('.', '/') ?? string.Empty, ex.Message)];
            return null;
        }
    }

    private static IReadOnlyList<RuleSetError> CheckSchema(string json, string? definition)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException ex)
        {
            return [new RuleSetError(string.Empty, $"Not valid JSON: {ex.Message}")];
        }

        using (document)
        {
            // A single rule is checked by wrapping it in a document, so both use the one published schema.
            var target = definition is null
                ? document.RootElement
                : JsonDocument.Parse($"{{\"rules\":[{document.RootElement.GetRawText()}]}}").RootElement;
            var result = Schema.Value.Evaluate(target, new EvaluationOptions { OutputFormat = OutputFormat.List });
            if (result.IsValid)
            {
                return [];
            }

            var prefix = definition is null ? null : "/rules/0";
            var errors = new List<RuleSetError>();
            foreach (var detail in result.Details ?? [])
            {
                foreach (var (keyword, message) in detail.Errors ?? new Dictionary<string, string>())
                {
                    // Applicators only say "a child failed"; the child's own error is reported separately.
                    if (ApplicatorKeywords.Contains(keyword))
                    {
                        continue;
                    }

                    var at = detail.InstanceLocation.ToString();
                    if (prefix is not null && at.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        at = at[prefix.Length..];
                    }

                    var text = keyword == "anyOf" ? "A saved rule needs a sender (from), a recipient (to), or both." : message.Replace("\\u0022", "\"", StringComparison.Ordinal);
                    errors.Add(new RuleSetError(at, text));
                }
            }

            return errors.Count > 0 ? errors.Distinct().ToList() : [new RuleSetError(string.Empty, "Does not match the saved rules schema.")];
        }
    }
}

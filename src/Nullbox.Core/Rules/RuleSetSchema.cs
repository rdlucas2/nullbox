using System.Text.Json;
using Json.Schema;

namespace Nullbox.Core.Rules;

/// <summary>The published JSON Schema for rule sets (<c>ruleset.schema.json</c>), shared with the UI editor.</summary>
public static class RuleSetSchema
{
    /// <summary>The schema document itself.</summary>
    public static string Text { get; } = ReadResource("Nullbox.Core.Rules.ruleset.schema.json");

    private static readonly HashSet<string> ApplicatorKeywords =
        ["properties", "additionalProperties", "items", "prefixItems", "allOf", "anyOf", "oneOf", "if", "then", "else", "$ref"];

    private static readonly Lazy<JsonSchema> Schema = new(() => JsonSchema.FromText(Text));

    /// <summary>Structural errors in <paramref name="json"/>; empty when it matches the schema.</summary>
    public static IReadOnlyList<RuleSetError> Validate(string json)
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
            var result = Schema.Value.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
            if (result.IsValid)
            {
                return [];
            }

            var errors = new List<RuleSetError>();
            foreach (var detail in result.Details ?? [])
            {
                // Failed "if" tests are how the per-type dispatch works, not mistakes in the document.
                if (detail.EvaluationPath.ToString().Contains("/if", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (var (keyword, message) in detail.Errors ?? new Dictionary<string, string>())
                {
                    // Applicators only say "a child failed"; the child's own error is reported separately.
                    if (!ApplicatorKeywords.Contains(keyword))
                    {
                        errors.Add(new RuleSetError(detail.InstanceLocation.ToString(), message.Replace("\\u0022", "\"", StringComparison.Ordinal)));
                    }
                }
            }

            return errors.Count > 0 ? errors.Distinct().ToList() : [new RuleSetError(string.Empty, "Does not match the rule set schema.")];
        }
    }

    internal static string ReadResource(string name)
    {
        using var stream = typeof(RuleSetSchema).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Missing embedded resource {name}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

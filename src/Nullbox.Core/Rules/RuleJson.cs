using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nullbox.Core.Jev;

namespace Nullbox.Core.Rules;

/// <summary>Reads and writes rule-set JSON (snake_case, like Jev's wire format).</summary>
public static class RuleJson
{
    public static JsonSerializerOptions Options { get; } = Create(indented: true);

    private static JsonSerializerOptions Compact { get; } = Create(indented: false);

    public static string Serialize(RuleSetDocument document) => JsonSerializer.Serialize(document, Options);

    /// <summary>Compact form without <c>$schema</c>, used to hash and compare versions.</summary>
    public static string Canonical(RuleSetDocument document) => JsonSerializer.Serialize(document with { Schema = null }, Compact);

    public static string Hash(RuleSetDocument document)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(document))));

    /// <summary>
    /// Checks <paramref name="json"/> against the JSON Schema, then deserializes and checks references between
    /// decisions and questions. Returns the document only when there are no errors.
    /// </summary>
    public static RuleSetDocument? Parse(string json, out IReadOnlyList<RuleSetError> errors)
    {
        errors = RuleSetSchema.Validate(json);
        if (errors.Count > 0)
        {
            return null;
        }

        RuleSetDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<RuleSetDocument>(json, Options);
        }
        catch (JsonException ex)
        {
            errors = [new RuleSetError(ex.Path?.TrimStart('$').Replace('.', '/') ?? string.Empty, ex.Message)];
            return null;
        }

        if (document is null)
        {
            errors = [new RuleSetError(string.Empty, "Expected a rule set object.")];
            return null;
        }

        errors = RuleSetValidator.Validate(document);
        return errors.Count == 0 ? document : null;
    }

    public static RuleSetDocument ParseOrThrow(string json)
        => Parse(json, out var errors) ?? throw new RuleSetValidationException(errors);

    private static JsonSerializerOptions Create(bool indented)
    {
        var options = new JsonSerializerOptions(JevJson.Options)
        {
            WriteIndented = indented,
            AllowOutOfOrderMetadataProperties = true, // "type" need not be the first property of a question
            RespectRequiredConstructorParameters = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nullbox.Core.Jev;
using Nullbox.Core.Rules;

namespace Nullbox.Core.Pipeline;

/// <summary>What the audit log keeps of the state sent to Jev: a hash of all of it, and a copy with the body cut short.</summary>
public static class AuditInputs
{
    /// <summary>Lower-case hex SHA-256 of the state's compact JSON.</summary>
    public static string Hash(JsonNode state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(state.ToJsonString())));
    }

    /// <summary>
    /// A copy of <paramref name="state"/> for storage: a string <c>body</c> is cut to <paramref name="bodyChars"/>
    /// characters (removed when that is 0 or less); everything else is kept as sent.
    /// </summary>
    public static JsonNode Redact(JsonNode state, int bodyChars)
    {
        ArgumentNullException.ThrowIfNull(state);
        var copy = state.DeepClone();
        if (copy is JsonObject obj && obj[StateFields.Body] is JsonValue body && body.TryGetValue<string>(out var text))
        {
            if (bodyChars <= 0)
            {
                obj.Remove(StateFields.Body);
            }
            else
            {
                obj[StateFields.Body] = JevRequestBuilder.Truncate(text, bodyChars);
            }
        }

        return copy;
    }

    /// <summary>Jev's answers as stored in <see cref="DecisionRecord.AnswersJson"/>, or null if they can't be read.</summary>
    public static IReadOnlyDictionary<string, JevAnswer>? ReadAnswers(string answersJson)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, JevAnswer>>(answersJson, JevJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// One number for "how sure was Jev": the lowest confidence across a result's answers. A choice or score answer counts
/// its <c>confidence</c>; a noul counts how far it leans either way, <c>max(p, 1 - p)</c>. Unknown answer types are
/// ignored.
/// </summary>
public static class AnswerConfidence
{
    public static double? Lowest(IReadOnlyDictionary<string, JevAnswer> answers)
    {
        ArgumentNullException.ThrowIfNull(answers);
        double? lowest = null;
        foreach (var answer in answers.Values)
        {
            double? value = answer switch
            {
                JevChoiceAnswer choice => choice.Confidence,
                JevScoreAnswer score => score.Confidence,
                JevNoulAnswer noul => Math.Max(noul.Noul, 1 - noul.Noul),
                _ => null,
            };
            if (value is { } v && (lowest is null || v < lowest))
            {
                lowest = v;
            }
        }

        return lowest;
    }
}

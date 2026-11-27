using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Nullbox.Core.Jev;

/// <summary>
/// One Jev (TypeSafe System One) request: the email as <see cref="State"/> plus named questions.
/// Shapes follow docs/jev-api.md exactly so a rule set's questions are real Jev JSON.
/// </summary>
public sealed record JevRequest
{
    public const string DefaultModel = "jev-latest";

    public string Model { get; init; } = DefaultModel;

    /// <summary>The email (string, object or array). Only fields named by the rule set's state template.</summary>
    public required JsonNode State { get; init; }

    public required IReadOnlyDictionary<string, JevQuestion> Questions { get; init; }
}

/// <summary>A Jev question. Instructions and criteria values may be strings or structured JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(JevChoiceQuestion), "choice")]
[JsonDerivedType(typeof(JevScoreQuestion), "score")]
[JsonDerivedType(typeof(JevNoulQuestion), "noul")]
public abstract record JevQuestion
{
    public JsonNode? Instructions { get; init; }
}

/// <summary>Pick one option. <see cref="Criteria"/> maps option name to its description.</summary>
public sealed record JevChoiceQuestion : JevQuestion
{
    public required IReadOnlyDictionary<string, JsonNode?> Criteria { get; init; }
}

/// <summary>Rate on ordered levels. <see cref="Criteria"/> lists level descriptions, lowest first.</summary>
public sealed record JevScoreQuestion : JevQuestion
{
    public required IReadOnlyList<JsonNode?> Criteria { get; init; }
}

/// <summary>Yes/no statement; the answer is P(yes).</summary>
public sealed record JevNoulQuestion : JevQuestion
{
    public JevNoulCriteria? Criteria { get; init; }
}

public sealed record JevNoulCriteria
{
    public JsonNode? True { get; init; }

    public JsonNode? False { get; init; }
}

/// <summary>Jev's response, plus what nullbox needs to audit it.</summary>
public sealed record JevResult
{
    /// <summary>The versioned model that answered (e.g. <c>jev-1.14.0</c>), even when <c>jev-latest</c> was requested.</summary>
    public required string Model { get; init; }

    public required IReadOnlyDictionary<string, JevAnswer> Answers { get; init; }

    public JevUsage Usage { get; init; } = new();

    /// <summary>True when this result came from the local cache instead of a paid call.</summary>
    [JsonIgnore]
    public bool FromCache { get; init; }

    public T? Answer<T>(string question)
        where T : JevAnswer
        => Answers.TryGetValue(question, out var answer) ? answer as T : null;
}

public sealed record JevUsage
{
    public long InputTokens { get; init; }

    public long OutputTokens { get; init; }
}

/// <summary>An answer to one question. Unknown answer types parse as <see cref="JevUnknownAnswer"/>.</summary>
[JsonConverter(typeof(JevAnswerConverter))]
public abstract record JevAnswer
{
    [JsonIgnore]
    public abstract string Type { get; }
}

public sealed record JevChoiceAnswer : JevAnswer
{
    public override string Type => "choice";

    public required string Choice { get; init; }

    public double Confidence { get; init; }

    public IReadOnlyDictionary<string, double> Probabilities { get; init; } = new Dictionary<string, double>();
}

public sealed record JevScoreAnswer : JevAnswer
{
    public override string Type => "score";

    public double Score { get; init; }

    public double Confidence { get; init; }

    public IReadOnlyDictionary<string, double> Probabilities { get; init; } = new Dictionary<string, double>();

    public IReadOnlyDictionary<string, JsonNode?> Legend { get; init; } = new Dictionary<string, JsonNode?>();
}

public sealed record JevNoulAnswer : JevAnswer
{
    public override string Type => "noul";

    /// <summary>Probability (0 to 1) that the statement is true.</summary>
    public double Noul { get; init; }
}

/// <summary>An answer type this client does not know yet; kept raw so parsing never fails on it.</summary>
public sealed record JevUnknownAnswer(string RawType, JsonElement Raw) : JevAnswer
{
    public override string Type => RawType;
}

/// <summary>Reads answers by their <c>type</c> field and writes them back in the same shape.</summary>
public sealed class JevAnswerConverter : JsonConverter<JevAnswer>
{
    public override JevAnswer? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        var type = root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;

        return type switch
        {
            "choice" => root.Deserialize<JevChoiceAnswer>(options),
            "score" => root.Deserialize<JevScoreAnswer>(options),
            "noul" => root.Deserialize<JevNoulAnswer>(options),
            _ => new JevUnknownAnswer(type ?? "unknown", root.Clone()),
        };
    }

    public override void Write(Utf8JsonWriter writer, JevAnswer value, JsonSerializerOptions options)
    {
        if (value is JevUnknownAnswer unknown)
        {
            unknown.Raw.WriteTo(writer);
            return;
        }

        var node = JsonSerializer.SerializeToNode(value, value.GetType(), options)!.AsObject();
        node.Remove("type"); // overrides of the ignored Type property still serialize
        node.Insert(0, "type", value.Type);
        node.WriteTo(writer, options);
    }
}

/// <summary>Serializer settings matching the Jev wire format (snake_case, nulls omitted).</summary>
public static class JevJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DictionaryKeyPolicy = null, // question and option names are ours; keep them verbatim
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;
using Nullbox.Core.Gmail;
using Nullbox.Core.Jev;
using Nullbox.Core.Rules;

namespace Nullbox.Web.Ui.Rules;

public enum QuestionKind
{
    Choice,
    Score,
    Noul,
}

/// <summary>A choice option (name and description) or a score level (description only).</summary>
public sealed class OptionDraft
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Plain text, or JSON when it starts with <c>{</c> or <c>[</c> and parses (see <see cref="JsonText"/>).</summary>
    public string Description { get; set; } = string.Empty;
}

public sealed class QuestionDraft
{
    public QuestionKind Kind { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Plain text, or JSON when it starts with <c>{</c> or <c>[</c> and parses.</summary>
    public string Instructions { get; set; } = string.Empty;

    /// <summary>Choice options, or score levels lowest first.</summary>
    public List<OptionDraft> Options { get; } = [];

    /// <summary>Noul: what makes the statement true. Optional.</summary>
    public string TrueCriteria { get; set; } = string.Empty;

    /// <summary>Noul: what makes the statement false. Optional.</summary>
    public string FalseCriteria { get; set; } = string.Empty;
}

public sealed class DecisionDraft
{
    public string Name { get; set; } = string.Empty;

    public string When { get; set; } = string.Empty;

    public List<string> Actions { get; } = [];

    /// <summary>
    /// New decisions are dry runs: they record what they match and change nothing until switched to Archive (with
    /// their review label), then Delete, which is only set after a confirmation in the editor.
    /// </summary>
    public RemoveMode? Remove { get; set; } = RemoveMode.DryRun;

    public bool AllowProtected { get; set; }
}

/// <summary>
/// The editor's mutable view of a <see cref="RuleSetDocument"/>. The visual tabs and the JSON tab both read and write
/// this, so they stay in sync: <see cref="ToDocument"/> and <see cref="ToJson"/> build the rule set, and
/// <see cref="TryLoadJson"/> replaces the contents from edited JSON.
/// </summary>
public sealed class RuleSetEditorModel
{
    public string? Schema { get; set; } = "/schemas/ruleset.schema.json";

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// New rule sets are live, as in <see cref="RuleSetDocument.Mode"/>: each rule is tried on its own, starting as a
    /// dry run (<see cref="DecisionDraft.Remove"/>) and promoted to Archive, then Delete. Saving never activates a rule set.
    /// </summary>
    public RuleSetMode Mode { get; set; } = RuleSetMode.Live;

    public string Model { get; set; } = JevRequest.DefaultModel;

    public DecisionMatch Match { get; set; } = DecisionMatch.First;

    public List<string> Fields { get; } = [.. new StateTemplate().Fields];

    public int BodyMaxChars { get; set; } = new StateTemplate().BodyMaxChars;

    /// <summary>All questions in order; the Choices, Scores and Nouls tabs each show one kind.</summary>
    public List<QuestionDraft> Questions { get; } = [];

    public List<DecisionDraft> Decisions { get; } = [];

    /// <summary>A sample email for the state and request previews.</summary>
    public static GmailMessage SampleMessage { get; } = new(
        "sample",
        "sample-thread",
        "Shop <deals@shop.example>",
        "50% off everything this weekend",
        "Fri, 27 Nov 2026 08:00:00 +0000",
        "<mailto:unsubscribe@shop.example>",
        [GmailLabels.Inbox, GmailLabels.Unread, "CATEGORY_PROMOTIONS"],
        "Our biggest sale of the year starts now. Everything is half price until Sunday.",
        new DateTimeOffset(2026, 11, 25, 8, 0, 0, TimeSpan.Zero),
        "Our biggest sale of the year starts now. Everything is half price until Sunday night, online and in stores. "
            + "Use code WEEKEND at checkout. You are receiving this because you signed up for deals.");

    public static RuleSetEditorModel FromDocument(RuleSetDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var model = new RuleSetEditorModel();
        model.Load(document);
        return model;
    }

    public IEnumerable<QuestionDraft> OfKind(QuestionKind kind) => Questions.Where(q => q.Kind == kind);

    public QuestionDraft AddQuestion(QuestionKind kind)
    {
        var prefix = kind switch
        {
            QuestionKind.Choice => "choice",
            QuestionKind.Score => "score",
            _ => "noul",
        };
        var n = 1;
        while (Questions.Any(q => q.Name == $"{prefix}_{n}"))
        {
            n++;
        }

        var question = new QuestionDraft { Kind = kind, Name = $"{prefix}_{n}" };
        if (kind == QuestionKind.Choice)
        {
            question.Options.Add(new OptionDraft { Name = "yes" });
            question.Options.Add(new OptionDraft { Name = "no" });
        }
        else if (kind == QuestionKind.Score)
        {
            question.Options.Add(new OptionDraft { Description = "Low" });
            question.Options.Add(new OptionDraft { Description = "High" });
        }

        Questions.Add(question);
        return question;
    }

    public DecisionDraft AddDecision()
    {
        var decision = new DecisionDraft { Name = $"Rule {Decisions.Count + 1}" };
        Decisions.Add(decision);
        return decision;
    }

    /// <summary>Moves the item at <paramref name="index"/> by <paramref name="delta"/> places; out-of-range moves do nothing.</summary>
    public static bool Move<T>(List<T> items, int index, int delta)
    {
        ArgumentNullException.ThrowIfNull(items);
        var target = index + delta;
        if (index < 0 || index >= items.Count || target < 0 || target >= items.Count)
        {
            return false;
        }

        (items[index], items[target]) = (items[target], items[index]);
        return true;
    }

    /// <summary>Moves a question among the questions of its own kind (the order the tab shows).</summary>
    public bool MoveQuestion(QuestionDraft question, int delta)
    {
        var sameKind = Questions.Select((q, i) => (q, i)).Where(x => x.q.Kind == question.Kind).Select(x => x.i).ToList();
        var at = sameKind.FindIndex(i => ReferenceEquals(Questions[i], question));
        var to = at + delta;
        if (at < 0 || to < 0 || to >= sameKind.Count)
        {
            return false;
        }

        (Questions[sameKind[at]], Questions[sameKind[to]]) = (Questions[sameKind[to]], Questions[sameKind[at]]);
        return true;
    }

    /// <summary>
    /// Problems the editor catches before building the document (blank or repeated question names), then the JSON
    /// Schema and the rule-set validator, at JSON paths like <c>/decisions/0/when</c>.
    /// </summary>
    public IReadOnlyList<RuleSetError> Validate()
    {
        var errors = ShapeErrors();
        if (errors.Count > 0)
        {
            return errors;
        }

        RuleJson.Parse(RuleJson.Serialize(ToDocument()), out var documentErrors);
        return documentErrors;
    }

    public RuleSetDocument ToDocument()
    {
        var questions = new Dictionary<string, JevQuestion>(StringComparer.Ordinal);
        foreach (var question in Questions)
        {
            questions.TryAdd(question.Name.Trim(), ToQuestion(question));
        }

        return new RuleSetDocument
        {
            Schema = Schema,
            Name = Name.Trim(),
            Description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim(),
            Mode = Mode,
            Model = Model.Trim(),
            State = new StateTemplate { Fields = [.. Fields], BodyMaxChars = BodyMaxChars },
            Questions = questions,
            Match = Match,
            Decisions = [.. Decisions.Select(d => new DecisionRule
            {
                Name = string.IsNullOrWhiteSpace(d.Name) ? null : d.Name.Trim(),
                When = d.When.Trim(),
                Actions = [.. d.Actions.Select(a => a.Trim()).Where(a => a.Length > 0)],
                Remove = d.Remove,
                AllowProtected = d.AllowProtected,
            })],
        };
    }

    public string ToJson() => RuleJson.Serialize(ToDocument());

    /// <summary>The JSON, or null while the editor holds something that can't be built (see <see cref="Validate"/>).</summary>
    public string? TryToJson() => ShapeErrors().Count == 0 ? ToJson() : null;

    /// <summary>Replaces the contents with <paramref name="json"/> when it is a valid rule set; otherwise leaves them as they are.</summary>
    public bool TryLoadJson(string json, out IReadOnlyList<RuleSetError> errors)
    {
        var document = RuleJson.Parse(json, out errors);
        if (document is null)
        {
            return false;
        }

        Load(document);
        return true;
    }

    /// <summary>The state Jev would see for <see cref="SampleMessage"/>.</summary>
    public string StatePreview(DateTimeOffset now)
        => JevRequestBuilder.BuildState(new StateTemplate { Fields = [.. Fields], BodyMaxChars = BodyMaxChars }, SampleMessage, now)
            .ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    /// <summary>The exact request body sent to Jev for <see cref="SampleMessage"/>, or null while the editor can't build one.</summary>
    public string? RequestPreview(DateTimeOffset now)
    {
        if (ShapeErrors().Count > 0)
        {
            return null;
        }

        var request = JevRequestBuilder.Build(ToDocument(), SampleMessage, now);
        return JsonSerializer.Serialize(request, new JsonSerializerOptions(JevJson.Options) { WriteIndented = true });
    }

    /// <summary>The review label a decision adds to mail it archives (or deletes).</summary>
    public string ReviewLabel(DecisionDraft decision)
        => ReviewLabels.For(
            string.IsNullOrWhiteSpace(Name) ? "rule-set" : Name.Trim(),
            new DecisionRule { Name = decision.Name, When = decision.When },
            Decisions.IndexOf(decision));

    private List<RuleSetError> ShapeErrors()
    {
        var errors = new List<RuleSetError>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < Questions.Count; i++)
        {
            var name = Questions[i].Name.Trim();
            if (name.Length == 0)
            {
                errors.Add(new RuleSetError($"/questions/{i}", "Give every question a name."));
            }
            else if (!seen.Add(name))
            {
                errors.Add(new RuleSetError($"/questions/{name}", $"Two questions are named '{name}'; question names must be unique."));
            }

            if (Questions[i].Kind == QuestionKind.Choice)
            {
                var options = new HashSet<string>(StringComparer.Ordinal);
                foreach (var option in Questions[i].Options)
                {
                    var optionName = option.Name.Trim();
                    if (optionName.Length == 0)
                    {
                        errors.Add(new RuleSetError($"/questions/{name}/criteria", "Give every option a name."));
                    }
                    else if (!options.Add(optionName))
                    {
                        errors.Add(new RuleSetError($"/questions/{name}/criteria", $"Option '{optionName}' is listed twice."));
                    }
                }
            }
        }

        return errors;
    }

    private void Load(RuleSetDocument document)
    {
        Schema = document.Schema;
        Name = document.Name;
        Description = document.Description ?? string.Empty;
        Mode = document.Mode;
        Model = document.Model;
        Match = document.Match;
        Fields.Clear();
        Fields.AddRange(document.State.Fields);
        BodyMaxChars = document.State.BodyMaxChars;

        Questions.Clear();
        foreach (var (name, question) in document.Questions)
        {
            Questions.Add(FromQuestion(name, question));
        }

        Decisions.Clear();
        foreach (var rule in document.Decisions)
        {
            var decision = new DecisionDraft
            {
                Name = rule.Name ?? string.Empty,
                When = rule.When,
                Remove = rule.Remove,
                AllowProtected = rule.AllowProtected,
            };
            decision.Actions.AddRange(rule.Actions);
            Decisions.Add(decision);
        }
    }

    private static QuestionDraft FromQuestion(string name, JevQuestion question)
    {
        var draft = new QuestionDraft { Name = name, Instructions = JsonText.ToText(question.Instructions) };
        switch (question)
        {
            case JevChoiceQuestion choice:
                draft.Kind = QuestionKind.Choice;
                draft.Options.AddRange(choice.Criteria.Select(c => new OptionDraft { Name = c.Key, Description = JsonText.ToText(c.Value) }));
                break;
            case JevScoreQuestion score:
                draft.Kind = QuestionKind.Score;
                draft.Options.AddRange(score.Criteria.Select(c => new OptionDraft { Description = JsonText.ToText(c) }));
                break;
            case JevNoulQuestion noul:
                draft.Kind = QuestionKind.Noul;
                draft.TrueCriteria = JsonText.ToText(noul.Criteria?.True);
                draft.FalseCriteria = JsonText.ToText(noul.Criteria?.False);
                break;
        }

        return draft;
    }

    private static JevQuestion ToQuestion(QuestionDraft draft)
    {
        var instructions = JsonText.ToNode(draft.Instructions);
        return draft.Kind switch
        {
            QuestionKind.Choice => new JevChoiceQuestion
            {
                Instructions = instructions,
                Criteria = draft.Options
                    .Where(o => o.Name.Trim().Length > 0)
                    .DistinctBy(o => o.Name.Trim())
                    .ToDictionary(o => o.Name.Trim(), o => (JsonNode?)(JsonText.ToNode(o.Description) ?? JsonValue.Create(string.Empty))),
            },
            QuestionKind.Score => new JevScoreQuestion
            {
                Instructions = instructions,
                Criteria = [.. draft.Options.Select(o => (JsonNode?)(JsonText.ToNode(o.Description) ?? JsonValue.Create(string.Empty)))],
            },
            _ => new JevNoulQuestion
            {
                Instructions = instructions,
                Criteria = draft.TrueCriteria.Trim().Length == 0 && draft.FalseCriteria.Trim().Length == 0
                    ? null
                    : new JevNoulCriteria { True = JsonText.ToNode(draft.TrueCriteria), False = JsonText.ToNode(draft.FalseCriteria) },
            },
        };
    }
}

/// <summary>
/// Instructions and criteria may be strings or structured JSON. The editor shows a string as itself and anything
/// else as JSON; text that starts with <c>{</c> or <c>[</c> and parses is read back as JSON, anything else as a string.
/// </summary>
public static class JsonText
{
    public static string ToText(JsonNode? node) => node switch
    {
        null => string.Empty,
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        _ => node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
    };

    /// <summary>Null for blank text.</summary>
    public static JsonNode? ToNode(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.Trim();
        if (trimmed[0] is '{' or '[')
        {
            try
            {
                return JsonNode.Parse(trimmed);
            }
            catch (JsonException)
            {
                // Not JSON after all: keep it as text.
            }
        }

        return JsonValue.Create(text);
    }
}

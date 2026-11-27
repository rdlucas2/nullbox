namespace Nullbox.Core.Rules;

public enum RuleActionKind
{
    /// <summary>Apply a user label (created if needed).</summary>
    Label,

    MarkRead,

    /// <summary>Remove from the inbox.</summary>
    Archive,

    /// <summary>Move to Trash (recoverable for 30 days). Rules never delete permanently.</summary>
    Trash,

    Star,

    /// <summary>Match and stop (with first-match) without changing anything.</summary>
    None,
}

/// <summary>A parsed decision action: <c>label:&lt;name&gt;</c>, <c>mark_read</c>, <c>archive</c>, <c>trash</c>, <c>star</c> or <c>none</c>.</summary>
public sealed record RuleAction(RuleActionKind Kind, string? Label = null)
{
    public const int MaxLabelLength = 225;
    private const string LabelPrefix = "label:";

    public static bool TryParse(string? text, out RuleAction action, out string? error)
    {
        action = new RuleAction(RuleActionKind.None);
        error = null;
        var value = text?.Trim() ?? string.Empty;

        if (value.StartsWith(LabelPrefix, StringComparison.Ordinal))
        {
            var label = value[LabelPrefix.Length..].Trim();
            if (label.Length == 0)
            {
                error = "label: needs a label name, e.g. label:promo.";
                return false;
            }

            if (label.Length > MaxLabelLength)
            {
                error = $"Label names can be at most {MaxLabelLength} characters.";
                return false;
            }

            action = new RuleAction(RuleActionKind.Label, label);
            return true;
        }

        RuleActionKind? kind = value switch
        {
            "mark_read" => RuleActionKind.MarkRead,
            "archive" => RuleActionKind.Archive,
            "trash" => RuleActionKind.Trash,
            "star" => RuleActionKind.Star,
            "none" => RuleActionKind.None,
            _ => null,
        };
        if (kind is null)
        {
            error = $"Unknown action '{value}'. Use label:<name>, mark_read, archive, trash, star or none.";
            return false;
        }

        action = new RuleAction(kind.Value);
        return true;
    }

    public static RuleAction Parse(string text)
        => TryParse(text, out var action, out var error) ? action : throw new FormatException(error);

    public override string ToString() => Kind switch
    {
        RuleActionKind.Label => LabelPrefix + Label,
        RuleActionKind.MarkRead => "mark_read",
        RuleActionKind.Archive => "archive",
        RuleActionKind.Trash => "trash",
        RuleActionKind.Star => "star",
        _ => "none",
    };
}

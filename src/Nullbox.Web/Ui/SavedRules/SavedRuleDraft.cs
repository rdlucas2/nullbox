using Nullbox.Core.Rules;
using Nullbox.Core.SavedRules;

namespace Nullbox.Web.Ui.SavedRules;

/// <summary>The Saved rules page's editor form: one <see cref="SavedRule"/> as separate, editable fields.</summary>
public sealed class SavedRuleDraft
{
    public string Name { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    /// <summary>New saved rules start as dry runs.</summary>
    public RuleSetMode Mode { get; set; } = RuleSetMode.DryRun;

    public string? From { get; set; }

    public string? To { get; set; }

    public string? SubjectContains { get; set; }

    /// <summary>Label names, comma-separated.</summary>
    public string? Labels { get; set; }

    public bool MarkRead { get; set; }

    public bool Star { get; set; }

    public RemoveMode? Remove { get; set; } = RemoveMode.Archive;

    public bool Stop { get; set; } = true;

    public bool AllowProtected { get; set; }

    public static SavedRuleDraft Of(SavedRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var actions = rule.Actions.Select(RuleAction.Parse).ToList();
        return new SavedRuleDraft
        {
            Name = rule.Name,
            Enabled = rule.Enabled,
            Mode = rule.Mode,
            From = rule.From,
            To = rule.To,
            SubjectContains = rule.SubjectContains,
            Labels = string.Join(", ", actions.Where(a => a.Kind == RuleActionKind.Label).Select(a => a.Label)),
            MarkRead = actions.Any(a => a.Kind == RuleActionKind.MarkRead),
            Star = actions.Any(a => a.Kind == RuleActionKind.Star),
            Remove = rule.Remove,
            Stop = rule.Stop,
            AllowProtected = rule.AllowProtected,
        };
    }

    /// <summary>The rule the form describes; blank fields are left out, labels first, then star, then mark read.</summary>
    public SavedRule ToRule()
    {
        var actions = (Labels ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .Select(label => "label:" + label)
            .ToList();
        if (Star)
        {
            actions.Add("star");
        }

        if (MarkRead)
        {
            actions.Add("mark_read");
        }

        return new SavedRule
        {
            Name = Name.Trim(),
            Enabled = Enabled,
            Mode = Mode,
            From = Blank(From),
            To = Blank(To),
            SubjectContains = Blank(SubjectContains),
            Actions = actions,
            Remove = Remove,
            Stop = Stop,
            AllowProtected = AllowProtected,
        };
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>How the Saved rules page describes a rule in its list and test box.</summary>
public static class SavedRuleText
{
    public static string Match(SavedRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(rule.From))
        {
            parts.Add($"from {rule.From}");
        }

        if (!string.IsNullOrWhiteSpace(rule.To))
        {
            parts.Add($"to {rule.To}");
        }

        if (!string.IsNullOrWhiteSpace(rule.SubjectContains))
        {
            parts.Add($"subject contains \"{rule.SubjectContains}\"");
        }

        return string.Join(", ", parts);
    }

    public static string Action(SavedRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var parts = rule.Actions.Select(a => RuleAction.Parse(a) switch
        {
            { Kind: RuleActionKind.Label, Label: var label } => $"label {label}",
            { Kind: RuleActionKind.MarkRead } => "mark read",
            { Kind: RuleActionKind.Star } => "star",
            _ => a,
        }).ToList();
        if (rule.Remove == RemoveMode.Archive)
        {
            parts.Add("archive");
        }
        else if (rule.Remove == RemoveMode.Delete)
        {
            parts.Add("delete");
        }

        return string.Join(", ", parts);
    }

    /// <summary>The Audit page filtered to the rule's decisions.</summary>
    public static string AuditHref(SavedRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return $"/audit?ruleSet={Uri.EscapeDataString(rule.Key)}";
    }
}

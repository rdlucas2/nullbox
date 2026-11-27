using Nullbox.Core.Cleanup;

namespace Nullbox.Web.Ui.Cleanup;

/// <summary>A cleanup rule file as the page edits it; converts to and from <see cref="CleanupConfig"/> and its JSON.</summary>
public sealed class CleanupForm
{
    public string Label { get; set; } = string.Empty;

    public bool KeepStarred { get; set; } = true;

    public bool KeepImportant { get; set; } = true;

    /// <summary>One sender or domain per line (commas work too).</summary>
    public string Senders { get; set; } = string.Empty;

    public List<CleanupRuleForm> Rules { get; } = [];

    public static CleanupForm Starter()
    {
        var form = new CleanupForm();
        form.Rules.Add(new CleanupRuleForm { Name = "Old promotions", Query = "category:promotions older_than:30d" });
        return form;
    }

    public static CleanupForm From(CleanupConfig config)
    {
        var form = new CleanupForm
        {
            Label = config.Label ?? string.Empty,
            KeepStarred = config.Keep.Starred,
            KeepImportant = config.Keep.Important,
            Senders = string.Join('\n', config.Keep.Senders),
        };
        form.Rules.AddRange(config.Rules.Select(r => new CleanupRuleForm
        {
            Name = r.Name,
            Query = r.Query,
            UnreadOnly = r.UnreadOnly,
            MarkRead = r.MarkRead,
            Archive = r.Archive,
        }));
        return form;
    }

    public CleanupConfig ToConfig() => new()
    {
        Label = string.IsNullOrWhiteSpace(Label) ? null : Label.Trim(),
        Keep = new CleanupKeep
        {
            Starred = KeepStarred,
            Important = KeepImportant,
            Senders = [.. Senders.Split(['\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)],
        },
        Rules = [.. Rules.Select(r => new CleanupRule
        {
            Name = r.Name.Trim(),
            Query = r.Query.Trim(),
            UnreadOnly = r.UnreadOnly,
            MarkRead = r.MarkRead,
            Archive = r.Archive,
        })],
    };

    /// <summary>The JSON to save, or null with the problem when the form is not a valid file yet.</summary>
    public string? TryToJson(out string? problem)
    {
        var json = ToConfig().ToJson();
        try
        {
            CleanupConfig.Parse(json);
            problem = null;
            return json;
        }
        catch (CleanupConfigException ex)
        {
            problem = ex.Message;
            return null;
        }
    }
}

public sealed class CleanupRuleForm
{
    public string Name { get; set; } = string.Empty;

    public string Query { get; set; } = string.Empty;

    public bool UnreadOnly { get; set; } = true;

    public bool MarkRead { get; set; } = true;

    public bool Archive { get; set; } = true;
}

using System.Collections.Concurrent;
using Nullbox.Core.Gmail;
using Nullbox.Core.Inbox;

namespace Nullbox.Tests.Inbox;

/// <summary>Scriptable <see cref="IGmailService"/> covering the calls the inbox poll makes.</summary>
public sealed class FakeInboxGmail : IGmailService
{
    public ulong CurrentHistoryId { get; set; } = 100;

    /// <summary>Newest first, as Gmail search returns them.</summary>
    public List<string> InboxSearchResults { get; } = [];

    public Func<ulong, GmailHistoryChanges>? History { get; set; }

    public List<string> Calls { get; } = [];

    public Task<ulong> GetCurrentHistoryIdAsync(CancellationToken cancellationToken = default)
    {
        Calls.Add("profile");
        return Task.FromResult(CurrentHistoryId);
    }

    public Task<GmailHistoryChanges> GetInboxChangesAsync(ulong startHistoryId, CancellationToken cancellationToken = default)
    {
        Calls.Add($"history:{startHistoryId}");
        return Task.FromResult((History ?? throw new InvalidOperationException("no history scripted"))(startHistoryId));
    }

    /// <summary>Per-query search results; when unset every search returns <see cref="InboxSearchResults"/>.</summary>
    public Func<string, IReadOnlyList<string>>? Search { get; set; }

    public Task<IReadOnlyList<string>> SearchAsync(string query, int? maxResults = null, CancellationToken cancellationToken = default)
    {
        Calls.Add($"search:{query}");
        var ids = Search?.Invoke(query) ?? [.. InboxSearchResults];
        return Task.FromResult<IReadOnlyList<string>>(maxResults is { } max ? [.. ids.Take(max)] : ids);
    }

    /// <summary>Messages <see cref="GetMessageAsync"/> returns; unknown ids throw <see cref="KeyNotFoundException"/>.</summary>
    public ConcurrentDictionary<string, GmailMessage> Messages { get; } = new();

    /// <summary>Every change made, e.g. <c>modify:m1:+Label_1,STARRED:-UNREAD,INBOX</c> or <c>trash:m1</c>.</summary>
    public ConcurrentQueue<string> Changes { get; } = new();

    public ConcurrentQueue<MessageFormat> Fetches { get; } = new();

    public Func<string, Exception?>? FailGet { get; set; }

    public Task<GmailMessage> GetMessageAsync(string messageId, MessageFormat format = MessageFormat.Metadata, CancellationToken cancellationToken = default)
    {
        Fetches.Enqueue(format);
        if (FailGet?.Invoke(messageId) is { } error)
        {
            throw error;
        }

        return Messages.TryGetValue(messageId, out var message)
            ? Task.FromResult(message)
            : throw new KeyNotFoundException(messageId);
    }

    public Task<string> EnsureLabelAsync(string name, CancellationToken cancellationToken = default)
    {
        Changes.Enqueue($"label:{name}");
        return Task.FromResult("Label_" + name);
    }

    public Task BatchModifyAsync(IEnumerable<string> messageIds, IEnumerable<string>? addLabelIds, IEnumerable<string>? removeLabelIds, CancellationToken cancellationToken = default)
    {
        Changes.Enqueue($"modify:{string.Join(",", messageIds)}:+{string.Join(",", addLabelIds ?? [])}:-{string.Join(",", removeLabelIds ?? [])}");
        return Task.CompletedTask;
    }

    public Task MarkReadAsync(IEnumerable<string> messageIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task ArchiveAsync(IEnumerable<string> messageIds, string? label, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    /// <summary>Mirrors the real service: Trash is refused while the delete lock is on (the default).</summary>
    public bool AllowDelete { get; set; }

    public Task TrashAsync(IEnumerable<string> messageIds, CancellationToken cancellationToken = default)
    {
        if (!AllowDelete)
        {
            throw new DeleteLockedException();
        }

        Changes.Enqueue($"trash:{string.Join(",", messageIds)}");
        return Task.CompletedTask;
    }

    /// <summary>Off by default, so anything that tries to delete permanently fails loudly.</summary>
    public bool AllowPermanentDelete { get; set; }

    public Task BatchDeleteAsync(IEnumerable<string> messageIds, CancellationToken cancellationToken = default)
    {
        if (!AllowPermanentDelete)
        {
            throw new InvalidOperationException("The pipeline must never delete permanently.");
        }

        Changes.Enqueue($"delete:{string.Join(",", messageIds)}");
        return Task.CompletedTask;
    }

    /// <summary>A message with the given labels (INBOX and UNREAD by default).</summary>
    public GmailMessage Add(string id, params string[] labels)
    {
        var message = new GmailMessage(
            id, "t-" + id, "Shop <deals@shop.example>", "50% off everything", "Fri, 27 Nov 2026 08:00:00 +0000",
            "<mailto:unsub@shop.example>", labels.Length == 0 ? [GmailLabels.Inbox, GmailLabels.Unread] : labels,
            "Our biggest sale", DateTimeOffset.Parse("2026-11-27T08:00:00Z", System.Globalization.CultureInfo.InvariantCulture), "Body text");
        Messages[id] = message;
        return message;
    }
}

public sealed class RecordingQueue : IMessageJobQueue
{
    public List<string> Enqueued { get; } = [];

    public List<(string MessageId, DateTimeOffset At)> Scheduled { get; } = [];

    public void EnqueueProcessing(string messageId) => Enqueued.Add(messageId);

    public void ScheduleProcessing(string messageId, DateTimeOffset at) => Scheduled.Add((messageId, at));
}

public sealed class RecordingProcessor : IMessageProcessor
{
    private int _running;

    public ConcurrentQueue<string> Processed { get; } = new();

    public int MaxConcurrent { get; private set; }

    public TimeSpan Delay { get; set; } = TimeSpan.Zero;

    public async Task ProcessAsync(string messageId, CancellationToken cancellationToken)
    {
        var now = Interlocked.Increment(ref _running);
        lock (this)
        {
            MaxConcurrent = Math.Max(MaxConcurrent, now);
        }

        try
        {
            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken);
            }

            Processed.Enqueue(messageId);
        }
        finally
        {
            Interlocked.Decrement(ref _running);
        }
    }
}

/// <summary>A SQLite state store in its own temp file.</summary>
public sealed class TempStore : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nullbox-inbox-tests", Guid.NewGuid().ToString("N"));

    public TempStore() => Store = Open();

    public string DbPath => System.IO.Path.Combine(_dir, "nullbox.db");

    public Nullbox.Web.Inbox.SqliteInboxStateStore Store { get; }

    public Nullbox.Web.Inbox.SqliteInboxStateStore Open() => new(DbPath);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

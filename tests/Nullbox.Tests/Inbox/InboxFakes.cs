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

    public Task<IReadOnlyList<string>> SearchAsync(string query, int? maxResults = null, CancellationToken cancellationToken = default)
    {
        Calls.Add($"search:{query}");
        return Task.FromResult<IReadOnlyList<string>>([.. InboxSearchResults]);
    }

    public Task<GmailMessage> GetMessageAsync(string messageId, MessageFormat format = MessageFormat.Metadata, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<string> EnsureLabelAsync(string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task BatchModifyAsync(IEnumerable<string> messageIds, IEnumerable<string>? addLabelIds, IEnumerable<string>? removeLabelIds, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task MarkReadAsync(IEnumerable<string> messageIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task ArchiveAsync(IEnumerable<string> messageIds, string? label, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task TrashAsync(IEnumerable<string> messageIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task BatchDeleteAsync(IEnumerable<string> messageIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

public sealed class RecordingQueue : IMessageJobQueue
{
    public List<string> Enqueued { get; } = [];

    public void EnqueueProcessing(string messageId) => Enqueued.Add(messageId);
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

using System.Globalization;
using Microsoft.Data.Sqlite;
using Nullbox.Core.Inbox;

namespace Nullbox.Web.Inbox;

/// <summary>Inbox sync state in <c>{data}/nullbox.db</c> (SQLite, WAL).</summary>
public sealed class SqliteInboxStateStore : IInboxStateStore
{
    private const string HistoryKey = "history_id";
    private const string PausedKey = "paused";
    private const string LastPollKey = "last_poll_utc";
    private const string LastResultKey = "last_poll_result";

    private readonly string _connectionString;
    private readonly SemaphoreSlim _init = new(1, 1);
    private bool _ready;

    public SqliteInboxStateStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
    }

    public async Task<InboxSyncState> GetAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT key, value FROM inbox_state";
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                values[reader.GetString(0)] = reader.GetString(1);
            }
        }

        return new InboxSyncState(
            values.TryGetValue(HistoryKey, out var h) ? ulong.Parse(h, CultureInfo.InvariantCulture) : null,
            values.TryGetValue(PausedKey, out var p) && p == "1",
            values.TryGetValue(LastPollKey, out var at) ? DateTimeOffset.Parse(at, CultureInfo.InvariantCulture) : null,
            values.GetValueOrDefault(LastResultKey));
    }

    public Task SetHistoryIdAsync(ulong historyId, CancellationToken cancellationToken = default)
        => SetAsync(cancellationToken, (HistoryKey, historyId.ToString(CultureInfo.InvariantCulture)));

    public Task SetPausedAsync(bool paused, CancellationToken cancellationToken = default)
        => SetAsync(cancellationToken, (PausedKey, paused ? "1" : "0"));

    public Task RecordPollAsync(DateTimeOffset at, string result, CancellationToken cancellationToken = default)
        => SetAsync(cancellationToken, (LastPollKey, at.ToString("O", CultureInfo.InvariantCulture)), (LastResultKey, result));

    public async Task<IReadOnlyList<string>> FilterUnseenAsync(IReadOnlyList<string> messageIds, CancellationToken cancellationToken = default)
    {
        if (messageIds.Count == 0)
        {
            return [];
        }

        await using var conn = await OpenAsync(cancellationToken);
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chunk in messageIds.Distinct(StringComparer.Ordinal).Chunk(500))
        {
            await using var cmd = conn.CreateCommand();
            var names = chunk.Select((id, i) =>
            {
                var name = "$p" + i.ToString(CultureInfo.InvariantCulture);
                cmd.Parameters.AddWithValue(name, id);
                return name;
            });
            cmd.CommandText = $"SELECT message_id FROM inbox_messages WHERE message_id IN ({string.Join(',', names)})";
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                known.Add(reader.GetString(0));
            }
        }

        var unseen = new List<string>();
        foreach (var id in messageIds)
        {
            if (known.Add(id))
            {
                unseen.Add(id);
            }
        }

        return unseen;
    }

    public async Task MarkEnqueuedAsync(string messageId, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO inbox_messages (message_id, enqueued_utc) VALUES ($id, $at)";
        cmd.Parameters.AddWithValue("$id", messageId);
        cmd.Parameters.AddWithValue("$at", at.ToUnixTimeMilliseconds());
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> MarkProcessedAsync(string messageId, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO inbox_messages (message_id, enqueued_utc, processed_utc) VALUES ($id, $at, $at)
            ON CONFLICT(message_id) DO UPDATE SET processed_utc = excluded.processed_utc WHERE processed_utc IS NULL
            """;
        cmd.Parameters.AddWithValue("$id", messageId);
        cmd.Parameters.AddWithValue("$at", at.ToUnixTimeMilliseconds());
        return await cmd.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<bool> IsProcessedAsync(string messageId, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM inbox_messages WHERE message_id = $id AND processed_utc IS NOT NULL";
        cmd.Parameters.AddWithValue("$id", messageId);
        return await cmd.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private async Task SetAsync(CancellationToken cancellationToken, params (string Key, string Value)[] pairs)
    {
        await using var conn = await OpenAsync(cancellationToken);
        await using var tx = conn.BeginTransaction();
        foreach (var (key, value) in pairs)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO inbox_state (key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value";
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$v", value);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        if (_ready)
        {
            return conn;
        }

        await _init.WaitAsync(cancellationToken);
        try
        {
            if (!_ready)
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    PRAGMA journal_mode = WAL;
                    CREATE TABLE IF NOT EXISTS inbox_state (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                    CREATE TABLE IF NOT EXISTS inbox_messages (
                        message_id TEXT PRIMARY KEY,
                        enqueued_utc INTEGER NOT NULL,
                        processed_utc INTEGER NULL);
                    """;
                await cmd.ExecuteNonQueryAsync(cancellationToken);
                _ready = true;
            }
        }
        finally
        {
            _init.Release();
        }

        return conn;
    }
}

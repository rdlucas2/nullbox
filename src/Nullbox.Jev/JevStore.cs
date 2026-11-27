using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;

namespace Nullbox.Jev;

/// <summary>Persists cached Jev results and the usage ledger behind the cost guard.</summary>
public interface IJevStore
{
    Task<string?> GetCachedAsync(string key, CancellationToken cancellationToken = default);

    Task PutCachedAsync(string key, string model, string resultJson, DateTimeOffset at, CancellationToken cancellationToken = default);

    Task RecordUsageAsync(DateTimeOffset at, long inputTokens, string model, CancellationToken cancellationToken = default);

    /// <summary>Input tokens and request count with <c>at &gt;= since</c>, plus the oldest such request.</summary>
    Task<JevUsageWindow> GetUsageSinceAsync(DateTimeOffset since, CancellationToken cancellationToken = default);
}

public sealed record JevUsageWindow(long InputTokens, int Requests, DateTimeOffset? Oldest);

/// <summary>Process-local store for tests and in-memory deployments.</summary>
public sealed class InMemoryJevStore : IJevStore
{
    private readonly ConcurrentDictionary<string, string> _cache = new();
    private readonly ConcurrentQueue<(DateTimeOffset At, long Tokens)> _usage = new();

    public Task<string?> GetCachedAsync(string key, CancellationToken cancellationToken = default)
        => Task.FromResult(_cache.TryGetValue(key, out var json) ? json : null);

    public Task PutCachedAsync(string key, string model, string resultJson, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        _cache[key] = resultJson;
        return Task.CompletedTask;
    }

    public Task RecordUsageAsync(DateTimeOffset at, long inputTokens, string model, CancellationToken cancellationToken = default)
    {
        _usage.Enqueue((at, inputTokens));
        return Task.CompletedTask;
    }

    public Task<JevUsageWindow> GetUsageSinceAsync(DateTimeOffset since, CancellationToken cancellationToken = default)
    {
        var rows = _usage.Where(u => u.At >= since).ToList();
        return Task.FromResult(new JevUsageWindow(
            rows.Sum(r => r.Tokens),
            rows.Count,
            rows.Count == 0 ? null : rows.Min(r => r.At)));
    }
}

/// <summary>SQLite store (WAL) at <c>{data}/jev.db</c> so the cache and the daily budget survive restarts.</summary>
public sealed class SqliteJevStore : IJevStore
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _init = new(1, 1);
    private bool _ready;

    public SqliteJevStore(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
    }

    public async Task<string?> GetCachedAsync(string key, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT result_json FROM jev_cache WHERE key = $key";
        cmd.Parameters.AddWithValue("$key", key);
        return await cmd.ExecuteScalarAsync(cancellationToken) as string;
    }

    public async Task PutCachedAsync(string key, string model, string resultJson, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO jev_cache (key, model, result_json, created_utc) VALUES ($key, $model, $json, $at)
            ON CONFLICT(key) DO UPDATE SET model = excluded.model, result_json = excluded.result_json, created_utc = excluded.created_utc
            """;
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$model", model);
        cmd.Parameters.AddWithValue("$json", resultJson);
        cmd.Parameters.AddWithValue("$at", at.ToUnixTimeMilliseconds());
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RecordUsageAsync(DateTimeOffset at, long inputTokens, string model, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO jev_usage (at_utc, input_tokens, model) VALUES ($at, $tokens, $model)";
        cmd.Parameters.AddWithValue("$at", at.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$tokens", inputTokens);
        cmd.Parameters.AddWithValue("$model", model);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<JevUsageWindow> GetUsageSinceAsync(DateTimeOffset since, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(SUM(input_tokens), 0), COUNT(*), MIN(at_utc) FROM jev_usage WHERE at_utc >= $since";
        cmd.Parameters.AddWithValue("$since", since.ToUnixTimeMilliseconds());
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new JevUsageWindow(
            reader.GetInt64(0),
            reader.GetInt32(1),
            reader.IsDBNull(2) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)));
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        if (!_ready)
        {
            await _init.WaitAsync(cancellationToken);
            try
            {
                if (!_ready)
                {
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = """
                        PRAGMA journal_mode = WAL;
                        CREATE TABLE IF NOT EXISTS jev_cache (
                            key TEXT PRIMARY KEY,
                            model TEXT NOT NULL,
                            result_json TEXT NOT NULL,
                            created_utc INTEGER NOT NULL);
                        CREATE TABLE IF NOT EXISTS jev_usage (
                            id INTEGER PRIMARY KEY AUTOINCREMENT,
                            at_utc INTEGER NOT NULL,
                            input_tokens INTEGER NOT NULL,
                            model TEXT NOT NULL);
                        CREATE INDEX IF NOT EXISTS ix_jev_usage_at ON jev_usage (at_utc);
                        """;
                    await cmd.ExecuteNonQueryAsync(cancellationToken);
                    _ready = true;
                }
            }
            finally
            {
                _init.Release();
            }
        }

        return conn;
    }
}

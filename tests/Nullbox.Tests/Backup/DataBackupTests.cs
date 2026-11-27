using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nullbox.Web.Backup;
using Nullbox.Web.Hosting;

namespace Nullbox.Tests.Backup;

public sealed class DataBackupTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 27, 3, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "nullbox-backup-tests", Guid.NewGuid().ToString("N"));

    private string Data => Path.Combine(_root, "data");

    private string Backups => Path.Combine(_root, "backups");

    public DataBackupTests()
    {
        Directory.CreateDirectory(Path.Combine(Data, "google"));
        Directory.CreateDirectory(Path.Combine(Data, "keys"));
        File.WriteAllText(Path.Combine(Data, "google", "token.json"), "{\"refresh_token\":\"r\"}");
        File.WriteAllText(Path.Combine(Data, "keys", "key-1.xml"), "<key/>");
        File.WriteAllText(Path.Combine(Data, "notes.txt"), "not backed up");
        Write("nullbox.db", "first");
        Write("hangfire.db", "jobs");
    }

    [Fact]
    public async Task Snapshot_holds_every_database_and_the_token_and_key_files()
    {
        var result = await DataBackup.CreateAsync(Data, Backups, keep: 7, T0);

        Assert.Equal(Path.Combine(Backups, "nullbox-20261127T030000Z"), result.Directory);
        Assert.Equal(["hangfire.db", "nullbox.db"], result.Databases);
        Assert.Equal(["google/token.json", "keys/key-1.xml"], result.Files);
        Assert.Equal(["first"], Read(Path.Combine(result.Directory, "nullbox.db")));
        Assert.Equal(["jobs"], Read(Path.Combine(result.Directory, "hangfire.db")));
        Assert.False(File.Exists(Path.Combine(result.Directory, "notes.txt")));
        Assert.False(Directory.Exists(result.Directory + ".partial"));
    }

    [Fact]
    public async Task Snapshot_is_consistent_while_a_writer_holds_the_database_open_in_wal_mode()
    {
        await using var writer = Open(Path.Combine(Data, "nullbox.db"));
        Exec(writer, "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; INSERT INTO t VALUES ('second');");
        Assert.True(File.Exists(Path.Combine(Data, "nullbox.db-wal")));

        var result = await DataBackup.CreateAsync(Data, Backups, keep: 7, T0);

        Assert.Equal(["first", "second"], Read(Path.Combine(result.Directory, "nullbox.db")));
        Assert.False(File.Exists(Path.Combine(result.Directory, "nullbox.db-wal")));
    }

    [Fact]
    public async Task Snapshot_files_and_folders_are_owner_only()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await DataBackup.CreateAsync(Data, Backups, keep: 7, T0);

        const UnixFileMode dir = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        const UnixFileMode file = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        Assert.Equal(dir, File.GetUnixFileMode(result.Directory));
        Assert.Equal(dir, File.GetUnixFileMode(Path.Combine(result.Directory, "google")));
        Assert.Equal(file, File.GetUnixFileMode(Path.Combine(result.Directory, "google", "token.json")));
        Assert.Equal(file, File.GetUnixFileMode(Path.Combine(result.Directory, "nullbox.db")));
    }

    [Fact]
    public async Task Only_the_newest_snapshots_are_kept()
    {
        Directory.CreateDirectory(Path.Combine(Backups, "unrelated"));
        for (var day = 0; day < 4; day++)
        {
            await DataBackup.CreateAsync(Data, Backups, keep: 2, T0.AddDays(day));
        }

        var left = Directory.EnumerateDirectories(Backups).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["nullbox-20261129T030000Z", "nullbox-20261130T030000Z", "unrelated"], left);
    }

    [Fact]
    public async Task Backups_inside_the_data_directory_are_refused()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => DataBackup.CreateAsync(Data, Path.Combine(Data, "backups"), keep: 7, T0));
        await Assert.ThrowsAsync<InvalidOperationException>(() => DataBackup.CreateAsync(Data, Data, keep: 7, T0));
    }

    [Fact]
    public async Task Restore_puts_the_snapshot_back_and_drops_stale_wal_files()
    {
        var snapshot = (await DataBackup.CreateAsync(Data, Backups, keep: 7, T0)).Directory;
        await using (var later = Open(Path.Combine(Data, "nullbox.db")))
        {
            Exec(later, "DELETE FROM t; INSERT INTO t VALUES ('changed');");
        }

        File.WriteAllText(Path.Combine(Data, "google", "token.json"), "{\"refresh_token\":\"new\"}");
        File.WriteAllText(Path.Combine(Data, "nullbox.db-wal"), "stale");

        var restored = DataBackup.Restore(snapshot, Data);

        Assert.Contains("nullbox.db", restored);
        Assert.Contains("google/token.json", restored);
        Assert.Equal(["first"], Read(Path.Combine(Data, "nullbox.db")));
        Assert.Contains("\"r\"", File.ReadAllText(Path.Combine(Data, "google", "token.json")));
        Assert.False(File.Exists(Path.Combine(Data, "nullbox.db-wal")));
        Assert.True(File.Exists(Path.Combine(Data, "notes.txt")));
    }

    [Fact]
    public void Restore_refuses_a_folder_that_is_not_a_snapshot()
        => Assert.Throws<DirectoryNotFoundException>(() => DataBackup.Restore(Path.Combine(_root, "data"), Path.Combine(_root, "elsewhere")));

    [Fact]
    public async Task Cli_backs_up_and_restores_with_exit_codes()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var env = new Dictionary<string, string?> { ["NULLBOX_DATA_DIR"] = Data, ["NULLBOX_BACKUP_DIR"] = Backups };

        var code = await DataBackup.RunCliAsync(["--backup", "--keep", "3"], k => env.GetValueOrDefault(k), output, error, T0);

        Assert.Equal(0, code);
        Assert.Contains("2 databases (hangfire.db, nullbox.db), 2 files; pruned 0", output.ToString());

        var restoreTo = Path.Combine(_root, "restored");
        code = await DataBackup.RunCliAsync(["--backup", "--restore", Path.Combine(Backups, "nullbox-20261127T030000Z"), "--data", restoreTo], k => env.GetValueOrDefault(k), output, error, T0);

        Assert.Equal(0, code);
        Assert.Equal(["first"], Read(Path.Combine(restoreTo, "nullbox.db")));
    }

    [Theory]
    [InlineData("--keep", "0")]
    [InlineData("--keep", "many")]
    [InlineData("--bogus", "x")]
    public async Task Cli_rejects_bad_arguments(string flag, string value)
    {
        var error = new StringWriter();
        var env = new Dictionary<string, string?> { ["NULLBOX_DATA_DIR"] = Data, ["NULLBOX_BACKUP_DIR"] = Backups };

        var code = await DataBackup.RunCliAsync(["--backup", flag, value], k => env.GetValueOrDefault(k), TextWriter.Null, error, T0);

        Assert.Equal(64, code);
        Assert.False(Directory.Exists(Backups));
    }

    [Fact]
    public async Task Cli_needs_a_destination()
    {
        var error = new StringWriter();

        var code = await DataBackup.RunCliAsync(["--backup"], _ => null, TextWriter.Null, error, T0);

        Assert.Equal(64, code);
        Assert.Contains("NULLBOX_BACKUP_DIR", error.ToString());
    }

    [Fact]
    public void Forwarded_headers_are_off_by_default_and_reject_bad_proxy_addresses()
    {
        var off = new ConfigurationBuilder().Build();
        Assert.False(ProxyHosting.IsEnabled(off));

        var bad = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Nullbox:ForwardedHeaders:Enabled"] = "true",
            ["Nullbox:ForwardedHeaders:KnownProxies:0"] = "caddy",
        }).Build();
        using var services = new ServiceCollection().AddNullboxForwardedHeaders(bad).BuildServiceProvider();

        Assert.Throws<InvalidOperationException>(() => services.GetRequiredService<Microsoft.Extensions.Options.IOptions<ForwardedHeadersOptions>>().Value);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private void Write(string file, string value)
    {
        using var conn = Open(Path.Combine(Data, file));
        Exec(conn, $"CREATE TABLE t (v TEXT); INSERT INTO t VALUES ('{value}');");
    }

    private static SqliteConnection Open(string path)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        conn.Open();
        return conn;
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static List<string> Read(string path)
    {
        using var conn = Open(path);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT v FROM t ORDER BY rowid";
        using var reader = cmd.ExecuteReader();
        var values = new List<string>();
        while (reader.Read())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }
}

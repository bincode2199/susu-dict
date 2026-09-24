using System.Globalization;
using System.Text;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Susu.Abstractions;

namespace Susu.Storage;

public sealed class DatabaseVersionException(int found, int supported, string? compatibleBackup)
    : Exception($"susu.db has schema {found}, this Su-Su supports {supported}." + (compatibleBackup is null ? "" : $" A schema {supported} backup exists: {Path.GetFileName(compatibleBackup)}."))
{
    public int Found { get; } = found;
    public int Supported { get; } = supported;
    public string? CompatibleBackup { get; } = compatibleBackup;
}

/// <summary>
/// susu.db (ARCHITECTURE 8.2): one writer thread owns the write connection and runs every write in a short
/// transaction; readers use their own connections under WAL. foreign_keys=ON, bounded busy_timeout, all SQL
/// parameterized. Migrations only move forward and are preceded by an online backup (safe while WAL is active).
/// </summary>
public sealed class Database : IDisposable
{
    public const int SchemaVersion = 1;
    private const int BusyTimeoutMs = 5000;

    /// <summary>Forward-only migrations. Vocabulary tables arrive as version 2 in F15 (entry point reserved here).</summary>
    public static readonly IReadOnlyDictionary<int, string> Migrations = new Dictionary<int, string>
    {
        [1] = """
            CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE window_state (window_kind TEXT PRIMARY KEY, monitor_hint TEXT NOT NULL, x INTEGER NOT NULL, y INTEGER NOT NULL, dpi INTEGER NOT NULL);
            CREATE TABLE usage (provider TEXT NOT NULL, account TEXT NOT NULL, period TEXT NOT NULL, metric TEXT NOT NULL, count INTEGER NOT NULL,
                PRIMARY KEY (provider, account, period, metric));
            CREATE TABLE usage_events (attempt_id TEXT NOT NULL, metric TEXT NOT NULL, provider TEXT NOT NULL, units INTEGER NOT NULL, outcome TEXT NOT NULL, day TEXT NOT NULL,
                UNIQUE (attempt_id, metric));
            CREATE INDEX usage_events_day ON usage_events(day);
            CREATE TABLE plugin_installations (installation_id TEXT PRIMARY KEY, package_id TEXT NOT NULL, version TEXT NOT NULL, signer TEXT NOT NULL,
                hash TEXT NOT NULL, active INTEGER NOT NULL CHECK (active IN (0, 1)));
            CREATE UNIQUE INDEX plugin_installations_active ON plugin_installations(package_id) WHERE active = 1;
            CREATE TABLE plugin_kv (installation_id TEXT NOT NULL REFERENCES plugin_installations(installation_id) ON DELETE CASCADE,
                namespace TEXT NOT NULL, key TEXT NOT NULL, value_json TEXT NOT NULL, bytes INTEGER NOT NULL,
                PRIMARY KEY (installation_id, namespace, key));
            """,
    };

    private readonly string connectionString;
    private readonly SqliteConnection writeConnection;
    private readonly Channel<Action<SqliteConnection>> queue = Channel.CreateBounded<Action<SqliteConnection>>(new BoundedChannelOptions(4096) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Thread writer;

    public string Path { get; }
    public int Version { get; }
    public string? MigrationBackup { get; }

    private Database(string path, SqliteConnection connection, int version, string? backup)
    {
        Path = path;
        connectionString = connection.ConnectionString;
        writeConnection = connection;
        Version = version;
        MigrationBackup = backup;
        writer = new Thread(WriterLoop) { IsBackground = true, Name = "susu-db-writer" };
        writer.Start();
    }

    public static Database Open(string path, IFaultPoint? faults = null, int targetVersion = SchemaVersion, IReadOnlyDictionary<int, string>? migrations = null)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false, DefaultTimeout = BusyTimeoutMs / 1000 }.ToString());
        try
        {
            connection.Open();
            Configure(connection);
            Exec(connection, "PRAGMA journal_mode=WAL;");
            int version = ReadVersion(connection);
            if (version > targetVersion)
            {
                string candidate = BackupPath(path, targetVersion);
                throw new DatabaseVersionException(version, targetVersion, File.Exists(candidate) ? candidate : null);
            }
            string? backup = null;
            if (version > 0 && version < targetVersion)
            {
                backup = BackupPath(path, version);
                BackupTo(connection, backup);
                faults?.Hit("migrate:backed-up");
            }
            for (int next = version + 1; next <= targetVersion; next++)
            {
                using var tx = connection.BeginTransaction();
                Exec(connection, (migrations ?? Migrations)[next], tx);
                Exec(connection, "INSERT INTO meta(key, value) VALUES ('schemaVersion', $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value;", tx, ("$v", next.ToString(CultureInfo.InvariantCulture)));
                faults?.Hit($"migrate:{next}");
                tx.Commit();
            }
            return new Database(path, connection, targetVersion, backup);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>Backup file an older app can restore when the database was migrated past its version (DATA03).</summary>
    public static string BackupPath(string databasePath, int version) => $"{databasePath}.v{version}.bak";

    /// <summary>Replaces the database with a consistent backup (e.g. after rolling back to an older app).</summary>
    public static void RestoreBackup(string backupPath, string databasePath)
    {
        using var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backupPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        using var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        source.Open();
        target.Open();
        source.BackupDatabase(target);
        Exec(target, "PRAGMA wal_checkpoint(TRUNCATE);");
    }

    /// <summary>Online backup through the SQLite backup API; consistent even with an active WAL.</summary>
    public Task BackupToAsync(string destination) => WriteAsync(w => { BackupTo(w.Connection, destination); return true; }, transactional: false);

    private static void BackupTo(SqliteConnection connection, string destination)
    {
        if (File.Exists(destination)) File.Delete(destination);
        using var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destination, Pooling = false }.ToString());
        target.Open();
        connection.BackupDatabase(target);
    }

    /// <summary>Queues a write on the single writer; by default it runs in one short transaction that rolls back on any exception.</summary>
    public Task<T> WriteAsync<T>(Func<WriteContext, T> work, bool transactional = true)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Run(SqliteConnection connection)
        {
            SqliteTransaction? tx = null;
            try
            {
                tx = transactional ? connection.BeginTransaction() : null;
                var result = work(new WriteContext(connection, tx));
                tx?.Commit();
                done.TrySetResult(result);
            }
            catch (Exception e)
            {
                try { tx?.Rollback(); } catch (SqliteException) { } catch (InvalidOperationException) { }
                done.TrySetException(e);
            }
            finally { tx?.Dispose(); }
        }
        if (queue.Writer.TryWrite(Run)) return done.Task;
        return Enqueue(Run, done.Task);
    }

    private async Task<T> Enqueue<T>(Action<SqliteConnection> run, Task<T> result)
    {
        try { await queue.Writer.WriteAsync(run).ConfigureAwait(false); }
        catch (ChannelClosedException) { throw new ObjectDisposedException(nameof(Database)); }
        return await result.ConfigureAwait(false);
    }

    public T Write<T>(Func<WriteContext, T> work) => WriteAsync(work).GetAwaiter().GetResult();

    /// <summary>Reads on a separate connection (WAL readers never block the writer).</summary>
    public T Read<T>(Func<SqliteConnection, T> work)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        Configure(connection);
        return work(connection);
    }

    /// <summary>Completes after every write queued before it.</summary>
    public Task FlushAsync() => WriteAsync(_ => true, transactional: false);

    private void WriterLoop()
    {
        var reader = queue.Reader;
        while (reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
            while (reader.TryRead(out var work)) work(writeConnection);
    }

    public void Dispose()
    {
        queue.Writer.TryComplete();
        if (Thread.CurrentThread != writer) writer.Join(TimeSpan.FromSeconds(10));
        try { Exec(writeConnection, "PRAGMA wal_checkpoint(TRUNCATE);"); } catch (SqliteException) { }
        writeConnection.Dispose();
    }

    private static void Configure(SqliteConnection connection)
        => Exec(connection, $"PRAGMA foreign_keys=ON; PRAGMA busy_timeout={BusyTimeoutMs}; PRAGMA synchronous=NORMAL;");

    private static int ReadVersion(SqliteConnection connection)
    {
        if (Scalar(connection, null, "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='meta';") is not long n || n == 0) return 0;
        return Scalar(connection, null, "SELECT value FROM meta WHERE key='schemaVersion';") is string s ? int.Parse(s, CultureInfo.InvariantCulture) : 0;
    }

    internal static int Exec(SqliteConnection connection, string sql, SqliteTransaction? tx = null, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, tx, sql, parameters);
        return command.ExecuteNonQuery();
    }

    internal static object? Scalar(SqliteConnection connection, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, tx, sql, parameters);
        return command.ExecuteScalar();
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? tx, string sql, (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = tx;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
}

/// <summary>The writer's connection and current transaction; all SQL is parameterized.</summary>
public sealed class WriteContext(SqliteConnection connection, SqliteTransaction? transaction)
{
    public SqliteConnection Connection { get; } = connection;
    public int Exec(string sql, params (string Name, object? Value)[] parameters) => Database.Exec(Connection, sql, transaction, parameters);
    public object? Scalar(string sql, params (string Name, object? Value)[] parameters) => Database.Scalar(Connection, transaction, sql, parameters);
}

public sealed class WindowStateRepository(Database db) : IWindowStateStore
{
    public WindowPlacement? Get(string windowKind) => db.Read(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT monitor_hint, x, y, dpi FROM window_state WHERE window_kind = $k;";
        command.Parameters.AddWithValue("$k", windowKind);
        using var reader = command.ExecuteReader();
        return reader.Read() ? new WindowPlacement(windowKind, reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3)) : null;
    });

    public void Save(WindowPlacement p) => db.Write(w => w.Exec(
        "INSERT INTO window_state(window_kind, monitor_hint, x, y, dpi) VALUES ($k, $m, $x, $y, $d) ON CONFLICT(window_kind) DO UPDATE SET monitor_hint=excluded.monitor_hint, x=excluded.x, y=excluded.y, dpi=excluded.dpi;",
        ("$k", p.WindowKind), ("$m", p.MonitorHint), ("$x", p.X), ("$y", p.Y), ("$d", p.Dpi)));
}

/// <summary>
/// Usage counts (DATA04): each (attempt, metric) event is stored once and only a first insert adds to the
/// monthly aggregate, so duplicate events never double count. These are local counts, not vendor balances.
/// </summary>
public sealed class UsageRepository(Database db, IClock clock) : IUsageSink
{
    public void Record(string serviceId, string attemptId, string metric, long units, string outcome)
        => _ = RecordAsync(serviceId, attemptId, metric, units, outcome);

    public Task<bool> RecordAsync(string serviceId, string attemptId, string metric, long units, string outcome, string account = "")
    {
        var now = clock.UtcNow;
        return db.WriteAsync(w =>
        {
            int inserted = w.Exec(
                "INSERT OR IGNORE INTO usage_events(attempt_id, metric, provider, units, outcome, day) VALUES ($a, $m, $p, $u, $o, $d);",
                ("$a", attemptId), ("$m", metric), ("$p", serviceId), ("$u", units), ("$o", outcome), ("$d", now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
            if (inserted == 0) return false;
            w.Exec(
                "INSERT INTO usage(provider, account, period, metric, count) VALUES ($p, $acc, $per, $m, $u) ON CONFLICT(provider, account, period, metric) DO UPDATE SET count = count + excluded.count;",
                ("$p", serviceId), ("$acc", account), ("$per", now.ToString("yyyy-MM", CultureInfo.InvariantCulture)), ("$m", metric), ("$u", units));
            return true;
        });
    }

    public long Count(string serviceId, string metric, string period, string account = "") => db.Read(connection =>
        Database.Scalar(connection, null, "SELECT count FROM usage WHERE provider=$p AND account=$a AND period=$per AND metric=$m;", ("$p", serviceId), ("$a", account), ("$per", period), ("$m", metric)) is long n ? n : 0);

    /// <summary>Dedup rows are kept 7 days after aggregation (ARCHITECTURE 8.2).</summary>
    public Task<int> PurgeEventsAsync() => db.WriteAsync(w =>
        w.Exec("DELETE FROM usage_events WHERE day < $d;", ("$d", clock.UtcNow.AddDays(-7).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))));
}

public sealed class PluginKvQuotaException(string message) : Exception(message);

/// <summary>
/// plugin_kv (DATA04): the host binds installation and namespace; a plugin cannot name another package's
/// data. Per-installation default quota 1 MiB, total 32 MiB, counted as UTF-8 bytes of key + value.
/// </summary>
public sealed class PluginKvRepository(Database db, long perInstallationBytes = 1 << 20, long totalBytes = 32 << 20) : IPluginKv
{
    public const string StoreNamespace = "store";
    public const int MaxKeyChars = 256;

    public void RegisterInstallation(string installationId, string packageId, string version, string signer, string hash, bool active = true)
        => db.Write(w => w.Exec(
            "INSERT INTO plugin_installations(installation_id, package_id, version, signer, hash, active) VALUES ($i, $p, $v, $s, $h, $a) ON CONFLICT(installation_id) DO UPDATE SET active=excluded.active;",
            ("$i", installationId), ("$p", packageId), ("$v", version), ("$s", signer), ("$h", hash), ("$a", active ? 1 : 0)));

    public string? Get(string installationId, string key) => db.Read(connection =>
        Database.Scalar(connection, null, "SELECT value_json FROM plugin_kv WHERE installation_id=$i AND namespace=$n AND key=$k;", ("$i", installationId), ("$n", StoreNamespace), ("$k", key)) as string);

    public void Set(string installationId, string key, string valueJson)
    {
        if (key.Length is 0 or > MaxKeyChars) throw new ArgumentException($"key must be 1..{MaxKeyChars} characters");
        long bytes = Encoding.UTF8.GetByteCount(key) + Encoding.UTF8.GetByteCount(valueJson);
        db.Write(w =>
        {
            (string, object?)[] p = [("$i", installationId), ("$n", StoreNamespace), ("$k", key)];
            long mine = (long)w.Scalar("SELECT coalesce(sum(bytes), 0) FROM plugin_kv WHERE installation_id=$i AND NOT (namespace=$n AND key=$k);", p)!;
            if (mine + bytes > perInstallationBytes) throw new PluginKvQuotaException($"store quota {perInstallationBytes} bytes exceeded");
            long all = (long)w.Scalar("SELECT coalesce(sum(bytes), 0) FROM plugin_kv WHERE NOT (installation_id=$i AND namespace=$n AND key=$k);", p)!;
            if (all + bytes > totalBytes) throw new PluginKvQuotaException($"total store quota {totalBytes} bytes exceeded");
            return w.Exec(
                "INSERT INTO plugin_kv(installation_id, namespace, key, value_json, bytes) VALUES ($i, $n, $k, $v, $b) ON CONFLICT(installation_id, namespace, key) DO UPDATE SET value_json=excluded.value_json, bytes=excluded.bytes;",
                ("$i", installationId), ("$n", StoreNamespace), ("$k", key), ("$v", valueJson), ("$b", bytes));
        });
    }

    public bool Delete(string installationId, string key) => db.Write(w =>
        w.Exec("DELETE FROM plugin_kv WHERE installation_id=$i AND namespace=$n AND key=$k;", ("$i", installationId), ("$n", StoreNamespace), ("$k", key)) > 0);

    public long BytesUsed(string installationId) => db.Read(connection =>
        (long)Database.Scalar(connection, null, "SELECT coalesce(sum(bytes), 0) FROM plugin_kv WHERE installation_id=$i;", ("$i", installationId))!);
}

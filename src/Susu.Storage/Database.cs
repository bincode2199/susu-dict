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

    /// <summary>What the user is shown (F17.3): what happened, that the data was not touched, and what to do.</summary>
    public string UserMessage(bool chinese)
    {
        string file = CompatibleBackup is null ? "" : Path.GetFileName(CompatibleBackup);
        return chinese
            ? $"数据库版本（{Found}）比这个 Su-Su 能读的版本（{Supported}）新，可能是运行过更新的版本。数据没有被改动。请安装新版 Su-Su。" + (file.Length == 0 ? "" : $"如果一定要继续用旧版：退出后把 {file} 恢复为 susu.db（升级之后新增的数据不在其中）。详见恢复说明。")
            : $"The database (version {Found}) is newer than this Su-Su can read (version {Supported}); a newer version has probably run on this data. Nothing was changed. Install the newer Su-Su." + (file.Length == 0 ? "" : $" To keep using this older version, quit and restore {file} as susu.db (anything saved after the upgrade is not in it). See the recovery notes.");
    }
}

/// <summary>
/// susu.db (ARCHITECTURE 8.2): one writer thread owns the write connection and runs every write in a short
/// transaction; readers use their own connections under WAL. foreign_keys=ON, bounded busy_timeout, all SQL
/// parameterized. Migrations only move forward and are preceded by an online backup (safe while WAL is active).
/// </summary>
public sealed class Database : IDisposable
{
    public const int SchemaVersion = 4;
    private const int BusyTimeoutMs = 5000;

    /// <summary>Forward-only migrations. Version 2 (F15.1) adds the vocabulary tables; version 3 (F15.2) adds the export target path for recovery.</summary>
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
        [2] = """
            CREATE TABLE vocab_entries (entry_id TEXT PRIMARY KEY, lang TEXT NOT NULL, normalized_text TEXT NOT NULL, display_text TEXT NOT NULL,
                content_json TEXT NOT NULL, revision INTEGER NOT NULL, created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL, deleted_at INTEGER,
                UNIQUE (lang, normalized_text));
            CREATE TABLE vocab_deliveries (entry_id TEXT NOT NULL REFERENCES vocab_entries(entry_id) ON DELETE CASCADE, target_instance_id TEXT NOT NULL,
                entry_revision INTEGER NOT NULL, operation_id TEXT NOT NULL, state TEXT NOT NULL, remote_id TEXT, attempts INTEGER NOT NULL, next_at INTEGER NOT NULL,
                UNIQUE (entry_id, target_instance_id, entry_revision));
            CREATE INDEX vocab_deliveries_queue ON vocab_deliveries(target_instance_id, state, next_at);
            CREATE TABLE vocab_exports (export_id TEXT PRIMARY KEY, format TEXT NOT NULL, file_hash TEXT NOT NULL, created_at INTEGER NOT NULL, outcome TEXT NOT NULL);
            CREATE TABLE vocab_export_items (export_id TEXT NOT NULL REFERENCES vocab_exports(export_id) ON DELETE CASCADE, entry_id TEXT NOT NULL,
                entry_revision INTEGER NOT NULL, PRIMARY KEY (export_id, entry_id, entry_revision));
            """,
        [3] = "ALTER TABLE vocab_exports ADD COLUMN path TEXT;",
        // F16.2: plugin storage is namespaced by package id, so an update (a new installation row) keeps its data and an uninstall can keep or drop it by choice.
        [4] = """
            CREATE TABLE plugin_kv_v4 (package_id TEXT NOT NULL, namespace TEXT NOT NULL, key TEXT NOT NULL, value_json TEXT NOT NULL, bytes INTEGER NOT NULL,
                PRIMARY KEY (package_id, namespace, key));
            INSERT OR REPLACE INTO plugin_kv_v4(package_id, namespace, key, value_json, bytes)
                SELECT i.package_id, k.namespace, k.key, k.value_json, k.bytes FROM plugin_kv k JOIN plugin_installations i ON i.installation_id = k.installation_id
                ORDER BY i.active, i.version;
            DROP TABLE plugin_kv;
            ALTER TABLE plugin_kv_v4 RENAME TO plugin_kv;
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
                BackupTo(connection, backup, faults);
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

    /// <summary>
    /// F18.2: a consistent standalone copy of a database file that no application has open (the updater runs after the app exited). Goes through the
    /// SQLite backup API so committed pages still in the -wal file are included; the copy appears under its final name only when complete.
    /// </summary>
    public static void CopyFile(string source, string destination, IFaultPoint? faults = null)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = source, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        BackupTo(connection, destination, faults);
    }

    /// <summary>F18.2: puts a copy made by <see cref="CopyFile"/> back as the database file; stale -wal and -shm files of the replaced database are removed first.</summary>
    public static void ReplaceFileWithCopy(string copy, string databasePath)
    {
        string temp = databasePath + ".restore.tmp";
        TryDeleteFile(temp);
        try
        {
            File.Copy(copy, temp, overwrite: true);
            TryDeleteFile(databasePath + "-wal");
            TryDeleteFile(databasePath + "-shm");
            File.Move(temp, databasePath, overwrite: true);
        }
        finally { TryDeleteFile(temp); }
    }

    /// <summary>F18.2: the schema version stored in a database file, 0 when there is none; opens read-only and migrates nothing.</summary>
    public static int PeekVersion(string path)
    {
        if (!File.Exists(path)) return 0;
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        return ReadVersion(connection);
    }

    /// <summary>Online backup through the SQLite backup API; consistent even with an active WAL.</summary>
    public Task BackupToAsync(string destination) => WriteAsync(w => { BackupTo(w.Connection, destination); return true; }, transactional: false);

    /// <summary>
    /// Writes the copy beside the destination and moves it into place only when complete, so a failure (disk full, power loss) never leaves a partial
    /// file under the name an older app would restore from, and a good earlier copy survives a failed new one.
    /// </summary>
    private static void BackupTo(SqliteConnection connection, string destination, IFaultPoint? faults = null)
    {
        string temp = destination + ".tmp";
        TryDeleteFile(temp);
        try
        {
            using (var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = temp, Pooling = false }.ToString()))
            {
                target.Open();
                connection.BackupDatabase(target);
                faults?.Hit("migrate:backup-writing");
            }
            File.Move(temp, destination, overwrite: true);
        }
        catch
        {
            TryDeleteFile(temp);
            throw;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
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
    public SqliteTransaction? Transaction { get; } = transaction;
    public int Exec(string sql, params (string Name, object? Value)[] parameters) => Database.Exec(Connection, sql, Transaction, parameters);
    public object? Scalar(string sql, params (string Name, object? Value)[] parameters) => Database.Scalar(Connection, Transaction, sql, parameters);
}

/// <summary>
/// window_state (PLAN 1.4.1): positions are read once into memory and written through the single writer
/// without waiting, so the UI thread never blocks on SQL (ARCHITECTURE 4).
/// </summary>
public sealed class WindowStateRepository : IWindowStateStore
{
    private readonly Database db;
    private readonly Dictionary<string, WindowPlacement> cache = new(StringComparer.Ordinal);
    private readonly object gate = new();

    public WindowStateRepository(Database db)
    {
        this.db = db;
        db.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT window_kind, monitor_hint, x, y, dpi FROM window_state;";
            using var reader = command.ExecuteReader();
            while (reader.Read()) cache[reader.GetString(0)] = new WindowPlacement(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4));
            return true;
        });
    }

    public WindowPlacement? Get(string windowKind) { lock (gate) return cache.TryGetValue(windowKind, out var p) ? p : null; }

    public void Save(WindowPlacement p)
    {
        lock (gate) cache[p.WindowKind] = p;
        _ = db.WriteAsync(w => w.Exec(
            "INSERT INTO window_state(window_kind, monitor_hint, x, y, dpi) VALUES ($k, $m, $x, $y, $d) ON CONFLICT(window_kind) DO UPDATE SET monitor_hint=excluded.monitor_hint, x=excluded.x, y=excluded.y, dpi=excluded.dpi;",
            ("$k", p.WindowKind), ("$m", p.MonitorHint), ("$x", p.X), ("$y", p.Y), ("$d", p.Dpi)));
    }
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
/// plugin_kv (DATA04, F16.2): the host binds the package id and the namespace; a plugin cannot name another package's data. Data belongs to the
/// package id, so it survives updates and is removed only by <see cref="DeletePackage"/> (an explicit choice at uninstall). Per-package default
/// quota 1 MiB, total 32 MiB, counted as UTF-8 bytes of key + value.
/// </summary>
public sealed class PluginKvRepository(Database db, long perInstallationBytes = 1 << 20, long totalBytes = 32 << 20) : IPluginKv
{
    public const string StoreNamespace = "store";
    public const int MaxKeyChars = 256;

    public string? Get(string packageId, string key) => db.Read(connection =>
        Database.Scalar(connection, null, "SELECT value_json FROM plugin_kv WHERE package_id=$i AND namespace=$n AND key=$k;", ("$i", packageId), ("$n", StoreNamespace), ("$k", key)) as string);

    public void Set(string packageId, string key, string valueJson)
    {
        if (key.Length is 0 or > MaxKeyChars) throw new ArgumentException($"key must be 1..{MaxKeyChars} characters");
        long bytes = Encoding.UTF8.GetByteCount(key) + Encoding.UTF8.GetByteCount(valueJson);
        db.Write(w =>
        {
            (string, object?)[] p = [("$i", packageId), ("$n", StoreNamespace), ("$k", key)];
            long mine = (long)w.Scalar("SELECT coalesce(sum(bytes), 0) FROM plugin_kv WHERE package_id=$i AND NOT (namespace=$n AND key=$k);", p)!;
            if (mine + bytes > perInstallationBytes) throw new PluginKvQuotaException($"store quota {perInstallationBytes} bytes exceeded");
            long all = (long)w.Scalar("SELECT coalesce(sum(bytes), 0) FROM plugin_kv WHERE NOT (package_id=$i AND namespace=$n AND key=$k);", p)!;
            if (all + bytes > totalBytes) throw new PluginKvQuotaException($"total store quota {totalBytes} bytes exceeded");
            return w.Exec(
                "INSERT INTO plugin_kv(package_id, namespace, key, value_json, bytes) VALUES ($i, $n, $k, $v, $b) ON CONFLICT(package_id, namespace, key) DO UPDATE SET value_json=excluded.value_json, bytes=excluded.bytes;",
                ("$i", packageId), ("$n", StoreNamespace), ("$k", key), ("$v", valueJson), ("$b", bytes));
        });
    }

    public bool Delete(string packageId, string key) => db.Write(w =>
        w.Exec("DELETE FROM plugin_kv WHERE package_id=$i AND namespace=$n AND key=$k;", ("$i", packageId), ("$n", StoreNamespace), ("$k", key)) > 0);

    public long BytesUsed(string packageId) => db.Read(connection =>
        (long)Database.Scalar(connection, null, "SELECT coalesce(sum(bytes), 0) FROM plugin_kv WHERE package_id=$i;", ("$i", packageId))!);

    /// <summary>Removes everything a package stored; returns the number of keys removed. Called only when the user chose to remove the data at uninstall.</summary>
    public int DeletePackage(string packageId) => db.Write(w => w.Exec("DELETE FROM plugin_kv WHERE package_id=$i;", ("$i", packageId)));
}

namespace Susu.Storage;

/// <summary>One row of plugin_installations: a package version that was activated at some point.</summary>
public sealed record InstallationRecord(string InstallationId, string PackageId, string Version, string Signer, string Hash, bool Active);

/// <summary>
/// plugin_installations as the active-version pointer (ARCHITECTURE 8.1, F16.1). One row per package version; the unique partial index allows
/// at most one active row per package, so <see cref="Activate"/> is the atomic switch: it deactivates the old row and activates the new one in
/// one transaction. Rows are never deleted here (plugin_kv cascades from them), so data survives an uninstall until a data-removal option exists.
/// </summary>
public sealed class PluginInstallationRepository(Database db)
{
    public static string IdFor(string packageId, string version) => packageId + "@" + version;

    public void Activate(string packageId, string version, string signer, string hash)
        => db.Write(w =>
        {
            w.Exec("UPDATE plugin_installations SET active=0 WHERE package_id=$p;", ("$p", packageId));
            return w.Exec(
                "INSERT INTO plugin_installations(installation_id, package_id, version, signer, hash, active) VALUES ($i, $p, $v, $s, $h, 1) " +
                "ON CONFLICT(installation_id) DO UPDATE SET active=1, signer=excluded.signer, hash=excluded.hash;",
                ("$i", IdFor(packageId, version)), ("$p", packageId), ("$v", version), ("$s", signer), ("$h", hash));
        });

    public void Deactivate(string packageId)
        => db.Write(w => w.Exec("UPDATE plugin_installations SET active=0 WHERE package_id=$p;", ("$p", packageId)));

    public IReadOnlyList<InstallationRecord> All() => Query("SELECT installation_id, package_id, version, signer, hash, active FROM plugin_installations ORDER BY package_id, version;");

    public IReadOnlyList<InstallationRecord> ActiveRecords() => [.. All().Where(r => r.Active)];

    public InstallationRecord? Active(string packageId) => All().FirstOrDefault(r => r.Active && r.PackageId == packageId);

    private IReadOnlyList<InstallationRecord> Query(string sql) => db.Read(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<InstallationRecord>();
        while (reader.Read())
            rows.Add(new InstallationRecord(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetInt64(5) == 1));
        return rows;
    });
}

using Microsoft.Data.Sqlite;
using NSec.Cryptography;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

internal static class LibraryProbes
{
    internal static void Sqlite()
    {
        using var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON; CREATE TABLE probe(id INTEGER PRIMARY KEY, value TEXT NOT NULL); INSERT INTO probe(value) VALUES($value); SELECT value FROM probe;";
        command.Parameters.AddWithValue("$value", "中文 ' synthetic");
        if ((string?)command.ExecuteScalar() != "中文 ' synthetic") throw new InvalidOperationException("SQLite parameterized round-trip failed.");
        using var backup = new SqliteConnection("Data Source=:memory:");
        backup.Open();
        db.BackupDatabase(backup);
        using var query = backup.CreateCommand();
        query.CommandText = "SELECT COUNT(*) FROM probe";
        if ((long)query.ExecuteScalar()! != 1) throw new InvalidOperationException("SQLite backup mismatch.");
    }

    internal static void Yaml()
    {
        var parser = new Parser(new StringReader("schemaVersion: 1\nlanguage: zh-Hans\n"));
        var scalars = new List<string>();
        while (parser.MoveNext()) if (parser.Current is Scalar scalar) scalars.Add(scalar.Value);
        if (!scalars.SequenceEqual(new[] { "schemaVersion", "1", "language", "zh-Hans" })) throw new InvalidOperationException("YAML low-level parser mismatch.");
    }

    // RFC 8032 section 7.1, test vector 1: empty message.
    internal static void Ed25519()
    {
        byte[] publicBytes = Convert.FromHexString("d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a");
        byte[] signature = Convert.FromHexString("e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e06522490155" + "5fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b");
        var algorithm = SignatureAlgorithm.Ed25519;
        var key = PublicKey.Import(algorithm, publicBytes, KeyBlobFormat.RawPublicKey);
        if (!algorithm.Verify(key, [], signature)) throw new InvalidOperationException("RFC 8032 verification failed.");
        signature[0] ^= 1;
        if (algorithm.Verify(key, [], signature)) throw new InvalidOperationException("Invalid signature accepted.");
    }

}

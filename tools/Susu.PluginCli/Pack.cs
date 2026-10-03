using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Susu.Plugins;
using Susu.Plugins.Install;

namespace Susu.PluginCli;

/// <summary>
/// `susu-plugin keygen` and `susu-plugin pack`: the packaging half of the author workflow. A package (.susuext) is a zip with manifest.yaml at
/// its root; the optional `signature` entry is the detached Ed25519 signature the installer reads (PackageTrust, F16.1). `pack` builds the
/// zip with the same file list the sandbox test staged (author-only files stay out) and signs it when given a key.
/// </summary>
internal static class Pack
{
    /// <summary>Files that exist for the author only: never staged for the test run, never packed.</summary>
    public static bool IsAuthorOnly(string relativePath)
        => relativePath.Equals(CaseFile.DefaultName, StringComparison.OrdinalIgnoreCase)
        || relativePath.EndsWith(".test.json", StringComparison.OrdinalIgnoreCase) && !relativePath.Contains('/')
        || relativePath.StartsWith("tests/", StringComparison.OrdinalIgnoreCase);

    public static int Keygen(string file)
    {
        if (File.Exists(file)) { Console.Error.WriteLine($"keygen: {file} already exists; it is not overwritten"); return 2; }
        byte[] seed = RandomNumberGenerator.GetBytes(Ed25519.SeedBytes);
        File.WriteAllText(file, Convert.ToHexStringLower(seed) + "\n");
        Console.WriteLine($"keygen: wrote the private seed to {file} (keep it secret; never commit it)");
        Console.WriteLine($"keygen: key id (public, goes in the signature): {Convert.ToHexStringLower(Ed25519.PublicKey(seed))}");
        return 0;
    }

    public static int Run(string dir, string? outFile, string? keyFile)
    {
        string manifestPath = Path.Combine(dir, "manifest.yaml");
        if (!File.Exists(manifestPath)) { Console.Error.WriteLine("pack: manifest.yaml not found"); return 1; }
        var (manifest, issues) = PackageManifest.Parse(File.ReadAllText(manifestPath));
        var problems = SafePackage.Validate(dir);
        if (manifest is null || issues.Count > 0 || problems.Count > 0)
        {
            foreach (var issue in issues) Console.Error.WriteLine(issue);
            foreach (var issue in problems) Console.Error.WriteLine(issue);
            Console.Error.WriteLine("pack: the package does not pass 'check'; nothing was written");
            return 1;
        }

        byte[]? seed = null;
        if (keyFile is not null)
        {
            try { seed = Convert.FromHexString(File.ReadAllText(keyFile).Trim()); }
            catch (Exception e) when (e is FormatException or IOException) { Console.Error.WriteLine($"pack: cannot read the key file: {e.Message}"); return 2; }
            if (seed.Length != Ed25519.SeedBytes) { Console.Error.WriteLine("pack: the key file must hold a 32-byte seed as 64 hex characters (see 'susu-plugin keygen')"); return 2; }
        }

        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        try
        {
            foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(dir, file).Replace('\\', '/');
                if (IsAuthorOnly(relative) || relative == PackageTrust.SignatureFileName) continue; // a stale signature never survives a repack
                files[relative] = File.ReadAllBytes(file);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Console.Error.WriteLine($"pack: cannot read the package folder: {e.Message}; nothing was written");
            return 1;
        }
        if (seed is not null)
        {
            // Same list PackageTrust.Verify rebuilds: every file except `signature` (the manifest included), sorted by path.
            var hashes = files.Select(f => (f.Key, Convert.ToHexStringLower(SHA256.HashData(f.Value)))).OrderBy(h => h.Key, StringComparer.Ordinal).ToList<(string Path, string Sha256Hex)>();
            byte[] signature = Ed25519.Sign(seed, PackageTrust.SignedBytes(files["manifest.yaml"], hashes));
            string keyId = Convert.ToHexStringLower(Ed25519.PublicKey(seed));
            files[PackageTrust.SignatureFileName] = Encoding.UTF8.GetBytes($"keyId: {keyId}\nalgorithm: ed25519\nsignature: {Convert.ToBase64String(signature)}\n");
        }

        outFile ??= manifest.Version is null ? $"{manifest.Id}.susuext" : $"{manifest.Id}-{manifest.Version}.susuext";
        outFile = Path.GetFullPath(outFile);
        try
        {
            if (File.Exists(outFile)) File.Delete(outFile);
            using var stream = File.Create(outFile);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
            foreach (var (name, bytes) in files)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                using var s = entry.Open();
                s.Write(bytes);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"pack: cannot write {outFile}: {e.Message}");
            return 2;
        }
        Console.WriteLine($"pack: {outFile} ({files.Count} files, {new FileInfo(outFile).Length} bytes){(seed is null ? ", unsigned" : ", signed")}");
        return 0;
    }
}

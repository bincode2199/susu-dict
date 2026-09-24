using Susu.ContractsGen;

// Writes protocol/generated/*.ts. With --check, exits 1 when committed files differ (CI drift check).
string root = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? Directory.GetCurrentDirectory();
bool check = args.Contains("--check");
string folder = Path.Combine(root, "protocol", "generated");
Directory.CreateDirectory(folder);
int drift = 0;
foreach (var (name, content) in TypeScriptGenerator.Generate())
{
    string path = Path.Combine(folder, name);
    string existing = File.Exists(path) ? File.ReadAllText(path).Replace("\r\n", "\n") : "";
    if (existing == content) continue;
    if (check) { Console.Error.WriteLine($"drift: {path}"); drift++; }
    else { File.WriteAllText(path, content); Console.WriteLine($"wrote {path}"); }
}
return drift == 0 ? 0 : 1;

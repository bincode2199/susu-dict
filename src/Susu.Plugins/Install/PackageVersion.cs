namespace Susu.Plugins.Install;

/// <summary>A plugin version: exactly major.minor.patch, digits only (no pre-release or build tags, so ordering has no ambiguity).</summary>
public readonly record struct PackageVersion(int Major, int Minor, int Patch) : IComparable<PackageVersion>
{
    public static bool TryParse(string? text, out PackageVersion version)
    {
        version = default;
        if (string.IsNullOrEmpty(text) || text.Length > 24) return false;
        var parts = text.Split('.');
        if (parts.Length != 3) return false;
        var numbers = new int[3];
        for (int i = 0; i < 3; i++)
        {
            if (parts[i].Length is 0 or > 6 || !parts[i].All(char.IsAsciiDigit) || (parts[i].Length > 1 && parts[i][0] == '0')) return false;
            numbers[i] = int.Parse(parts[i], System.Globalization.CultureInfo.InvariantCulture);
        }
        version = new PackageVersion(numbers[0], numbers[1], numbers[2]);
        return true;
    }

    public int CompareTo(PackageVersion other) => (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch));
    public static bool operator >(PackageVersion a, PackageVersion b) => a.CompareTo(b) > 0;
    public static bool operator <(PackageVersion a, PackageVersion b) => a.CompareTo(b) < 0;
    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}

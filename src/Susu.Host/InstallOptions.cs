using System.Text.Json;
using Susu.Storage;

namespace Susu.Host;

/// <summary>
/// F18.1: choices the installer made that belong in the user's settings (today only start-at-login). The installer cannot edit settings.yaml, which only
/// the application writes (revision and file hash checked), so it drops install-options.json next to the local data; the first start applies it through
/// the normal settings save and deletes the file. A damaged or unknown file is ignored and deleted, never a startup failure.
/// </summary>
internal static class InstallOptions
{
    public const string FileName = "install-options.json";

    /// <summary>Applies and removes the pending file. Returns the applied start-at-login value, or null when there was nothing to apply.</summary>
    public static bool? ApplyPending(AppPaths paths, ConfigService config)
    {
        string file = Path.Combine(paths.Local, FileName);
        if (!File.Exists(file)) return null;
        bool? value = null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("launchAtStartup", out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False)
                value = v.GetBoolean();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { }
        try { File.Delete(file); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        if (value is null) return null;
        var state = config.State;
        if (state.Effective.General.LaunchAtStartup == value) return value;
        var saved = config.Save(state.Effective with { General = state.Effective.General with { LaunchAtStartup = value.Value } }, state.Revision, state.FileHash);
        return saved.Status == Susu.Abstractions.SaveStatus.Saved ? value : null;
    }
}

using System.Globalization;

namespace Susu.Domain;

public enum ConfigFieldType { String, Integer, Number, Boolean }

/// <summary>
/// Where a field's choices come from at run time (PLAN 4.6 <c>x-susu.optionsSource</c>, ARCHITECTURE 3.1).
/// Method: the plugin's optional <c>options</c> function (models, decks, word books) or <c>voices</c> for TTS.
/// DependsOn: fields or secret names of the same instance whose change makes earlier results stale.
/// </summary>
public sealed record OptionsSource(string Method, IReadOnlyList<string> DependsOn)
{
    public const string OptionsMethod = "options", VoicesMethod = "voices";
}

/// <summary>
/// One property of a package's <c>config</c> schema (PLAN 4.6: JSON Schema semantics plus the <c>x-susu</c>
/// extension), the host renders a settings control from it. Values are stored as strings in
/// <see cref="InstanceSettings.Config"/>; <see cref="ConfigSchema.Check"/> is the host-side check a page cannot skip.
/// </summary>
public sealed record ConfigField(
    string Name,
    ConfigFieldType Type,
    string? Title = null,
    string? Default = null,
    IReadOnlyList<string>? Enum = null,
    string? Format = null,
    double? Minimum = null,
    double? Maximum = null,
    string? Group = null,
    string? Placeholder = null,
    string? Help = null,
    string? ShowWhenField = null,
    string? ShowWhenEquals = null,
    bool Secret = false,
    OptionsSource? Options = null);

public static class ConfigSchema
{
    public const int MaxStringLength = 2048;

    /// <summary>Null when <paramref name="value"/> is acceptable for <paramref name="field"/>, else a short reason code.</summary>
    public static string? Check(ConfigField field, string value)
    {
        if (field.Secret) return "secret-field";
        if (value.Length > MaxStringLength || value.Any(char.IsControl)) return "format";
        switch (field.Type)
        {
            case ConfigFieldType.Boolean:
                return value is "true" or "false" ? null : "type";
            case ConfigFieldType.Integer:
                if (!long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long whole)) return "type";
                return InRange(field, whole);
            case ConfigFieldType.Number:
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || !double.IsFinite(number)) return "type";
                return InRange(field, number);
        }
        // A dynamic field's list is advisory: the stored value may be one the list no longer shows (CFG02).
        if (field.Enum is { Count: > 0 } choices && field.Options is null && !choices.Contains(value, StringComparer.Ordinal)) return "enum";
        if (field.Format == "uri" && !IsServiceAddress(value)) return "uri";
        return null;
    }

    private static string? InRange(ConfigField field, double value)
        => field.Minimum is { } min && value < min || field.Maximum is { } max && value > max ? "range" : null;

    /// <summary>
    /// A service address the user may point a package at: absolute https (http only for loopback, e.g. a
    /// local model server), no userinfo, query or fragment. The origin still goes through grant confirmation.
    /// </summary>
    public static bool IsServiceAddress(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return false;
        if (uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0) return false;
        return uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback;
    }

    /// <summary>Model parameters (ARCHITECTURE 3.1): only a package whose schema declares one may receive it.</summary>
    public static readonly IReadOnlyList<string> ModelParameters = ["temperature"];

    /// <summary>
    /// The instance config a plugin call carries (F07.3, CFG04). A model parameter is dropped unless the package's
    /// schema declares it and the stored value still passes <see cref="Check"/> (a hand-edited settings file can
    /// hold anything); every other key passes through as before (host-managed keys such as DeepL's plan).
    /// </summary>
    public static IReadOnlyDictionary<string, string> ForPlugin(IReadOnlyDictionary<string, string> config, IReadOnlyList<ConfigField>? schema)
    {
        if (!config.Keys.Any(ModelParameters.Contains)) return config;
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in config)
        {
            if (ModelParameters.Contains(name) && (schema?.FirstOrDefault(f => f.Name == name) is not { } field || Check(field, value) is not null)) continue;
            result[name] = value;
        }
        return result;
    }
}

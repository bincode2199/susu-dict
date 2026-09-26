using System.Text;
using Susu.Contracts;

namespace Susu.Domain;

/// <summary>
/// A built-in OCR package (DEV-PLAN 5: P-O01 Tencent OCR, P-O02 Simple LaTeX; F11.2). Its secrets are released only under
/// grants for its exact origin (PLAN 4.5.4); Tencent OCR shares the Tencent Cloud account with TMT/TTS through a separate
/// grant for <c>ocr.tencentcloudapi.com</c> (PLAN 1.3/4.5.3). <see cref="MaxImageBytes"/> is the vendor's own upload limit
/// for the raw image (lower than the host's 32 MiB, PLAN 4.5.1 "供应商更低上限优先").
/// </summary>
public sealed record OcrPackage(string InstanceId, string PackageId, string Plan, string DefaultOrigin, IReadOnlyList<CredentialTarget> Credentials, long MaxImageBytes)
    : ICredentialPackage
{
    public string Signer => $"unsigned:{PackageId}";
    public IReadOnlyList<string> SecretNames => [.. Credentials.Select(c => c.Secret).Distinct()];
    public string Directory => $"plugins/{InstanceId}";

    /// <summary>The exact origin this package calls: an explicit <c>baseUrl</c> (tests), else the vendor default.</summary>
    public string Origin(IReadOnlyDictionary<string, string> config)
        => config.TryGetValue("baseUrl", out var baseUrl) && Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            && Domain.Origin.TryNormalize($"{uri.Scheme}://{uri.Authority}", out var custom) ? custom : Domain.Origin.Normalize(DefaultOrigin);

    public IReadOnlyList<CredentialGrant> RequiredGrants(IReadOnlyDictionary<string, string> config)
    {
        string origin = Origin(config);
        return [.. Credentials.Select(c => new CredentialGrant(PackageId, Signer, c.Secret, origin, c.Use))];
    }
}

public static class OcrCatalog
{
    public const string TencentOcr = "tencent-ocr", SimpleLatex = "simple-latex";

    public static readonly IReadOnlyList<OcrPackage> All =
    [
        // P-O01: GeneralBasicOCR, JSON ImageBase64 (Base64 of the image at most 10 MB, so about 7.5 MB raw), TC3-signed by the host.
        new(TencentOcr, "app.susu.tencent-ocr", "F11.2 P-O01", "https://ocr.tencentcloudapi.com",
            [new("secretId", "signer:tencent-tc3"), new("secretKey", "signer:tencent-tc3")], 7 * 1024 * 1024),
        // P-O02: SimpleTex multipart upload with a UAT in the "token" header.
        new(SimpleLatex, "app.susu.simple-latex", "F11.2 P-O02", "https://server.simpletex.cn",
            [new("apiKey", "header:token")], 10 * 1024 * 1024),
    ];

    public static OcrPackage? Find(string instanceId) => All.FirstOrDefault(p => p.InstanceId == instanceId);

    /// <summary>
    /// The OCR instances to try, in order: the selected service first (SetOcr "默认"), then every other enabled OCR service in
    /// catalog order. Only services enabled for <see cref="Capability.Ocr"/> are listed; the caller still skips any whose
    /// credentials are not saved and granted (PLAN 1.2: the feature is unavailable when none is left).
    /// </summary>
    public static IReadOnlyList<string> Candidates(AppSettings settings)
    {
        var enabled = settings.Services.Where(s => s.Capability == Capability.Ocr && s.Enabled && Find(s.Instance) is not null).Select(s => s.Instance).ToHashSet(StringComparer.Ordinal);
        var order = new List<string>();
        string selected = settings.Ocr.Service;
        if (enabled.Contains(selected)) order.Add(selected);
        order.AddRange(All.Select(p => p.InstanceId).Where(id => id != selected && enabled.Contains(id)));
        return order;
    }
}

/// <summary>
/// SetOcr (F11.2 service; F11.3 the page): the OCR service used by default; whether recognized text is translated at once
/// (DESIGN "识别完成后自动翻译", on); whether a copy of each screenshot is kept in Pictures/Su-Su (off unless the user turns it on,
/// OCR03) and for how many days the startup cleanup keeps indexed copies (DESIGN "保留 7 天", ARCHITECTURE 8.4).
/// </summary>
public sealed record OcrSettings(string Service, bool AutoTranslate = true, bool KeepScreenshots = false, int RetentionDays = OcrSettings.DefaultRetentionDays)
{
    public const int DefaultRetentionDays = 7, MinRetentionDays = 1, MaxRetentionDays = 365;

    /// <summary>DESIGN SetOcr: Tencent OCR is the default service.</summary>
    public static OcrSettings Default => new(OcrCatalog.TencentOcr);
}

/// <summary>
/// The text of an OCR result as it enters the source card and the translation pipeline (PLAN 6.2 ⑦, F11.2): blocks in the
/// order the service returned them, one per line; formula blocks keep their LaTeX source verbatim so it can be copied.
/// Blank blocks are dropped. Null when nothing but whitespace was recognized.
/// </summary>
public static class OcrText
{
    public static string? Join(IEnumerable<OcrBlock> blocks)
    {
        var text = new StringBuilder();
        foreach (var block in blocks)
        {
            string value = block.Kind == "formula" ? block.Text.Trim() : block.Text.TrimEnd();
            if (value.Trim().Length == 0) continue;
            if (text.Length > 0) text.Append('\n');
            text.Append(value);
        }
        return text.Length == 0 ? null : text.ToString();
    }
}

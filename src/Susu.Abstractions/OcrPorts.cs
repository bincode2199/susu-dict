using Susu.Contracts;

namespace Susu.Abstractions;

/// <summary>
/// One recognition request (PLAN 4.4 <c>ocr(req: { image: FileHandle; lang?: string })</c>, F11.2). <see cref="Image"/> is the
/// host file lease id of the screenshot (a <see cref="ScreenshotImage"/>'s <c>File.LeaseId</c>); the provider grants exactly that
/// handle to the one plugin call and never reads or forwards the bytes itself. Lang: canonical BCP-47 hint, or null to use the
/// service's configured language.
/// </summary>
public sealed record OcrCall(string Image, string Mime, long Bytes, int Width, int Height, string? Lang, string AttemptId, TimeSpan Timeout);

/// <summary>Recognized blocks (validated, with non-blank text), or a classified failure. "Nothing recognized" is a failure.</summary>
public abstract record OcrOutcome
{
    public sealed record Recognized(IReadOnlyList<OcrBlock> Blocks) : OcrOutcome;
    /// <summary>The service answered but found no text in the image (never reported as an empty success).</summary>
    public sealed record NoText : OcrOutcome;
    public sealed record Failure(ProviderError Error) : OcrOutcome;
}

/// <summary>
/// An OCR service (F11.2: P-O01 Tencent OCR, P-O02 Simple LaTeX as plugin packages). Cancelling the token cancels the plugin
/// call and the broker's I/O; the caller keeps ownership of the image lease throughout.
/// </summary>
public interface IOcrProvider
{
    string InstanceId { get; }
    Task<OcrOutcome> RecognizeAsync(OcrCall call, CancellationToken cancellationToken);
}

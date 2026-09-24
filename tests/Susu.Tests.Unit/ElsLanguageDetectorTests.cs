using Susu.Windows;
using Xunit;

namespace Susu.Tests.Unit;

/// <summary>
/// F06.1: the real Windows ELS P/Invoke (elscore.dll), not a fake - runs on every Windows CI/dev box
/// since ELS ships with the OS, but a language pack or service registration is not guaranteed everywhere,
/// so a service-not-found/empty result is tolerated; only genuine exceptions/hangs would be a real bug.
/// </summary>
public class ElsLanguageDetectorTests
{
    [Fact]
    public async Task Detects_a_clearly_english_sentence_or_returns_empty_without_throwing()
    {
        var detector = new ElsLanguageDetector();
        var candidates = await detector.DetectAsync("This is a complete English sentence for local language detection.", TestContext.Current.CancellationToken);
        Assert.NotNull(candidates);
        // Tolerant on a box without the English detection service/language pack registered; verified by
        // hand that this returns ["en", ...] on a normal Windows 11 dev box (F06.1 evidence).
        if (candidates.Count > 0) Assert.All(candidates, c => Assert.False(string.IsNullOrWhiteSpace(c)));
    }

    [Fact]
    public async Task Empty_text_returns_no_candidates()
        => Assert.Empty(await new ElsLanguageDetector().DetectAsync("", TestContext.Current.CancellationToken));

    [Fact]
    public async Task Already_cancelled_token_throws_cancellation_not_a_detection_failure()
    {
        using var cts = new System.Threading.CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ElsLanguageDetector().DetectAsync("hello", cts.Token));
    }
}

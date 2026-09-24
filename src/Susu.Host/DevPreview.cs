#if DEV_PREVIEW
using Susu.Abstractions;
using Susu.Contracts;
using Susu.Domain;

namespace Susu.Host;

/// <summary>
/// Development-build-only fixture services (DEV-PLAN F03.4): they demonstrate the loading, streaming, success,
/// error and unsupported card states before real adapters exist (F05/F06). This file is compiled only with
/// DEV_PREVIEW; release builds contain no fixture provider and register no working translation entry.
/// </summary>
internal sealed class FixtureProvider(string serviceId, string displayName, Func<TranslateCall, Func<string, ValueTask>, CancellationToken, Task<ProviderOutcome>> behaviour,
    Func<string, string, bool>? pairs = null) : ITranslationProvider
{
    private static readonly TranslationLimits Limits_ = new(InputUnit.Utf8Bytes, 500, BatchMode.Single, 1, 500);

    public string ServiceId { get; } = serviceId;
    public string DisplayName { get; } = displayName;
    public string LimiterKey => $"fixture:{ServiceId}";
    public TranslationLimits Limits => Limits_;
    public bool SupportsLanguagePair(string from, string to) => pairs?.Invoke(from, to) ?? true;
    public Task<ProviderOutcome> TranslateAsync(TranslateCall call, Func<string, ValueTask> onChunk, CancellationToken cancellationToken) => behaviour(call, onChunk, cancellationToken);

    public static IReadOnlyList<ITranslationProvider> All() =>
    [
        new FixtureProvider("fixture-echo", "Fixture · 回显", async (call, _, ct) =>
        {
            await Task.Delay(350, ct);
            return new ProviderOutcome.Success($"［预览］{call.Text}");
        }),
        new FixtureProvider("fixture-stream", "Fixture · 流式", async (call, chunk, ct) =>
        {
            var pieces = new[] { "这是", "开发预览", "的流式", "译文，", "不是", "真实服务", "的结果。" };
            foreach (var piece in pieces) { await Task.Delay(180, ct); await chunk(piece); }
            return new ProviderOutcome.Success(string.Concat(pieces));
        }),
        new FixtureProvider("fixture-network", "Fixture · 网络错误", async (_, _, ct) =>
        {
            await Task.Delay(500, ct);
            return new ProviderOutcome.Failure(new ProviderError(ErrorKind.Network));
        }),
        new FixtureProvider("fixture-quota", "Fixture · 额度用完", async (_, _, ct) =>
        {
            await Task.Delay(300, ct);
            return new ProviderOutcome.Failure(new ProviderError(ErrorKind.Quota));
        }),
        new FixtureProvider("fixture-unsupported", "Fixture · 不支持语言对", (_, _, _) => Task.FromResult<ProviderOutcome>(new ProviderOutcome.Success("")), (_, _) => false),
    ];
}
#endif

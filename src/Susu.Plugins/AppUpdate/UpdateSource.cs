using System.Text.Json;
using Susu.Abstractions;
using Susu.Storage;

namespace Susu.Plugins.AppUpdate;

/// <summary>Moves bytes for the update source. GetAsync returns null for "not found" (a rotation record that does not exist) and throws for any other failure.</summary>
public interface IUpdateTransport
{
    Task<byte[]?> GetAsync(Uri uri, int maxBytes, CancellationToken cancellationToken);

    /// <summary>Writes at most <paramref name="maxBytes"/> to the destination file; a longer body fails (IOException).</summary>
    Task DownloadAsync(Uri uri, string destination, long maxBytes, CancellationToken cancellationToken);
}

public sealed record UpdateSourceFile(string? ManifestUrl);

/// <summary>
/// The configured update manifest location. Nothing is registered by default and no endpoint is built in (the project has not specified one): the URL
/// is read from <c>update-source.json</c> in the updates folder and must be an https URL without credentials. When it is absent, there is no source and
/// the page says updates are unavailable.
/// </summary>
public static class UpdateSourceConfig
{
    public const string FileName = "update-source.json";

    public static Uri? Load(string updatesFolder)
    {
        try
        {
            string path = Path.Combine(updatesFolder, FileName);
            if (!File.Exists(path) || new FileInfo(path).Length > 4096) return null;
            var file = JsonSerializer.Deserialize(File.ReadAllBytes(path), AppUpdateJson.Default.UpdateSourceFile);
            return Parse(file?.ManifestUrl);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    public static Uri? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 300) return null;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length > 0 || uri.Host.Length == 0 || !string.IsNullOrEmpty(uri.Fragment)) return null;
        string last = uri.Segments[^1];
        return last.EndsWith('/') ? null : uri; // the URL names the manifest file itself
    }
}

/// <summary>
/// The real update source: https GETs of the manifest, its detached signature and any key rotation records next to it, handed to the verifier before
/// anything is read. A transport failure is "network"; a verification failure keeps its own code; neither is ever reported as up to date.
/// The package is fetched from the same directory by the verified file name, never from a URL inside the manifest.
/// </summary>
public sealed class SignedManifestAppUpdateSource(Uri manifestUrl, IUpdateTransport transport, UpdateVerifier verifier, UpdateTrustStore store) : IAppUpdateSource
{
    private Uri Sibling(string name) => new(manifestUrl, name);

    public async Task<AppUpdateCheck> CheckAsync(string currentVersion, CancellationToken cancellationToken)
    {
        try
        {
            byte[]? manifest = await transport.GetAsync(manifestUrl, UpdateVerifier.MaxManifestBytes + 1, cancellationToken).ConfigureAwait(false);
            if (manifest is null) return AppUpdateCheck.Failed("manifest-unavailable");
            byte[]? signature = await transport.GetAsync(new Uri(manifestUrl.AbsoluteUri + ".sig"), UpdateSignature.MaxSignatureFileBytes + 1, cancellationToken).ConfigureAwait(false);
            if (signature is null) return AppUpdateCheck.Failed("signature-missing");
            var rotations = new List<SignedBlob>();
            for (int n = store.Chain.Count + 1; n <= store.Chain.Count + UpdateVerifier.MaxRotations; n++)
            {
                byte[]? record = await transport.GetAsync(Sibling($"keyring-{n}.yaml"), KeyRotation.MaxRecordBytes + 1, cancellationToken).ConfigureAwait(false);
                if (record is null) break;
                byte[]? recordSignature = await transport.GetAsync(Sibling($"keyring-{n}.yaml.sig"), UpdateSignature.MaxSignatureFileBytes + 1, cancellationToken).ConfigureAwait(false);
                if (recordSignature is null) return AppUpdateCheck.Failed("rotation-invalid");
                rotations.Add(new SignedBlob(record, recordSignature));
            }
            return verifier.Check(new UpdateBundle(manifest, signature, rotations), currentVersion);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TimeoutException or InvalidDataException || e is TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return AppUpdateCheck.Failed("network");
        }
    }

    public async Task<string> FetchAsync(AppUpdateOffer offer, string folder, CancellationToken cancellationToken)
    {
        if (!UpdateVerifier.IsSafeFileName(offer.FileName)) throw new InvalidDataException("file name");
        Directory.CreateDirectory(folder);
        string destination = Path.Combine(folder, offer.FileName);
        await transport.DownloadAsync(Sibling(offer.FileName), destination, offer.Size, cancellationToken).ConfigureAwait(false);
        return destination;
    }
}

/// <summary>The https transport. Each request has a timeout; a redirect to anything but https is refused; bodies are size-limited while reading.</summary>
public sealed class HttpUpdateTransport(HttpClient client) : IUpdateTransport
{
    public async Task<byte[]?> GetAsync(Uri uri, int maxBytes, CancellationToken cancellationToken)
    {
        RequireHttps(uri);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        RequireHttps(response.RequestMessage?.RequestUri);
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        await CopyLimited(stream, buffer, maxBytes, timeout.Token).ConfigureAwait(false);
        return buffer.ToArray();
    }

    public async Task DownloadAsync(Uri uri, string destination, long maxBytes, CancellationToken cancellationToken)
    {
        RequireHttps(uri);
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        RequireHttps(response.RequestMessage?.RequestUri);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var file = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough);
        await CopyLimited(stream, file, maxBytes, cancellationToken).ConfigureAwait(false);
        await file.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void RequireHttps(Uri? uri)
    {
        if (uri is null || uri.Scheme != Uri.UriSchemeHttps) throw new HttpRequestException("update transport requires https");
    }

    private static async Task CopyLimited(Stream from, Stream to, long maxBytes, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await from.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > maxBytes) throw new IOException("response larger than allowed");
            await to.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }
}

public sealed record ScheduleFile(string? LastAttempt, bool AutoCheck);

/// <summary>The update preferences shared by the application and plugin update checks: when the last attempt was and whether automatic checks are on (default on).</summary>
public sealed class UpdatePrefs
{
    private readonly string path;

    public DateTimeOffset? LastAttempt { get; private set; }
    public bool AutoCheck { get; private set; } = true;

    public UpdatePrefs(string path)
    {
        this.path = path;
        try
        {
            if (!File.Exists(path)) return;
            var file = JsonSerializer.Deserialize(File.ReadAllBytes(path), AppUpdateJson.Default.ScheduleFile);
            if (file is null) return;
            AutoCheck = file.AutoCheck;
            if (DateTimeOffset.TryParse(file.LastAttempt, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var t)) LastAttempt = t;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
    }

    public void Update(DateTimeOffset? attempt = null, bool? autoCheck = null)
    {
        if (attempt is { } a) LastAttempt = a;
        if (autoCheck is { } c) AutoCheck = c;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var file = new ScheduleFile(LastAttempt?.ToString("O", System.Globalization.CultureInfo.InvariantCulture), AutoCheck);
            AtomicFile.Write(path, JsonSerializer.SerializeToUtf8Bytes(file, AppUpdateJson.Default.ScheduleFile));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>
/// The shared background schedule (ARCHITECTURE 10): the first check 15 s after start, then one check when at least 24 h have passed since the last
/// attempt (a failed attempt counts), serial, never a polling loop (one sleep until the next due time). It runs the checks only; downloading and
/// installing the application are never started from here. Turned off by <see cref="UpdatePrefs.AutoCheck"/>.
/// </summary>
public sealed class UpdateSchedule(IClock clock, UpdatePrefs prefs, IReadOnlyList<Func<CancellationToken, Task>> checks)
{
    public static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(15), Interval = TimeSpan.FromHours(24);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await clock.Delay(StartDelay, cancellationToken).ConfigureAwait(false);
        while (!cancellationToken.IsCancellationRequested)
        {
            var now = clock.UtcNow;
            if (prefs.AutoCheck && (prefs.LastAttempt is not { } last || now - last >= Interval))
            {
                prefs.Update(attempt: now); // recorded before the checks: a crash or failure still counts as an attempt
                foreach (var check in checks)
                {
                    try { await check(cancellationToken).ConfigureAwait(false); }
                    catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException) { }
                }
            }
            var wait = prefs.AutoCheck && prefs.LastAttempt is { } l ? l + Interval - clock.UtcNow : Interval;
            if (wait < TimeSpan.FromMinutes(1)) wait = TimeSpan.FromMinutes(1);
            await clock.Delay(wait, cancellationToken).ConfigureAwait(false);
        }
    }
}

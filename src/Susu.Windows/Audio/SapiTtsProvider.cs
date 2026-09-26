using System.Globalization;
using System.Runtime.InteropServices;
using Susu.Abstractions;
using Susu.Contracts;
using Susu.Domain;

namespace Susu.Windows.Audio;

/// <summary>
/// Native Windows TTS (F10.1, PLAN 2.3 NativeProvider "Windows 内置 TTS（SAPI）"): SAPI 5 <c>ISpVoice</c> through explicit
/// vtable calls, speaking into a WAV file (22.05 kHz, 16-bit, mono) held by a host lease. It needs no network, no
/// account and no plugin process (TTS01: SAPI does not depend on the plugin host being alive).
///
/// Voices are the installed SAPI voice tokens; a voice id is the token's last path segment
/// (e.g. <c>TTS_MS_EN-US_ZIRA_11.0</c>). Without a voice the first installed voice for the text's language is used;
/// when a language is given and no installed voice speaks it, the call fails with <c>unsupported_language</c> instead
/// of reading the text in the wrong language. Text is always spoken as plain text (<c>SPF_IS_NOT_XML</c>), so markup in
/// captured text can never change voice, volume or output. Cancelling stops synthesis promptly and deletes the partial
/// file (B07).
/// </summary>
public sealed class SapiTtsProvider(IAudioFileFactory files) : ITtsProvider
{
    public const int MaxChars = 5000;

    /// <summary>The settings schema of the native instance (voice and speed through the F07 config controls).</summary>
    public static readonly IReadOnlyList<ConfigField> Schema =
    [
        new("voice", ConfigFieldType.String, "Voice", Help: "Leave empty to use the installed voice for the text's language.",
            Options: new OptionsSource(OptionsSource.VoicesMethod, [])),
        new("rate", ConfigFieldType.Number, "Speed", "1", Minimum: SpeakRequest.MinRate, Maximum: SpeakRequest.MaxRate, Placeholder: "1",
            Help: "Speech speed from 0.5 to 2; 1 is normal."),
    ];

    private static readonly Guid ClsidSpVoice = new("96749377-3391-11D2-9EE3-00C04F797396");
    private static readonly Guid IidSpVoice = new("6C44DF74-72B9-4992-A1EC-EF996E0422D4");
    private static readonly Guid ClsidSpStream = new("715D9C59-4442-11D2-9605-00C04F8EE628");
    private static readonly Guid IidSpStream = new("12E3CCA9-7518-44C5-A5E7-BA5A79CB929E");
    private static readonly Guid ClsidSpObjectTokenCategory = new("A910187F-0C7A-45AC-92CC-59EDAFB77B53");
    private static readonly Guid IidSpObjectTokenCategory = new("2D3D3845-39AF-4850-BBF9-40B49780011D");
    private static readonly Guid SpdfidWaveFormatEx = new("C31ADBAE-527F-4FF5-A230-F62BB61FF70C");
    private const string VoicesCategory = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Speech\Voices";
    private const uint SpfAsync = 1, SpfPurgeBeforeSpeak = 2, SpfIsNotXml = 0x10;
    private const int SpfmCreateAlways = 3;

    public string InstanceId => BuiltInCatalog.NativeTts;
    public bool Native => true;

    /// <summary>Installed SAPI voices (id, display name, BCP-47 language). Empty when SAPI has none.</summary>
    public static Task<IReadOnlyList<Voice>> VoicesAsync()
        => Com.RunMta<IReadOnlyList<Voice>>(() =>
        {
            var list = new List<Voice>();
            ForEachToken((id, name, lang, _) => { list.Add(new Voice(id, name, lang)); return false; });
            return list;
        }, "susu-sapi-voices");

    public async Task<AudioOutcome> SynthesizeAsync(SpeakCall call, CancellationToken cancellationToken)
    {
        var request = call.Request;
        if (string.IsNullOrWhiteSpace(request.Text)) return Fail(ErrorKind.BadResponse, "empty text");
        int chars = request.Text.EnumerateRunes().Count();
        if (chars > MaxChars) return Fail(ErrorKind.BadResponse, $"text too long for SAPI ({chars} > {MaxChars} characters)");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (call.Timeout > TimeSpan.Zero && call.Timeout != Timeout.InfiniteTimeSpan) deadline.CancelAfter(call.Timeout);
        var clip = files.Create("audio/wav", "wav");
        bool handedOver = false;
        try
        {
            string path = clip.FilePath;
            var error = await Com.RunMta(() => Speak(request, path, deadline.Token), "susu-sapi-speak");
            if (error is not null) return new AudioOutcome.Failure(error);
            handedOver = true;
            return new AudioOutcome.Ready(clip);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return Fail(ErrorKind.Timeout, "SAPI synthesis timed out"); }
        catch (COMException error) { return Fail(ErrorKind.Unavailable, $"SAPI 0x{error.HResult:X8}"); }
        finally { if (!handedOver) clip.Dispose(); } // releases the lease: a partial or failed file is deleted (B07)
    }

    private static AudioOutcome Fail(ErrorKind kind, string detail) => new AudioOutcome.Failure(new ProviderError(kind, detail));

    /// <summary>SAPI rate is -10..10 where +10 is about 3x and -10 about 1/3x (logarithmic).</summary>
    public static int SapiRate(double multiplier)
        => (int)Math.Clamp(Math.Round(10 * Math.Log(Math.Clamp(multiplier, SpeakRequest.MinRate, SpeakRequest.MaxRate)) / Math.Log(3)), -10, 10);

    /// <summary>Runs on an MTA thread. Null on success; the file at <paramref name="path"/> then holds the WAV.</summary>
    private static unsafe ProviderError? Speak(SpeakRequest request, string path, CancellationToken cancellationToken)
    {
        nint token = 0, voice = 0, stream = 0;
        try
        {
            token = FindToken(request.Voice, request.Lang, out var refusal);
            if (refusal is not null) return refusal;

            voice = Com.Create(ClsidSpVoice, IidSpVoice);
            stream = Com.Create(ClsidSpStream, IidSpStream);
            // WAVEFORMATEX: PCM, 1 channel, 22050 Hz, 44100 B/s, block 2, 16 bit, cbSize 0.
            byte* format = stackalloc byte[18];
            *(ushort*)format = 1; *(ushort*)(format + 2) = 1; *(uint*)(format + 4) = 22050; *(uint*)(format + 8) = 44100;
            *(ushort*)(format + 12) = 2; *(ushort*)(format + 14) = 16; *(ushort*)(format + 16) = 0;
            Guid formatId = SpdfidWaveFormatEx;
            fixed (char* file = path)
                Com.Check(((delegate* unmanaged[Stdcall]<nint, char*, int, Guid*, byte*, ulong, int>)Com.Slot(stream, 17))(stream, file, SpfmCreateAlways, &formatId, format, 0), "ISpStream.BindToFile");
            Com.Check(((delegate* unmanaged[Stdcall]<nint, nint, int, int>)Com.Slot(voice, 13))(voice, stream, 1), "ISpVoice.SetOutput");
            if (token != 0) Com.Check(((delegate* unmanaged[Stdcall]<nint, nint, int>)Com.Slot(voice, 18))(voice, token), "ISpVoice.SetVoice");
            Com.Check(((delegate* unmanaged[Stdcall]<nint, int, int>)Com.Slot(voice, 28))(voice, SapiRate(request.ClampedRate)), "ISpVoice.SetRate");

            fixed (char* text = request.Text)
                Com.Check(((delegate* unmanaged[Stdcall]<nint, char*, uint, uint*, int>)Com.Slot(voice, 20))(voice, text, SpfAsync | SpfIsNotXml, null), "ISpVoice.Speak");
            var wait = (delegate* unmanaged[Stdcall]<nint, uint, int>)Com.Slot(voice, 32);
            while (true)
            {
                int hr = wait(voice, 50); // S_OK: done; S_FALSE: still speaking
                Com.Check(hr, "ISpVoice.WaitUntilDone");
                if (hr == 0) break;
                if (cancellationToken.IsCancellationRequested)
                {
                    ((delegate* unmanaged[Stdcall]<nint, char*, uint, uint*, int>)Com.Slot(voice, 20))(voice, null, SpfPurgeBeforeSpeak, null);
                    wait(voice, 1000);
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
            return null;
        }
        finally
        {
            if (stream != 0) ((delegate* unmanaged[Stdcall]<nint, int>)Com.Slot(stream, 18))(stream); // ISpStream.Close: flush the WAV header, free the file
            if (voice != 0) ((delegate* unmanaged[Stdcall]<nint, nint, int, int>)Com.Slot(voice, 13))(voice, 0, 1);
            Com.Release(voice);
            Com.Release(stream);
            Com.Release(token);
        }
    }

    /// <summary>The token for an explicit voice id, or the best installed voice for <paramref name="lang"/>, or 0 (SAPI default).</summary>
    private static unsafe nint FindToken(string? voiceId, string? lang, out ProviderError? refusal)
    {
        refusal = null;
        nint best = 0;
        int bestScore = 0;
        bool any = false;
        ForEachToken((id, _, voiceLang, token) =>
        {
            any = true;
            int score = !string.IsNullOrEmpty(voiceId) ? (string.Equals(id, voiceId, StringComparison.OrdinalIgnoreCase) ? 100 : 0) : LanguageScore(lang, voiceLang);
            if (score > bestScore)
            {
                Com.Release(best);
                ((delegate* unmanaged[Stdcall]<nint, uint>)Com.Slot(token, 1))(token); // AddRef: kept beyond the enumeration
                best = token;
                bestScore = score;
            }
            return false;
        });
        if (!any) { refusal = new ProviderError(ErrorKind.Unavailable, "no SAPI voice is installed"); return 0; }
        if (best != 0) return best;
        if (!string.IsNullOrEmpty(voiceId)) refusal = new ProviderError(ErrorKind.BadResponse, "the configured SAPI voice is not installed");
        else if (!string.IsNullOrEmpty(lang) && lang != "auto") refusal = new ProviderError(ErrorKind.UnsupportedLanguage, $"no installed SAPI voice speaks '{lang}'");
        return 0;
    }

    /// <summary>0: no match; 1: same primary language; 2: preferred script/region; 3: exact tag. No language: any voice (1).</summary>
    public static int LanguageScore(string? requested, string voiceLang)
    {
        if (string.IsNullOrEmpty(requested) || requested == "auto") return 1;
        if (string.Equals(requested, voiceLang, StringComparison.OrdinalIgnoreCase)) return 3;
        string primary = requested.Split('-')[0], voicePrimary = voiceLang.Split('-')[0];
        if (!string.Equals(primary, voicePrimary, StringComparison.OrdinalIgnoreCase)) return 0;
        string region = voiceLang.Contains('-') ? voiceLang[(voiceLang.IndexOf('-') + 1)..].ToUpperInvariant() : "";
        return requested switch
        {
            "zh-Hans" => region is "CN" or "SG" ? 2 : 1,
            "zh-Hant" => region is "TW" or "HK" or "MO" ? 2 : 1,
            "en" => region == "US" ? 2 : 1,
            _ => 1,
        };
    }

    /// <summary>Enumerates installed voice tokens; the callback returns true to stop. Tokens are released after each callback.</summary>
    private static unsafe void ForEachToken(Func<string, string, string, nint, bool> visit)
    {
        nint category = 0, enumerator = 0;
        try
        {
            category = Com.Create(ClsidSpObjectTokenCategory, IidSpObjectTokenCategory);
            fixed (char* id = VoicesCategory)
                Com.Check(((delegate* unmanaged[Stdcall]<nint, char*, int, int>)Com.Slot(category, 15))(category, id, 0), "ISpObjectTokenCategory.SetId");
            Com.Check(((delegate* unmanaged[Stdcall]<nint, char*, char*, nint*, int>)Com.Slot(category, 18))(category, null, null, &enumerator), "EnumTokens");
            uint count;
            Com.Check(((delegate* unmanaged[Stdcall]<nint, uint*, int>)Com.Slot(enumerator, 8))(enumerator, &count), "IEnumSpObjectTokens.GetCount");
            for (uint i = 0; i < count; i++)
            {
                nint token = 0;
                if (((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Com.Slot(enumerator, 7))(enumerator, i, &token) < 0 || token == 0) continue;
                try
                {
                    nint rawId = 0;
                    ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Com.Slot(token, 16))(token, &rawId);
                    string fullId = Com.TakeString(rawId) ?? "";
                    string id = fullId[(fullId.LastIndexOf('\\') + 1)..];
                    string name = StringValue(token, null) ?? id;
                    string lang = "";
                    nint attributes = 0;
                    fixed (char* key = "Attributes")
                        if (((delegate* unmanaged[Stdcall]<nint, char*, nint*, int>)Com.Slot(token, 9))(token, key, &attributes) >= 0 && attributes != 0)
                        {
                            try
                            {
                                name = StringValue(attributes, "Name") ?? name;
                                lang = LanguageFromLcid(StringValue(attributes, "Language"));
                            }
                            finally { Com.Release(attributes); }
                        }
                    if (id.Length > 0 && visit(id, name, lang, token)) return;
                }
                finally { Com.Release(token); }
            }
        }
        catch (COMException) { } // SAPI missing or broken: no voices
        finally
        {
            Com.Release(enumerator);
            Com.Release(category);
        }
    }

    private static unsafe string? StringValue(nint key, string? name)
    {
        nint value = 0;
        int hr;
        fixed (char* n = name)
            hr = ((delegate* unmanaged[Stdcall]<nint, char*, nint*, int>)Com.Slot(key, 6))(key, name is null ? null : n, &value);
        return hr >= 0 ? Com.TakeString(value) : null;
    }

    /// <summary>SAPI "Language" attribute: hex LCIDs separated by ';' (first is primary), e.g. "409;9" → en-US.</summary>
    public static string LanguageFromLcid(string? value)
    {
        string first = (value ?? "").Split(';')[0].Trim();
        if (!int.TryParse(first, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int lcid) || lcid <= 0) return "";
        try { return CultureInfo.GetCultureInfo(lcid).Name; }
        catch (CultureNotFoundException) { return ""; }
    }
}

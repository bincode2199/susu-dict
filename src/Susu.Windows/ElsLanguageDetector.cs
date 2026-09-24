using System.Runtime.InteropServices;
using Susu.Abstractions;

namespace Susu.Windows;

/// <summary>
/// Windows Extended Linguistic Services (ELS) language detection (ARCHITECTURE 7:普通输入先 Unicode 快判，
/// 混排走 ELS - pure Han/Latin text takes the fast path in Susu.Domain.ScriptDetector and never reaches
/// here; only mixed-script or otherwise ambiguous text calls this). Talks to elscore.dll directly
/// (MappingGetServices/MappingRecognizeText) - the same Win32 API F00's AOT probe
/// (src/Susu.Windows/native/probes.cpp:susu_els_detect) validated - as managed P/Invoke, so detection
/// needs no extra native module shipped with the host (susu_windows_probe.dll is probe/harness-only and
/// is never shipped, per src/Susu.Windows/native/CMakeLists.txt).
///
/// Detection failure (missing service, no language pack, any HRESULT failure) never throws to the
/// caller: it returns an empty candidate list, and ShellCoordinator falls back to the configured default
/// language (ARCHITECTURE 7: "检测失败回默认语言，不阻塞至翻译总 deadline 之外").
/// </summary>
public sealed partial class ElsLanguageDetector : ILanguageDetector
{
    // ELS_GUID_LANGUAGE_DETECTION (elssrvc.h).
    private static readonly Guid LanguageDetectionGuid = new("CF7E00B1-909B-4D95-A8F4-611F7C377702");

    [StructLayout(LayoutKind.Sequential)]
    private struct MAPPING_ENUM_OPTIONS
    {
        public nuint Size;
        public IntPtr pszCategory;
        public IntPtr pszInputLanguage;
        public IntPtr pszOutputLanguage;
        public IntPtr pszInputScript;
        public IntPtr pszOutputScript;
        public IntPtr pszInputContentType;
        public IntPtr pszOutputContentType;
        public IntPtr pGuid;
        public uint Flags; // OnlineService:2, ServiceType:2 bitfields - unused, left zero (ALL_SERVICES/ALL_SERVICE_TYPES)
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MAPPING_PROPERTY_BAG
    {
        public nuint Size;
        public IntPtr prgResultRanges; // MAPPING_DATA_RANGE*
        public uint dwRangesCount;
        public IntPtr pServiceData;
        public uint dwServiceDataSize;
        public IntPtr pCallerData;
        public uint dwCallerDataSize;
        public IntPtr pContext;
    }

    // MAPPING_DATA_RANGE (elscore.h) field offsets on a 64-bit target (the only architectures this app
    // ships for, x64/arm64, both 8-byte pointers): only pData/dwDataSize are read, so the struct itself
    // is never materialized in managed code - offsets are computed once from those two fields' positions.
    private const int RangeStride = 72, RangePDataOffset = 24, RangeDataSizeOffset = 32;

    [LibraryImport("elscore.dll")]
    private static partial int MappingGetServices(ref MAPPING_ENUM_OPTIONS pOptions, out IntPtr prgServices, out uint pdwServicesCount);

    [LibraryImport("elscore.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MappingRecognizeText(IntPtr pServiceInfo, string pszText, uint dwLength, uint dwIndex, IntPtr pOptions, ref MAPPING_PROPERTY_BAG pbag);

    [LibraryImport("elscore.dll")]
    private static partial int MappingFreeServices(IntPtr pServiceInfo);

    [LibraryImport("elscore.dll")]
    private static partial int MappingFreePropertyBag(ref MAPPING_PROPERTY_BAG pBag);

    public Task<IReadOnlyList<string>> DetectAsync(string text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<string> result;
        try { result = Detect(text); }
        catch (Exception) { result = []; } // never surfaces a detection failure as a translation failure
        return Task.FromResult(result);
    }

    private static unsafe IReadOnlyList<string> Detect(string text)
    {
        if (string.IsNullOrEmpty(text)) return [];
        Guid guid = LanguageDetectionGuid;
        var options = new MAPPING_ENUM_OPTIONS { Size = (nuint)Marshal.SizeOf<MAPPING_ENUM_OPTIONS>(), pGuid = (IntPtr)(&guid) };
        if (MappingGetServices(ref options, out IntPtr services, out uint count) < 0 || count == 0 || services == IntPtr.Zero) return [];
        try
        {
            var bag = new MAPPING_PROPERTY_BAG { Size = (nuint)Marshal.SizeOf<MAPPING_PROPERTY_BAG>() };
            if (MappingRecognizeText(services, text, (uint)text.Length, 0, IntPtr.Zero, ref bag) < 0) return [];
            try { return ReadLanguages(bag); }
            finally { MappingFreePropertyBag(ref bag); }
        }
        finally { MappingFreeServices(services); }
    }

    private static List<string> ReadLanguages(MAPPING_PROPERTY_BAG bag)
    {
        var result = new List<string>();
        if (bag.dwRangesCount == 0 || bag.prgResultRanges == IntPtr.Zero) return result;
        for (uint i = 0; i < bag.dwRangesCount; i++)
        {
            nint rangeAddress = bag.prgResultRanges + (int)(i * RangeStride);
            IntPtr data = Marshal.ReadIntPtr(rangeAddress, RangePDataOffset);
            int dataSize = Marshal.ReadInt32(rangeAddress, RangeDataSizeOffset);
            if (data == IntPtr.Zero || dataSize < 2) continue;
            string tag = (Marshal.PtrToStringUni(data, dataSize / 2) ?? "").TrimEnd('\0');
            if (tag.Length > 0 && !result.Contains(tag, StringComparer.OrdinalIgnoreCase)) result.Add(tag);
        }
        return result;
    }
}

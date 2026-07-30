// FilePatches — OOS throw sites for NavFile browser-round-trip Upload/Download.
//
// Scope: docs/scope.md §3.4 (file-storage). Browser round-trip variants require a
// live BC client and cannot run in the standalone runner. The stream-based variants
// (ALUploadIntoStream, ALDownloadFromStream) are in-scope and must NOT be touched.
//
// NavFile.ALUpload has 4 static overloads:
//   (string, string, string, string, ByRef<NavText>,    Guid) — 6 params
//   (DataError, string, string, string, string, ByRef<NavText>,    Guid) — 7 params
//   (string, string, string, string, ByRef<NavOemText>, Guid) — 6 params
//   (DataError, string, string, string, string, ByRef<NavOemText>, Guid) — 7 params
// The 6-param overloads delegate to the 7-param ones; both are hooked for defence-in-depth.
// ByRef<T> is a reference type (8-byte pointer on x64); using 'object' is ABI-safe.
//
// Same 4-overload pattern applies to ALDownload.
using System.Runtime.CompilerServices;
using AlRunner.Infrastructure;

namespace AlRunner;

public static partial class BcRuntime
{
    // ──────────────────────────────────────────────────────────────────
    // NavFile.ALUpload — browser round-trip (§3.4 OOS)
    // ──────────────────────────────────────────────────────────────────

    /// <summary>ALUpload(dialogTitle, fromFolder, filterText, fromFileName, ByRef toFileName, Guid) — 6 params.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool NavFile_ALUpload_6(string dialogTitle, string fromFolder,
        string filterText, string fromFileName, object toFileName, System.Guid automationId)
    {
        RunnerScope.ThrowOutOfScope("NavFile.Upload", "browser-roundtrip", "file-storage");
        return default;
    }

    /// <summary>ALUpload(DataError, dialogTitle, fromFolder, filterText, fromFileName, ByRef toFileName, Guid) — 7 params.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool NavFile_ALUpload_7(int errorLevel, string dialogTitle, string fromFolder,
        string filterText, string fromFileName, object toFileName, System.Guid automationId)
    {
        RunnerScope.ThrowOutOfScope("NavFile.Upload", "browser-roundtrip", "file-storage");
        return default;
    }

    // ──────────────────────────────────────────────────────────────────
    // NavFile.ALDownload — browser round-trip (§3.4 OOS)
    // ──────────────────────────────────────────────────────────────────

    /// <summary>ALDownload(fromFileName, dialogTitle, toFolder, filterText, ByRef toFileName, Guid) — 6 params.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool NavFile_ALDownload_6(string fromFileName, string dialogTitle,
        string toFolder, string filterText, object toFileName, System.Guid automationId)
    {
        RunnerScope.ThrowOutOfScope("NavFile.Download", "browser-roundtrip", "file-storage");
        return default;
    }

    /// <summary>ALDownload(DataError, fromFileName, dialogTitle, toFolder, filterText, ByRef toFileName, Guid) — 7 params.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool NavFile_ALDownload_7(int errorLevel, string fromFileName, string dialogTitle,
        string toFolder, string filterText, object toFileName, System.Guid automationId)
    {
        RunnerScope.ThrowOutOfScope("NavFile.Download", "browser-roundtrip", "file-storage");
        return default;
    }
}
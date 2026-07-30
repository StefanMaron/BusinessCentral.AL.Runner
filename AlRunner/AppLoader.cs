// AppLoader — universal `.app` package reader.
//
// BC `.app` files are a NAVX header (4-byte magic "NAVX" + 4-byte LE uint32 ZIP
// offset) followed by a ZIP archive. Two flavours we care about:
//
//   1. R2R packages (Microsoft-shipped: System Application, Base Application).
//      Outer ZIP contains `readytorunappmanifest.json`, a nested AL `.app`,
//      and `publishedartifacts/.../<HASH>.dll` — the pre-compiled IL DLL
//      we want to load directly.
//
//   2. alc `/generatecode+` output. ZIP contains `bin/COD<id>.cs` (and `.xml`)
//      — C# source per AL object, post BC's Compilation.Emit. This is what
//      v2 feeds into Roslyn for the AL-source path.
//
// One method per shape so the bundle pipeline can ask the right question.
//
// Reference for NAVX wrapper handling: AlRunner/Program.cs:4540
// (AppPackageReader.ExtractAlSources in v1).
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace AlRunner;

public sealed record DependencyRef(Guid AppId, string Name, string Publisher, Version Version, bool Optional = false);

public sealed record AppManifest(
    string Publisher,
    string Name,
    Version Version,
    Guid AppId,
    IReadOnlyList<DependencyRef> Dependencies,
    // Implicit first-party dep versions from the NAVX manifest's `Application` /
    // `Platform` attributes (the real `al` compiler injects Microsoft/Application
    // and Microsoft/System from these). Null when the manifest omits them.
    // See AppLoader.ImplicitRoots for synthesizing the matching DependencyRefs.
    Version? Application = null,
    Version? Platform = null);

public static class AppLoader
{
    /// <summary>
    /// Reads NavxManifest.xml from an `.app` package and returns the App element's
    /// Publisher / Name / Version / Id. Returns null if the file is malformed or
    /// missing the manifest.
    /// </summary>
    public static AppManifest? ReadManifest(string appPath)
    {
        try
        {
            var bytes = File.ReadAllBytes(appPath);
            return ReadManifestFromBytes(bytes);
        }
        catch { return null; }
    }

    /// <summary>
    /// True if the .app is a real, compiler-valid BC package — i.e. its NAVX zip
    /// contains a <c>SymbolReference.json</c> part. Such a package can serve
    /// compile-time symbols directly through BC's native .app scanner (no synthetic
    /// symbols.json needed), and merges tableextensions/etc. correctly. A synthetic
    /// source-only .app emitted by InProcessAppPackager returns false here.
    /// Returns false on any read/format error.
    /// </summary>
    public static bool HasSymbolReference(string appPath)
    {
        try
        {
            var bytes = File.ReadAllBytes(appPath);
            using var zip = OpenZipFromNavx(bytes);
            if (zip.Entries.Any(e => e.FullName.Equals("SymbolReference.json", StringComparison.OrdinalIgnoreCase)))
                return true;
            // R2R nested case: the inner .app carries the SymbolReference.json.
            var nested = zip.Entries.FirstOrDefault(e =>
                e.FullName.EndsWith(".app", StringComparison.OrdinalIgnoreCase) && !e.FullName.Contains('/'));
            if (nested == null) return false;
            using var ns = nested.Open();
            using var nms = new MemoryStream();
            ns.CopyTo(nms);
            using var innerZip = OpenZipFromNavx(nms.ToArray());
            return innerZip.Entries.Any(e => e.FullName.Equals("SymbolReference.json", StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    private static AppManifest? ReadManifestFromBytes(byte[] bytes)
    {
        try
        {
            using var zip = OpenZipFromNavx(bytes);
            var entry = zip.Entries.FirstOrDefault(e =>
                string.Equals(e.FullName, "NavxManifest.xml", StringComparison.OrdinalIgnoreCase));
            if (entry == null)
            {
                // R2R outer .app — recurse into nested .app
                var nested = zip.Entries.FirstOrDefault(e =>
                    e.FullName.EndsWith(".app", StringComparison.OrdinalIgnoreCase)
                    && !e.FullName.Contains('/'));
                if (nested == null) return null;
                using var nestedStream = nested.Open();
                using var nms = new MemoryStream();
                nestedStream.CopyTo(nms);
                return ReadManifestFromBytes(nms.ToArray());
            }
            using var s = entry.Open();
            var doc = XDocument.Load(s);
            XNamespace ns = "http://schemas.microsoft.com/navx/2015/manifest";
            var app = doc.Root?.Element(ns + "App");
            if (app == null) return null;
            var idStr = app.Attribute("Id")?.Value;
            var name = app.Attribute("Name")?.Value ?? "";
            var publisher = app.Attribute("Publisher")?.Value ?? "";
            var verStr = app.Attribute("Version")?.Value ?? "1.0.0.0";
            if (idStr == null || !Guid.TryParse(idStr, out var id)) return null;
            if (!Version.TryParse(verStr, out var ver)) return null;

            // <Dependencies><Dependency Id="..." Name="..." Publisher="..."
            //   MinVersion="..." CompatibilityId="..." /></Dependencies>
            var deps = new List<DependencyRef>();
            var depsRoot = doc.Root?.Element(ns + "Dependencies");
            if (depsRoot != null)
            {
                foreach (var dep in depsRoot.Elements(ns + "Dependency"))
                {
                    var depIdStr = dep.Attribute("Id")?.Value;
                    var depName = dep.Attribute("Name")?.Value ?? "";
                    var depPub = dep.Attribute("Publisher")?.Value ?? "";
                    var depVerStr = dep.Attribute("MinVersion")?.Value
                        ?? dep.Attribute("Version")?.Value
                        ?? "0.0.0.0";
                    Guid depId = Guid.Empty;
                    if (!string.IsNullOrEmpty(depIdStr))
                        Guid.TryParse(depIdStr, out depId);
                    if (!Version.TryParse(depVerStr, out var depVer))
                        depVer = new Version(0, 0, 0, 0);
                    deps.Add(new DependencyRef(depId, depName, depPub, depVer));
                }
            }
            // Implicit first-party deps: the `Application` / `Platform` attributes
            // on <App>. Modern apps do NOT list Microsoft apps under <Dependencies>;
            // the real `al` compiler injects them from these attributes. Capture the
            // versions so callers resolving a ROOT app can synthesize the matching
            // Microsoft/Application + Microsoft/System roots (see ImplicitRoots).
            Version.TryParse(app.Attribute("Application")?.Value, out var appVer);
            Version.TryParse(app.Attribute("Platform")?.Value, out var platVer);
            return new AppManifest(publisher, name, ver, id, deps, appVer, platVer);
        }
        catch { return null; }
    }

    /// <summary>
    /// Synthetic implicit first-party dependency roots for a ROOT app being
    /// compiled, derived from its manifest's `Application` / `Platform` versions.
    /// `Application` → Microsoft/Application (the umbrella app that transitively
    /// pulls Base Application + System Application + Business Foundation);
    /// `Platform` → Microsoft/System (platform symbols). Mirrors the app.json
    /// synthesis in Program.ReadDependencies so `.app` inputs resolve BaseApp the
    /// same way app.json inputs do. Roots are Optional (warn-not-throw if absent)
    /// and resolved by (Name, Publisher) — version is informational.
    ///
    /// Apply ONLY to the root app being compiled, never transitively: the
    /// dependency resolver throws on cycles, and every Microsoft app's manifest
    /// carries these same attributes (Application → Base Application → Application …).
    /// </summary>
    public static IEnumerable<DependencyRef> ImplicitRoots(AppManifest manifest)
    {
        if (manifest.Application != null)
            yield return new DependencyRef(Guid.Empty, "Application", "Microsoft", manifest.Application, Optional: true);
        if (manifest.Platform != null)
            yield return new DependencyRef(Guid.Empty, "System", "Microsoft", manifest.Platform, Optional: true);
    }

    /// <summary>
    /// True if the package contains an R2R `publishedartifacts/*.dll`.
    /// Used by the loader to pick between Tier-2 (R2R extract) and Tier-3
    /// (source-only on-the-fly compile).
    /// </summary>
    public static bool IsR2R(string appPath)
    {
        try
        {
            using var zip = OpenAppZip(appPath);
            return zip.Entries.Any(e =>
                e.FullName.StartsWith("publishedartifacts/", StringComparison.OrdinalIgnoreCase)
                && e.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }


    /// <summary>
    /// Returns the IL DLL bytes from a Microsoft R2R `.app` package, or null
    /// if no `publishedartifacts/*.dll` is present (i.e. the package is not R2R).
    /// Returns only the first DLL — kept for backwards-compat callers that
    /// happen to want a single-DLL result. Use <see cref="ExtractAllDlls"/>
    /// for multi-DLL R2R packages (e.g. Base Application is 5 DLL chunks).
    /// </summary>
    public static byte[]? ExtractDll(string appPath)
    {
        var all = ExtractAllDlls(appPath);
        return all.Count == 0 ? null : all[0];
    }

    /// <summary>
    /// Returns ALL `publishedartifacts/*.dll` byte blobs from a Microsoft R2R
    /// `.app` package. Microsoft ships large apps (notably Base Application)
    /// as multiple DLL chunks under `publishedartifacts/...`; loading only
    /// the first leaves the majority of types unresolved at runtime.
    /// Returns an empty list if the package is not R2R.
    /// </summary>
    public static IReadOnlyList<byte[]> ExtractAllDlls(string appPath)
    {
        using var zip = OpenAppZip(appPath);
        var dllEntries = zip.Entries
            .Where(e => e.FullName.StartsWith("publishedartifacts/", StringComparison.OrdinalIgnoreCase)
                     && e.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.FullName, StringComparer.Ordinal)
            .ToList();
        var result = new List<byte[]>(dllEntries.Count);
        foreach (var entry in dllEntries)
        {
            using var s = entry.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            result.Add(ms.ToArray());
        }
        return result;
    }

    /// <summary>
    /// Returns the per-AL-object C# sources from an alc `/generatecode+` `.app`
    /// (the `bin/*.cs` entries). Empty list if the package contains no `bin/*.cs`.
    /// </summary>
    public static IReadOnlyList<EmittedSource> ExtractCSharp(string appPath)
    {
        using var zip = OpenAppZip(appPath);
        var result = new List<EmittedSource>();
        foreach (var entry in zip.Entries
            .Where(e => e.FullName.StartsWith("bin/", StringComparison.OrdinalIgnoreCase)
                     && e.FullName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.FullName, StringComparer.Ordinal))
        {
            using var s = entry.Open();
            using var reader = new StreamReader(s, Encoding.UTF8);
            result.Add(new EmittedSource(entry.Name, reader.ReadToEnd()));
        }
        return result;
    }

    /// <summary>
    /// Returns the AL `.al` sources from an `.app` package's `src/`. Handles
    /// the R2R nested-app shape (outer ZIP contains a nested `.app` whose
    /// inner ZIP holds `src/*.al`). Returned as (Name, Source) for parity
    /// with v1's AppPackageReader.
    /// </summary>
    public static IReadOnlyList<(string Name, string Source)> ExtractAl(string appPath)
    {
        var bytes = File.ReadAllBytes(appPath);
        var direct = ReadAlFromNavx(bytes);
        if (direct.Count > 0) return direct;

        // R2R nested case.
        using var zipStream = new MemoryStream(bytes, NavxZipOffset(bytes), bytes.Length - NavxZipOffset(bytes));
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read);
        var nested = zip.Entries.FirstOrDefault(e =>
            e.FullName.EndsWith(".app", StringComparison.OrdinalIgnoreCase)
            && !e.FullName.Contains('/'));
        if (nested == null) return Array.Empty<(string, string)>();

        using var ns = nested.Open();
        using var nms = new MemoryStream();
        ns.CopyTo(nms);
        return ReadAlFromNavx(nms.ToArray());
    }

    /// <summary>
    /// Returns report layout resources (`.rdlc`, `.docx`, `.xlsx`) shipped in an `.app`'s
    /// <c>layout/</c> folder, as (FileName, Bytes). A code-bearing report object declares
    /// <c>LayoutFile = './X.rdlc'</c> relative to its source; BC's compile-time layout-embed
    /// step reads that file and NREs (AL1081 "Unable to update report layout … Object reference
    /// not set") if it is absent. The Tier-3 source compile must therefore stage these next to
    /// the extracted `.al` so the relative reference resolves. Handles both the direct NAVX zip
    /// and the R2R nested-.app case, mirroring <see cref="ExtractAl"/>.
    /// </summary>
    public static IReadOnlyList<(string FileName, byte[] Bytes)> ExtractReportLayouts(string appPath)
    {
        var bytes = File.ReadAllBytes(appPath);
        var direct = ReadLayoutsFromNavx(bytes);
        if (direct.Count > 0) return direct;

        // R2R nested case.
        using var zipStream = new MemoryStream(bytes, NavxZipOffset(bytes), bytes.Length - NavxZipOffset(bytes));
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read);
        var nested = zip.Entries.FirstOrDefault(e =>
            e.FullName.EndsWith(".app", StringComparison.OrdinalIgnoreCase)
            && !e.FullName.Contains('/'));
        if (nested == null) return Array.Empty<(string, byte[])>();
        using var ns = nested.Open();
        using var nms = new MemoryStream();
        ns.CopyTo(nms);
        return ReadLayoutsFromNavx(nms.ToArray());
    }

    private static List<(string FileName, byte[] Bytes)> ReadLayoutsFromNavx(byte[] data)
    {
        var offset = NavxZipOffset(data);
        var result = new List<(string, byte[])>();
        using var ms = new MemoryStream(data, offset, data.Length - offset, writable: false);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        foreach (var entry in zip.Entries
            .Where(e => e.FullName.StartsWith("layout/", StringComparison.OrdinalIgnoreCase)
                     && (e.FullName.EndsWith(".rdlc", StringComparison.OrdinalIgnoreCase)
                      || e.FullName.EndsWith(".docx", StringComparison.OrdinalIgnoreCase)
                      || e.FullName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))))
        {
            // The package stores layout names URL-encoded (e.g. "Test%20Report%20-%20Default=RDLC.rdlc");
            // the report's LayoutFile reference uses the decoded name. Decode so the staged file
            // name matches the './<Name>' reference.
            var decoded = Uri.UnescapeDataString(entry.Name);
            using var s = entry.Open();
            using var msEntry = new MemoryStream();
            s.CopyTo(msEntry);
            result.Add((decoded, msEntry.ToArray()));
        }
        return result;
    }

    // ── internals ────────────────────────────────────────────────────────────

    private static ZipArchive OpenAppZip(string appPath)
    {
        var bytes = File.ReadAllBytes(appPath);
        return OpenZipFromNavx(bytes);
    }

    private static ZipArchive OpenZipFromNavx(byte[] bytes)
    {
        var offset = NavxZipOffset(bytes);
        var ms = new MemoryStream(bytes, offset, bytes.Length - offset, writable: false);
        return new ZipArchive(ms, ZipArchiveMode.Read);
    }

    private static int NavxZipOffset(byte[] bytes)
    {
        if (bytes.Length >= 8
            && bytes[0] == (byte)'N' && bytes[1] == (byte)'A'
            && bytes[2] == (byte)'V' && bytes[3] == (byte)'X')
            return (int)BitConverter.ToUInt32(bytes, 4);
        return 0;
    }

    private static List<(string Name, string Source)> ReadAlFromNavx(byte[] data)
    {
        var offset = NavxZipOffset(data);
        var result = new List<(string, string)>();
        using var ms = new MemoryStream(data, offset, data.Length - offset, writable: false);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        foreach (var entry in zip.Entries
            .Where(e => e.FullName.StartsWith("src/", StringComparison.OrdinalIgnoreCase)
                     && e.FullName.EndsWith(".al", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.FullName, StringComparer.Ordinal))
        {
            using var s = entry.Open();
            using var reader = new StreamReader(s, Encoding.UTF8);
            result.Add((entry.Name, reader.ReadToEnd()));
        }
        return result;
    }
}

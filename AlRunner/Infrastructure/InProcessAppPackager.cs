// InProcessAppPackager — emit a source bundle dir as a real .app package in-process.
//
// Strategy: build a synthetic NAVX .app (= 40-byte BC header + zip) that contains
// NavxManifest.xml and all src/*.al files. This is sufficient for:
//   • DependencyResolver.Resolve — reads NavxManifest.xml for identity
//   • AppLoader.ExtractAl — reads src/*.al for Tier-3 compile-on-the-fly
//
// We intentionally do NOT use PackageModuleOutputter: that API requires the AL
// compiler's Compilation.Emit to succeed without AL1153 errors, but the BC 28.x
// artifact packages (runtime 17.0) exceed the v27.5 CodeAnalysis.dll's known
// runtime ceiling (16.1). The manual approach is simpler and fully sufficient for
// the dependency-resolution + DependencyLoader.LoadAll paths.
//
// CONTRACT: the caller must have called BcRuntime.EnsureApplied() and
// DependencyLoader.EnsureResolverInstalled_Public() before invoking EmitAppPackage.

using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace AlRunner.Infrastructure;

/// <summary>
/// Identity read from a bundle's app.json, used to synthesize a NAVX .app.
/// </summary>
public sealed record BundleIdentity(
    Guid AppId,
    string Name,
    string Publisher,
    Version Version,
    Version RuntimeVersion,
    IReadOnlyList<DependencyRef> Dependencies);

public static class InProcessAppPackager
{
    // NAVX header (BC .app format), verified byte-for-byte against a shipped Microsoft
    // package (Microsoft_Application_27.0.38460.53260.app):
    //
    //   0..3    'N','A','V','X'
    //   4..7    LE uint32  — offset of the zip data within the file (= 40)
    //   8..11   LE uint32  — format version (= 2)
    //   12..27  16 bytes   — the app GUID
    //   28..35  LE uint64  — payload (zip) length in bytes
    //   36..39  'N','A','V','X'  — trailing magic, closing the header
    //
    // We used to write a truncated 8-byte header (magic + offset only). BC 28's package
    // reader tolerates it; BC 27's does NOT — it rejects the file outright with AL1023
    // "The package file … is not valid". Because the compiler's native scanner walks whole
    // directories, ONE such package poisons every compile that scans its directory, which is
    // how a single test fixture blocked an entire bundle on BC 27 while looking fine on 28.
    private static readonly byte[] NavxMagic = [(byte)'N', (byte)'A', (byte)'V', (byte)'X'];
    private const uint NavxZipOffset = 40;   // zip begins immediately after the 40-byte header
    private const uint NavxFormatVersion = 2;

    /// <summary>
    /// Read the identity (id/name/publisher/version/runtime/dependencies) from an app.json.
    /// Returns null if the file does not exist or cannot be parsed.
    /// </summary>
    public static BundleIdentity? ReadIdentity(string appJsonPath)
    {
        if (!File.Exists(appJsonPath)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(appJsonPath));
            var root = doc.RootElement;

            var idStr = root.TryGetProperty("id", out var pid) ? pid.GetString() : null;
            var name = root.TryGetProperty("name", out var pn) ? pn.GetString() ?? "Unknown" : "Unknown";
            var pub = root.TryGetProperty("publisher", out var pp) ? pp.GetString() ?? "Unknown" : "Unknown";
            var verStr = root.TryGetProperty("version", out var pv) ? pv.GetString() ?? "1.0.0.0" : "1.0.0.0";
            var rtStr = root.TryGetProperty("runtime", out var pr) ? pr.GetString() ?? "1.0" : "1.0";

            Guid appId = Guid.Empty;
            if (!string.IsNullOrEmpty(idStr)) Guid.TryParse(idStr, out appId);
            if (!Version.TryParse(verStr, out var ver)) ver = new Version(1, 0, 0, 0);
            if (!Version.TryParse(rtStr, out var rtVer)) rtVer = new Version(1, 0);

            var deps = new List<DependencyRef>();
            if (root.TryGetProperty("dependencies", out var depsEl)
                && depsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var d in depsEl.EnumerateArray())
                {
                    var dIdStr = d.TryGetProperty("id", out var di) ? di.GetString() : null;
                    var dName = d.TryGetProperty("name", out var dn) ? dn.GetString() ?? "" : "";
                    var dPub = d.TryGetProperty("publisher", out var dp) ? dp.GetString() ?? "" : "";
                    var dVerStr = d.TryGetProperty("version", out var dv) ? dv.GetString() ?? "0.0.0.0" : "0.0.0.0";
                    Guid dId = Guid.Empty;
                    if (!string.IsNullOrEmpty(dIdStr)) Guid.TryParse(dIdStr, out dId);
                    if (!Version.TryParse(dVerStr, out var dVer)) dVer = new Version(0, 0, 0, 0);
                    deps.Add(new DependencyRef(dId, dName, dPub, dVer));
                }
            }
            // Inject implicit MS deps from application/platform fields (same logic as
            // Program.cs ReadDependencies) so the reference loader resolves them.
            foreach (var (field, implName) in new[] { ("application", "Application"), ("platform", "System") })
            {
                if (root.TryGetProperty(field, out var fv)
                    && fv.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(fv.GetString()))
                {
                    if (!Version.TryParse(fv.GetString(), out var iv)) iv = new Version(0, 0, 0, 0);
                    deps.Add(new DependencyRef(Guid.Empty, implName, "Microsoft", iv, Optional: true));
                }
            }

            return new BundleIdentity(appId, name, pub, ver, rtVer, deps);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[layered] InProcessAppPackager: failed to read {appJsonPath}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// The minimum BC version this app declares it can run against — the higher of app.json's
    /// <c>application</c> and <c>platform</c> floors, or null when it declares neither.
    ///
    /// Both fields are MINIMA in AL, not pins: `"application": "27.0.0.0"` means "needs BC 27.0
    /// or newer". A suite whose floor is above the BC version under test cannot compile, because
    /// the Microsoft/Application + Microsoft/System symbols it asks for do not exist at that
    /// version. Today that surfaces as an emit exclusion with no AL diagnostic attached (the
    /// dependency simply never resolves), which reads as a runner bug and is what made BC 27.x
    /// legs abort before running a test. Callers use this to skip such a suite deliberately and
    /// say so, instead of failing opaquely.
    /// </summary>
    public static Version? ReadMinimumBcVersion(string appJsonPath)
    {
        if (!File.Exists(appJsonPath)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(appJsonPath));
            var root = doc.RootElement;
            Version? floor = null;
            foreach (var field in new[] { "application", "platform" })
            {
                if (root.TryGetProperty(field, out var v)
                    && v.ValueKind == JsonValueKind.String
                    && Version.TryParse(v.GetString(), out var parsed)
                    && (floor == null || parsed > floor))
                    floor = parsed;
            }
            return floor;
        }
        catch (Exception ex)
        {
            // Do not guess a floor from an unreadable manifest — a wrong guess either skips a
            // suite that would have run (silent coverage loss) or admits one that cannot compile.
            Console.Error.WriteLine($"[bc-floor] failed to read {appJsonPath}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Emit a bundle directory as a synthetic NAVX .app package to <paramref name="outPath"/>.
    ///
    /// The .app contains:
    ///   • NavxManifest.xml  — identity, used by DependencyResolver.Resolve
    ///   • src/*.al           — AL sources, used by DependencyLoader Tier-3 compile-on-the-fly
    ///
    /// Throws loudly on any failure — never silently swallows.
    /// </summary>
    public static void EmitAppPackageToFile(
        string bundleDir,
        BundleIdentity identity,
        string outPath,
        byte[]? symbolReferenceJson = null)
    {
        // Collect AL files.
        var alFiles = Directory.EnumerateFiles(bundleDir, "*.al", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
        if (alFiles.Count == 0)
            throw new InvalidOperationException(
                $"[layered] InProcessAppPackager: no .al files found under {bundleDir}");

        // Build NavxManifest.xml content.
        var manifestXml = BuildNavxManifestXml(identity);

        // Write NAVX header + zip to file.
        using var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None);
        WriteNavxApp(fs, identity.AppId, manifestXml, bundleDir, alFiles, symbolReferenceJson);
    }

    // ── internals ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Write the 40-byte NAVX header (see <see cref="NavxZipOffset"/>) followed by the zip
    /// payload to <paramref name="outStream"/>.
    /// </summary>
    private static void WriteNavxApp(
        Stream outStream,
        Guid appId,
        string manifestXml,
        string bundleDir,
        IReadOnlyList<string> alFiles,
        byte[]? symbolReferenceJson = null)
    {
        // Build the zip in its OWN buffer first, so its central-directory / EOCD
        // offsets are relative to the zip's byte 0. If we instead wrote the
        // ZipArchive directly onto outStream AFTER the 8-byte header, the EOCD
        // would record absolute positions that include the header — and reading
        // the zip back from a stream sliced at offset 8 (AppLoader.OpenZipFromNavx)
        // then fails with "Number of entries expected in End Of Central Directory
        // does not correspond to number of entries in Central Directory", so the
        // package is silently unresolvable. Self-contained zip bytes round-trip.
        byte[] zipBytes;
        using (var zipMs = new MemoryStream())
        {
            using (var zip = new ZipArchive(zipMs, ZipArchiveMode.Create, leaveOpen: true))
            {
                // NavxManifest.xml
                var manifestEntry = zip.CreateEntry("NavxManifest.xml", CompressionLevel.Optimal);
                using (var mw = manifestEntry.Open())
                {
                    var xmlBytes = Encoding.UTF8.GetBytes(manifestXml);
                    mw.Write(xmlBytes, 0, xmlBytes.Length);
                }

                // SymbolReference.json — optional; when provided makes the .app valid
                // for BC's package scanner (avoids AL1023 "package not valid"). The
                // standard BC compiler embeds this in every .app it produces; without it
                // BC reports AL1023 when a referencing compilation tries to load the package.
                if (symbolReferenceJson != null)
                {
                    // Each ZipArchiveEntry stream must be fully closed before the next
                    // CreateEntry call — ZipArchive throws IOException otherwise.
                    // Use explicit {} blocks so the using-var goes out of scope before
                    // the next CreateEntry.
                    {
                        var symEntry = zip.CreateEntry("SymbolReference.json", CompressionLevel.Optimal);
                        using var sw = symEntry.Open();
                        sw.Write(symbolReferenceJson, 0, symbolReferenceJson.Length);
                    }

                    // [Content_Types].xml — required by BC's OPC-based package validator.
                    // Without it BC reports AL1023 even when SymbolReference.json is present.
                    // Schema: minimal OPC content-types matching real MS .app format (no BOM,
                    // no space before <Types>). The C# \xNN escape is a Unicode code point,
                    // NOT a raw byte — don't add a BOM via \xEF\xBB\xBF or it double-encodes
                    // to UTF-8 (\xC3\xAF\xC2\xBB\xC2\xBF) which BC's XML reader rejects.
                    {
                        var contentTypesXml = "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
                            "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                            "<Default Extension=\"xml\" ContentType=\"\" />" +
                            "<Default Extension=\"json\" ContentType=\"\" />" +
                            "<Default Extension=\"al\" ContentType=\"\" />" +
                            "<Default Extension=\"png\" ContentType=\"\" />" +
                            "</Types>";
                        var ctEntry = zip.CreateEntry("[Content_Types].xml", CompressionLevel.Optimal);
                        using var cw = ctEntry.Open();
                        var ctBytes = System.Text.Encoding.UTF8.GetBytes(contentTypesXml);
                        cw.Write(ctBytes, 0, ctBytes.Length);
                    }
                }

                // src/<filename>.al for every .al file in the bundle.
                foreach (var alPath in alFiles)
                {
                    var entryName = "src/" + Path.GetFileName(alPath);
                    var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
                    using var ew = entry.Open();
                    using var fr = File.OpenRead(alPath);
                    fr.CopyTo(ew);
                }
            }
            zipBytes = zipMs.ToArray();
        }

        // 40-byte NAVX header (see NavxZipOffset), then the self-contained zip bytes.
        outStream.Write(NavxMagic, 0, 4);
        WriteLe(outStream, BitConverter.GetBytes(NavxZipOffset));
        WriteLe(outStream, BitConverter.GetBytes(NavxFormatVersion));
        outStream.Write(appId.ToByteArray(), 0, 16);
        WriteLe(outStream, BitConverter.GetBytes((ulong)zipBytes.Length));
        outStream.Write(NavxMagic, 0, 4);
        outStream.Write(zipBytes, 0, zipBytes.Length);
    }

    /// <summary>
    /// Write <paramref name="bytes"/> little-endian regardless of host byte order. The NAVX
    /// header is a fixed on-disk format, so it must not inherit the architecture's endianness.
    /// </summary>
    private static void WriteLe(Stream s, byte[] bytes)
    {
        if (!BitConverter.IsLittleEndian) Array.Reverse(bytes);
        s.Write(bytes, 0, bytes.Length);
    }

    /// <summary>
    /// Build a minimal NavxManifest.xml string from a <see cref="BundleIdentity"/>.
    /// AppLoader.ReadManifest reads: App/@Id, @Name, @Publisher, @Version
    /// and Dependencies/Dependency elements.
    /// </summary>
    private static string BuildNavxManifestXml(BundleIdentity identity)
    {
        XNamespace ns = "http://schemas.microsoft.com/navx/2015/manifest";

        var depsEl = new XElement(ns + "Dependencies");
        // Only include the explicit user deps (not the implicit platform/application ones
        // that were injected for reference-loader purposes) so the manifest stays clean.
        foreach (var dep in identity.Dependencies.Where(d => !d.Optional))
        {
            depsEl.Add(new XElement(ns + "Dependency",
                new XAttribute("Id", dep.AppId == Guid.Empty ? "" : dep.AppId.ToString()),
                new XAttribute("Name", dep.Name),
                new XAttribute("Publisher", dep.Publisher),
                new XAttribute("MinVersion", dep.Version.ToString())));
        }

        var doc = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(ns + "Package",
                new XAttribute("xmlns", ns.NamespaceName),
                new XElement(ns + "App",
                    new XAttribute("Id", identity.AppId.ToString()),
                    new XAttribute("Name", identity.Name),
                    new XAttribute("Publisher", identity.Publisher),
                    new XAttribute("Version", identity.Version.ToString()),
                    new XAttribute("ShowMyCode", "true")),
                depsEl));

        // A plain StringWriter is UTF-16-backed, so doc.Save() would emit
        // encoding="utf-16" in the declaration — but the bytes are later written as
        // UTF-8 (WriteNavxApp → Encoding.UTF8.GetBytes). That declaration/encoding
        // mismatch makes XDocument.Load throw when AppLoader.ReadManifest reads the
        // package back, so DependencyResolver silently skips it (the .app becomes
        // unresolvable). Advertise UTF-8 so the declaration matches the bytes.
        using var sw = new Utf8StringWriter();
        doc.Save(sw);
        return sw.ToString();
    }

    /// <summary>StringWriter that reports UTF-8 so Xml serialization emits a
    /// matching <c>encoding="utf-8"</c> declaration (StringWriter is otherwise
    /// UTF-16-backed and would emit <c>encoding="utf-16"</c>).</summary>
    private sealed class Utf8StringWriter : StringWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
    }
}

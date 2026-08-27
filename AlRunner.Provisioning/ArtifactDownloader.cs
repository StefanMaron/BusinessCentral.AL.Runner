// BC dependency downloader — HTTP range requests against the public BC artifact CDN.
//
// This is the single source of truth for fetching BC artifacts. It is called two ways:
//   1. In-process by the runner's auto-provision path (ProvisioningCheck), and
//   2. By the standalone tools/DownloadArtifacts CLI (a thin wrapper) and the
//      AlRunner.csproj MSBuild pre-build target.
//
// Kept deliberately BC-free (HTTP + ZIP only) so it builds before BC's own DLLs exist.
// The ranged-ZIP extraction fetches only the entries we need out of multi-hundred-MB
// artifacts; the full /service/ closure is required for the cold first run (see
// handoff_2026_05_27_cold_ci_artifact_closure). Logic ported verbatim from the former
// top-level DownloadArtifacts program; the only behavioural change is that al-compiler
// selects its NuGet package by RID instead of hardcoding .linux.

using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("AlRunner.Tests")]

namespace AlRunner.Provisioning;

public static class ArtifactDownloader
{
    /// <summary>Public BC artifact CDN base (sandbox channel).</summary>
    public const string CdnBase = "https://bcartifacts-exdbf9fwegejdqak.b02.azurefd.net/sandbox";

    private static Action<string> L(Action<string>? log) => log ?? Console.Error.WriteLine;

    // -----------------------------------------------------------------------
    // AL Compiler: download the NuGet package and extract the cross-platform DLLs.
    // (Not used on the runtime path — the runner emits via BC's Compilation.Emit —
    // but kept for tooling. RID-aware so it works on Windows/macOS/Linux.)
    // -----------------------------------------------------------------------
    public static int AlCompiler(string version, string outputDir, Action<string>? log = null)
    {
        var logf = L(log);
        var packageId = AlCompilerPackageId();
        var url = $"https://api.nuget.org/v3-flatcontainer/{packageId}/{version}/{packageId}.{version}.nupkg";

        Directory.CreateDirectory(outputDir);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        logf($"Downloading AL compiler {version} from NuGet ({packageId})...");

        byte[] nupkg;
        try
        {
            using var resp = http.Send(new HttpRequestMessage(HttpMethod.Get, url));
            resp.EnsureSuccessStatusCode();
            using var ms = new MemoryStream();
            resp.Content.ReadAsStream().CopyTo(ms);
            nupkg = ms.ToArray();
        }
        catch (Exception ex)
        {
            logf($"Error downloading: {ex.Message}");
            return 1;
        }

        logf($"Downloaded {nupkg.Length / 1048576} MB");

        int extracted = 0;
        using var zipStream = new MemoryStream(nupkg);
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read);
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            // v16 uses tools/net8.0/any/, v17+ uses lib/net8.0/ — both cross-platform.
            if (!name.StartsWith("tools/net8.0/any/", StringComparison.OrdinalIgnoreCase)
                && !name.StartsWith("lib/net8.0/", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                continue;

            var outPath = Path.Combine(outputDir, Path.GetFileName(name));
            using var entryStream = entry.Open();
            using var outFile = File.Create(outPath);
            entryStream.CopyTo(outFile);
            extracted++;
        }

        logf($"Extracted {extracted} DLLs to {outputDir}");
        return extracted > 0 ? 0 : 1;
    }

    // The BC AL compiler ships as OS-specific NuGet packages; the DLLs under
    // tools|lib/net8.0/(any) are cross-platform but the *package id* is not.
    private static string AlCompilerPackageId()
    {
        const string @base = "microsoft.dynamics.businesscentral.development.tools";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return @base;      // win pkg has no suffix
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return @base + ".osx";
        return @base + ".linux";
    }

    // -----------------------------------------------------------------------
    // Service Tier: the ~55-DLL /service/ closure from the platform artifact.
    // -----------------------------------------------------------------------
    public static int ServiceTier(string version, string outputDir, Action<string>? log = null)
    {
        var logf = L(log);
        var artifactUrl = $"{CdnBase}/{version}/platform";
        Directory.CreateDirectory(outputDir);

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

        logf($"Resolving artifact size for BC {version}...");
        if (!TryHeadContentLength(http, artifactUrl, version, "platform", logf, out long totalSize)) return 1;
        if (totalSize == 0) { logf("Error: unknown size"); return 1; }
        logf($"Platform artifact: {totalSize / 1048576} MB");

        logf("Downloading ZIP directory...");
        if (!TryReadCentralDirectory(http, artifactUrl, totalSize, logf, out var cdData, out var cdStart, out var entryCount))
            return 1;

        // Collect every *.dll anywhere under a ServiceTier .../Service/ path, then keep one
        // copy per file name preferring the shallowest path (the server runtime's own copy in
        // .../Service/ over a tooling copy in .../Service/Admin|Management/). The FULL closure
        // is required — not just top-level Nav DLLs — because (1) the load-time Cecil rewrite of
        // Ncl.dll re-serializes the whole module so Mono.Cecil must resolve every referenced
        // type, and (2) the Default-ALC fallback resolver loads version-pinned assemblies (e.g.
        // Microsoft.Extensions.Logging.Abstractions v8) that live only under Service/Admin|Management/.
        // A partial set fails the cold first run. See handoff_2026_05_27_cold_ci_artifact_closure.
        var byName = new Dictionary<string, (string Name, int Method, long CompSize, long Offset, int Depth)>();
        int pos = cdStart;
        for (int i = 0; i < entryCount && pos + 46 <= cdData.Length; i++)
        {
            if (!IsCentralHeader(cdData, pos)) break;
            var (cm, cs, nl, el, cl, lo, name) = ReadCentralEntry(cdData, pos);
            var lower = name.ToLowerInvariant();
            var bn = Path.GetFileName(lower);
            if (lower.Contains("servicetier/") && lower.Contains("/service/") &&
                bn.EndsWith(".dll") && cs > 0)
            {
                int depth = lower.Split("/service/").Last().Count(ch => ch == '/');
                if (!byName.TryGetValue(bn, out var existing) || depth < existing.Depth)
                    byName[bn] = (name, cm, cs, lo, depth);
            }
            pos += 46 + nl + el + cl;
        }

        var matching = byName.Values.Select(v => (v.Name, v.Method, v.CompSize, v.Offset)).ToList();
        if (matching.Count == 0) { logf("Error: no service-tier DLLs found"); return 1; }
        logf($"Found {matching.Count} service-tier DLLs (full /service/ closure, deduped by name)");

        matching.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        long totalBytes = 0;
        int extracted = 0;
        foreach (var (name, method, compSize, offset) in matching)
        {
            var fileData = ExtractEntry(http, artifactUrl, totalSize, name, method, compSize, offset, logf);
            if (fileData == null) continue;
            File.WriteAllBytes(Path.Combine(outputDir, Path.GetFileName(name)), fileData);
            totalBytes += fileData.Length;
            extracted++;
            if (extracted % 50 == 0)
                logf($"  …{extracted}/{matching.Count} extracted ({totalBytes / 1048576} MB)");
        }

        logf($"Downloaded {extracted} DLLs ({totalBytes / 1048576} MB) to {outputDir}");
        return extracted > 0 ? 0 : 1;
    }

    // -----------------------------------------------------------------------
    // Test Apps: test-toolkit .app files under Applications/<area>/Test/*.app in the
    // platform artifact (NOT part of the w1/Extensions set platform-apps fetches).
    // -----------------------------------------------------------------------
    public static int TestApps(string version, string outputDir, Action<string>? log = null)
    {
        var logf = L(log);
        var artifactUrl = $"{CdnBase}/{version}/platform";
        Directory.CreateDirectory(outputDir);

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

        logf($"Resolving artifact size for BC {version} (platform)...");
        if (!TryHeadContentLength(http, artifactUrl, version, "platform", logf, out long totalSize)) return 1;
        if (totalSize == 0) { logf("Error: unknown size"); return 1; }

        logf("Downloading ZIP directory...");
        if (!TryReadCentralDirectory(http, artifactUrl, totalSize, logf, out var cdData, out var cdStart, out var entryCount))
            return 1;

        var matching = new List<(string Name, int Method, long CompSize, long Offset)>();
        int pos = cdStart;
        for (int i = 0; i < entryCount && pos + 46 <= cdData.Length; i++)
        {
            if (!IsCentralHeader(cdData, pos)) break;
            var (cm, cs, nl, el, cl, lo, name) = ReadCentralEntry(cdData, pos);
            var lower = name.ToLowerInvariant();
            // "/test/" alone MISSES the actual test toolkit: Library Assert, Test Runner,
            // Any and Library Variable Storage ship under Applications/TestFramework/
            // TestLibraries/... and TestFramework/TestRunner/..., which contain no "/test/"
            // segment. Those four are exactly what a test bundle's app.json depends on, so
            // the old filter fetched 97 country test apps and none of the packages anyone
            // actually needs — leaving --package-cache mandatory with no way to populate it.
            if (lower.EndsWith(".app") && cs > 0
                && (lower.Contains("/test/") || lower.Contains("testframework")
                    || lower.Contains("testlibraries") || lower.Contains("testrunner")))
                matching.Add((name, cm, cs, lo));
            pos += 46 + nl + el + cl;
        }

        if (matching.Count == 0) { logf("Error: no test .app files found"); return 1; }
        logf($"Found {matching.Count} test-toolkit .app files");

        matching.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        long totalBytes = 0; int extracted = 0;
        foreach (var (name, method, compSize, offset) in matching)
        {
            var fileData = ExtractEntry(http, artifactUrl, totalSize, name, method, compSize, offset, logf);
            if (fileData == null) continue;
            File.WriteAllBytes(Path.Combine(outputDir, Path.GetFileName(name)), fileData);
            totalBytes += fileData.Length; extracted++;
            logf($"  Written {Path.GetFileName(name)} ({fileData.Length / 1048576} MB)");
        }
        logf($"Downloaded {extracted} test .app file(s) ({totalBytes / 1048576} MB) to {outputDir}");
        return extracted > 0 ? 0 : 1;
    }

    // -----------------------------------------------------------------------
    // Platform Apps: Microsoft Base/System/BusinessFoundation/Application .app files
    // from the w1 artifact's Extensions/ folder.
    // -----------------------------------------------------------------------
    public static int PlatformApps(string version, string outputDir, Action<string>? log = null)
    {
        var logf = L(log);
        var artifactUrl = $"{CdnBase}/{version}/w1";
        Directory.CreateDirectory(outputDir);

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

        logf($"Resolving artifact size for BC {version} (w1)...");
        if (!TryHeadContentLength(http, artifactUrl, version, "w1", logf, out long totalSize)) return 1;
        if (totalSize == 0) { logf("Error: unknown size"); return 1; }
        logf($"w1 artifact: {totalSize / 1048576} MB");

        logf("Downloading ZIP directory...");
        if (!TryReadCentralDirectory(http, artifactUrl, totalSize, logf, out var cdData, out var cdStart, out var entryCount))
            return 1;

        var wantedPrefixes = new[]
        {
            "microsoft_base application_",
            "microsoft_system application_",
            "microsoft_business foundation_",
            "microsoft_application_",
            // Ships in w1/Extensions like the four above, NOT in the platform artifact the
            // `test-apps` command streams — so `test-apps` cannot supply it however it is
            // filtered. A test bundle depending on it (tests/runner-extras/microsoft-dependencies)
            // was therefore unresolvable on any machine without a full BC sandbox artifact,
            // which is every CI runner: the leg aborted with the provisioning-gap message
            // before running a test, while passing locally off a multi-GB sandbox download.
            "microsoft_application test library_",
        };

        var matching = new List<(string Name, int Method, long CompSize, long Offset)>();
        int pos = cdStart;
        for (int i = 0; i < entryCount && pos + 46 <= cdData.Length; i++)
        {
            if (!IsCentralHeader(cdData, pos)) break;
            var (cm, cs, nl, el, cl, lo, name) = ReadCentralEntry(cdData, pos);
            var lower = name.ToLowerInvariant();
            var bn = Path.GetFileName(lower);
            if (lower.StartsWith("extensions/") && lower.EndsWith(".app") && cs > 0
                && Array.Exists(wantedPrefixes, p => bn.StartsWith(p)))
                matching.Add((name, cm, cs, lo));
            pos += 46 + nl + el + cl;
        }

        if (matching.Count == 0) { logf("Error: no platform .app files found"); return 1; }
        logf($"Found {matching.Count} platform app(s):");
        foreach (var (name, _, compSize, _) in matching)
            logf($"  {Path.GetFileName(name)}  ({compSize / 1048576} MB compressed)");

        matching.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        long totalBytes = 0;
        int extracted = 0;
        foreach (var (name, method, compSize, offset) in matching)
        {
            var basename = Path.GetFileName(name);
            logf($"  Downloading {basename}...");
            var fileData = ExtractEntry(http, artifactUrl, totalSize, name, method, compSize, offset, logf);
            if (fileData == null) continue;
            File.WriteAllBytes(Path.Combine(outputDir, basename), fileData);
            totalBytes += fileData.Length;
            extracted++;
            logf($"  Written {basename} ({fileData.Length / 1048576} MB)");
        }

        // Microsoft/System — the platform symbol package. It is NOT in the w1 artifact's
        // Extensions/ folder with the four apps above; it ships in the PLATFORM artifact under
        // ModernDev/.../AL Development Environment/System.app, so it needs its own pass over a
        // second artifact.
        //
        // Why this matters: without it the compile falls back to whatever System.app a bundle
        // happens to carry in its own .alpackages. The al-language corpus carries 27.0.46760.0,
        // and AL compiler 17.0.39.53543 (BC 28.1.49838.53220) rejects it with
        //   AL1022: A package with publisher 'Microsoft', name 'System', and a version
        //           compatible with '28.0.0.0' could not be found
        // That one miss cascades: Table 'Integer' (a System virtual table) goes missing,
        // "Global Triggers" fails to bind, three Report objects fail to emit, and the emit-retry
        // loop drops the two test codeunits that referenced them — 7 corpus tests, gone. The
        // older compiler (17.0.36.40629) accepted the 27.0 package, which is why this only
        // appeared when CI moved to a newer BC build.
        extracted += SystemApp(version, outputDir, logf);

        logf($"Downloaded {extracted} app(s) ({totalBytes / 1048576} MB total) to {outputDir}");
        return extracted > 0 ? 0 : 1;
    }

    /// <summary>
    /// Extracts Microsoft's System.app (the platform symbol package) from the platform
    /// artifact into <paramref name="outputDir"/>. Returns the number of files written (0 or 1).
    /// </summary>
    private static int SystemApp(string version, string outputDir, Action<string> logf)
    {
        var artifactUrl = $"{CdnBase}/{version}/platform";
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

        logf($"Resolving platform artifact for System.app (BC {version})...");
        if (!TryHeadContentLength(http, artifactUrl, version, "platform", logf, out long totalSize))
        {
            logf("Warning: skipping System.app");
            return 0;
        }
        if (totalSize == 0) { logf("Warning: could not size the platform artifact — skipping System.app"); return 0; }

        if (!TryReadCentralDirectory(http, artifactUrl, totalSize, logf, out var cdData, out var cdStart, out var entryCount))
        {
            logf("Warning: could not read the platform artifact directory — skipping System.app");
            return 0;
        }

        int pos = cdStart;
        for (int i = 0; i < entryCount && pos + 46 <= cdData.Length; i++)
        {
            if (!IsCentralHeader(cdData, pos)) break;
            var (cm, cs, nl, el, cl, lo, name) = ReadCentralEntry(cdData, pos);
            var lower = name.ToLowerInvariant();
            // Anchor on the AL Development Environment folder: the artifact also carries
            // per-version copies elsewhere, and this is the one the AL compiler ships with.
            if (Path.GetFileName(lower) == "system.app" && cs > 0
                && lower.Contains("al development environment"))
            {
                logf($"  Downloading System.app ({cs / 1024} KB compressed)...");
                var fileData = ExtractEntry(http, artifactUrl, totalSize, name, cm, cs, lo, logf);
                if (fileData == null) break;
                File.WriteAllBytes(Path.Combine(outputDir, "System.app"), fileData);
                logf($"  Written System.app ({fileData.Length / 1024} KB)");
                return 1;
            }
            pos += 46 + nl + el + cl;
        }

        logf("Warning: System.app not found in the platform artifact");
        return 0;
    }

    // -----------------------------------------------------------------------
    // Cheap existence probe for an EXACT 4-part version (issue #2033): a single HEAD
    // request against the platform artifact, no download and no ZIP central-directory
    // read. Used by BcArtifacts.DefaultProvisionTarget to check whether the engine's own
    // exact build is fetchable before deciding to fall back to a looser tier (minor, then
    // major). ResolveVersion below answers a different question (latest build matching a
    // PREFIX); this answers "does this exact version exist at all".
    // -----------------------------------------------------------------------
    public static bool VersionExists(string version, Action<string>? log = null)
    {
        var logf = L(log);
        var url = $"{CdnBase}/{version}/platform";
        try
        {
            using var http = new HttpClient();
            using var resp = http.Send(new HttpRequestMessage(HttpMethod.Head, url));
            return resp.IsSuccessStatusCode;
        }
        catch (HttpRequestException ex)
        {
            logf($"[provision] could not probe BC {version} on the CDN: {ex.Message}");
            return false;
        }
    }

    // -----------------------------------------------------------------------
    // Resolve a BC version prefix (e.g. "28.2") to the latest full version via
    // Microsoft's public index. Returns null when nothing matches.
    // -----------------------------------------------------------------------
    public static string? ResolveVersion(string prefix, Action<string>? log = null)
    {
        var logf = L(log);
        var indexUrl = $"{CdnBase}/indexes/w1.json";
        logf($"Resolving BC version prefix '{prefix}'...");

        string json;
        try { using var http = new HttpClient(); json = http.GetStringAsync(indexUrl).Result; }
        catch (Exception ex) { logf($"Error fetching index: {ex.Message}"); return null; }

        var searchPrefix = prefix + ".";
        var versions = new List<string>();
        int idx = 0;
        while ((idx = json.IndexOf("\"Version\"", idx, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            idx = json.IndexOf(':', idx); if (idx < 0) break;
            idx = json.IndexOf('"', idx + 1); if (idx < 0) break;
            int end = json.IndexOf('"', idx + 1); if (end < 0) break;
            var ver = json.Substring(idx + 1, end - idx - 1);
            if (ver.StartsWith(searchPrefix)) versions.Add(ver);
            idx = end + 1;
        }

        if (versions.Count == 0) { logf($"No versions found for prefix '{prefix}'"); return null; }

        versions.Sort((a, b) =>
        {
            var pa = a.Split('.').Select(int.Parse).ToArray();
            var pb = b.Split('.').Select(int.Parse).ToArray();
            for (int i = 0; i < Math.Min(pa.Length, pb.Length); i++)
            {
                var cmp = pa[i].CompareTo(pb[i]);
                if (cmp != 0) return cmp;
            }
            return pa.Length.CompareTo(pb.Length);
        });

        var resolved = versions.Last();
        logf($"Resolved: {prefix} -> {resolved}");
        return resolved;
    }

    // ----------------------------- ZIP helpers -----------------------------

    private static long HeadContentLength(HttpClient http, string url)
    {
        using var headResp = http.Send(new HttpRequestMessage(HttpMethod.Head, url));
        headResp.EnsureSuccessStatusCode();
        return headResp.Content.Headers.ContentLength ?? 0;
    }

    /// <summary>
    /// Sizes a remote artifact and turns a failure into a named, actionable log message
    /// instead of letting <see cref="HttpRequestException"/> propagate as an unhandled
    /// exception with a raw .NET stack trace. A 404 (no artifact published for that exact
    /// version) gets the <c>resolve-version</c> pointer; any other transport failure
    /// (DNS, TLS, timeout, 5xx) gets a distinct "could not reach the CDN" message so the
    /// caller can tell "your version is wrong" from "the network/tool is broken" — the
    /// two categories the raw stack trace collapsed into one indistinguishable crash.
    /// </summary>
    internal static bool TryHeadContentLength(
        HttpClient http, string url, string version, string channel, Action<string> logf, out long size)
    {
        try
        {
            size = HeadContentLength(http, url);
            return true;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            var prefix = string.Join(".", version.Split('.').Take(2));
            logf($"Error: no BC artifact published for {version} ({channel}).");
            logf("       Check the version, or resolve the latest for a prefix:");
            // Issue #2085: this fires both from the standalone tools/DownloadArtifacts CLI
            // (repo-checkout only) AND in-process from the shipped `al-runner` binary's own
            // auto-provision path — a `dotnet run --project tools/DownloadArtifacts` hint
            // here would be a dead end for anyone using the latter, which is the common
            // case. `al-runner provision --resolve-version` works from both.
            logf($"         al-runner provision --resolve-version {prefix}");
            size = 0;
            return false;
        }
        catch (HttpRequestException ex)
        {
            logf($"Error: could not reach the BC artifact CDN for {version} ({channel}): {ex.Message}");
            size = 0;
            return false;
        }
    }

    // Read the ZIP End-Of-Central-Directory + central directory bytes for a remote
    // artifact. Returns false (after logging) when the EOCD can't be located.
    private static bool TryReadCentralDirectory(
        HttpClient http, string url, long totalSize, Action<string> logf,
        out byte[] cdData, out int cdStart, out int entryCount)
    {
        cdData = Array.Empty<byte>(); cdStart = 0; entryCount = 0;
        var tail = DownloadRange(http, url, totalSize - 65536, totalSize - 1);
        int eocdPos = -1;
        for (int i = tail.Length - 22; i >= 0; i--)
            if (tail[i] == 0x50 && tail[i + 1] == 0x4b && tail[i + 2] == 0x05 && tail[i + 3] == 0x06)
            { eocdPos = i; break; }
        if (eocdPos < 0) { logf("Error: EOCD not found"); return false; }

        entryCount = BitConverter.ToUInt16(tail, eocdPos + 10);
        uint cdOffset = BitConverter.ToUInt32(tail, eocdPos + 16);

        long cdInTail = tail.Length - (totalSize - cdOffset);
        if (cdInTail >= 0) { cdData = tail; cdStart = (int)cdInTail; }
        else { logf("Downloading central directory..."); cdData = DownloadRange(http, url, cdOffset, totalSize - 1); cdStart = 0; }
        return true;
    }

    private static bool IsCentralHeader(byte[] cd, int pos)
        => cd[pos] == 0x50 && cd[pos + 1] == 0x4b && cd[pos + 2] == 0x01 && cd[pos + 3] == 0x02;

    private static (int Method, uint CompSize, int NameLen, int ExtraLen, int CommentLen, uint LocalOffset, string Name)
        ReadCentralEntry(byte[] cd, int pos)
    {
        int cm = BitConverter.ToUInt16(cd, pos + 10);
        uint cs = BitConverter.ToUInt32(cd, pos + 20);
        int nl = BitConverter.ToUInt16(cd, pos + 28);
        int el = BitConverter.ToUInt16(cd, pos + 30);
        int cl = BitConverter.ToUInt16(cd, pos + 32);
        uint lo = BitConverter.ToUInt32(cd, pos + 42);
        var name = Encoding.UTF8.GetString(cd, pos + 46, Math.Min(nl, cd.Length - (pos + 46))).Replace('\\', '/');
        return (cm, cs, nl, el, cl, lo, name);
    }

    // Fetch and decompress a single ZIP entry by its central-directory metadata.
    // Returns null (after a warning) on a bad/truncated header or unsupported method.
    private static byte[]? ExtractEntry(
        HttpClient http, string url, long totalSize,
        string name, int method, long compSize, long offset, Action<string> logf)
    {
        // Local file header (30 bytes) + filename + extra field, then compressed data.
        // The local header's extra-field length can differ from the central directory's,
        // so over-fetch a header margin and parse the real lengths from the local header.
        long headerMargin = 30 + name.Length + 4096;
        long entryEnd = Math.Min(offset + headerMargin + compSize, totalSize - 1);
        var data = DownloadRange(http, url, offset, entryEnd);

        if (data.Length < 30 || data[0] != 0x50 || data[1] != 0x4b || data[2] != 0x03 || data[3] != 0x04)
        {
            logf($"  WARNING: bad local header for {Path.GetFileName(name)} — skipping");
            return null;
        }
        int nl2 = BitConverter.ToUInt16(data, 26);
        int el2 = BitConverter.ToUInt16(data, 28);
        int ds = 30 + nl2 + el2;
        if (ds + compSize > data.Length)
        {
            entryEnd = Math.Min(offset + ds + compSize, totalSize - 1);
            data = DownloadRange(http, url, offset, entryEnd);
            if (ds + compSize > data.Length)
            {
                logf($"  WARNING: truncated data for {Path.GetFileName(name)} — skipping");
                return null;
            }
        }

        if (method == 0)
        {
            var fileData = new byte[compSize];
            Array.Copy(data, ds, fileData, 0, (int)compSize);
            return fileData;
        }
        if (method == 8)
        {
            using var cs2 = new MemoryStream(data, ds, (int)compSize);
            using var df = new DeflateStream(cs2, CompressionMode.Decompress);
            using var o = new MemoryStream();
            df.CopyTo(o);
            return o.ToArray();
        }
        logf($"  WARNING: unsupported compression method {method} for {Path.GetFileName(name)} — skipping");
        return null;
    }

    private static byte[] DownloadRange(HttpClient http, string url, long from, long to)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Range = new RangeHeaderValue(from, to);
                using var resp = http.Send(req);
                resp.EnsureSuccessStatusCode();
                using var ms = new MemoryStream();
                resp.Content.ReadAsStream().CopyTo(ms);
                return ms.ToArray();
            }
            catch when (attempt == 0)
            {
                Console.Error.WriteLine("  Retrying download...");
            }
        }
        throw new Exception($"Failed to download range {from}-{to}");
    }
}

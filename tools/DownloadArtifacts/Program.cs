// Standalone tool to download BC dependencies via HTTP range requests.
// Used by MSBuild pre-build targets when DLLs aren't present locally.
//
// Modes:
//   DownloadArtifacts service-tier <bc-version> <output-dir>
//     Downloads ~55 Microsoft.Dynamics.Nav.*.dll from the BC platform artifact (~11 MB).
//
//   DownloadArtifacts al-compiler <tool-version> <output-dir>
//     Downloads the AL compiler NuGet package and extracts the needed DLLs (~57 MB).
//
//   DownloadArtifacts platform-apps <bc-version> <output-dir>
//     Downloads Microsoft platform .app files (Base/System/Business Foundation/Application)
//     from the BC w1 sandbox artifact via HTTP range requests.

using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: DownloadArtifacts service-tier <bc-version> <output-dir>");
    Console.Error.WriteLine("       DownloadArtifacts al-compiler <tool-version> <output-dir>");
    Console.Error.WriteLine("       DownloadArtifacts platform-apps <bc-version> <output-dir>");
    Console.Error.WriteLine("       DownloadArtifacts resolve-version <bc-prefix>");
    return 1;
}

var mode = args[0];
var version = args[1];
var outputDir = args.Length >= 3 ? args[2] : "";

return mode switch
{
    "service-tier" => DownloadServiceTier(version, outputDir),
    "al-compiler" => DownloadAlCompiler(version, outputDir),
    "platform-apps" => DownloadPlatformApps(version, outputDir),
    "resolve-version" => ResolveVersion(version),
    _ => Error($"Unknown mode: {mode}")
};

// ---------------------------------------------------------------------------
// AL Compiler: download NuGet package and extract DLLs
// ---------------------------------------------------------------------------
static int DownloadAlCompiler(string version, string outputDir)
{
    // The NuGet package name varies by platform but DLLs in tools/net8.0/any/ are cross-platform
    var packageId = "microsoft.dynamics.businesscentral.development.tools.linux";
    var url = $"https://api.nuget.org/v3-flatcontainer/{packageId}/{version}/{packageId}.{version}.nupkg";

    Directory.CreateDirectory(outputDir);
    using var http = new HttpClient();
    http.Timeout = TimeSpan.FromMinutes(5);

    Console.Error.WriteLine($"Downloading AL compiler {version} from NuGet...");

    // Download the full NuGet package (it's ~57 MB, small enough to download whole)
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
        Console.Error.WriteLine($"Error downloading: {ex.Message}");
        return 1;
    }

    Console.Error.WriteLine($"Downloaded {nupkg.Length / 1048576} MB");

    // Extract DLLs from tools/net8.0/any/ (cross-platform path)
    int extracted = 0;
    using var zipStream = new MemoryStream(nupkg);
    using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read);
    foreach (var entry in zip.Entries)
    {
        var name = entry.FullName.Replace('\\', '/');
        // v16 uses tools/net8.0/any/, v17+ uses lib/net8.0/
        if (!name.StartsWith("tools/net8.0/any/", StringComparison.OrdinalIgnoreCase)
            && !name.StartsWith("lib/net8.0/", StringComparison.OrdinalIgnoreCase))
            continue;
        if (!name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            continue;

        var basename = Path.GetFileName(name);
        var outPath = Path.Combine(outputDir, basename);
        using var entryStream = entry.Open();
        using var outFile = File.Create(outPath);
        entryStream.CopyTo(outFile);
        extracted++;
    }

    Console.Error.WriteLine($"Extracted {extracted} DLLs to {outputDir}");
    return extracted > 0 ? 0 : 1;
}

// ---------------------------------------------------------------------------
// Service Tier: download Nav DLLs via HTTP range requests
// ---------------------------------------------------------------------------
static int DownloadServiceTier(string version, string outputDir)
{
    var artifactUrl = $"https://bcartifacts-exdbf9fwegejdqak.b02.azurefd.net/sandbox/{version}/platform";
    Directory.CreateDirectory(outputDir);

    using var handler = new HttpClientHandler();
    using var http = new HttpClient(handler);
    http.Timeout = TimeSpan.FromMinutes(5);

    Console.Error.WriteLine($"Resolving artifact size for BC {version}...");
    var headReq = new HttpRequestMessage(HttpMethod.Head, artifactUrl);
    var headResp = http.Send(headReq);
    headResp.EnsureSuccessStatusCode();
    var totalSize = headResp.Content.Headers.ContentLength ?? 0;
    headResp.Dispose();
    if (totalSize == 0) { Console.Error.WriteLine("Error: unknown size"); return 1; }
    Console.Error.WriteLine($"Platform artifact: {totalSize / 1048576} MB");

    Console.Error.WriteLine("Downloading ZIP directory...");
    var tail = DownloadRange(http, artifactUrl, totalSize - 65536, totalSize - 1);

    int eocdPos = -1;
    for (int i = tail.Length - 22; i >= 0; i--)
        if (tail[i] == 0x50 && tail[i + 1] == 0x4b && tail[i + 2] == 0x05 && tail[i + 3] == 0x06)
        { eocdPos = i; break; }
    if (eocdPos < 0) { Console.Error.WriteLine("Error: EOCD not found"); return 1; }

    int entryCount = BitConverter.ToUInt16(tail, eocdPos + 10);
    uint cdOffset = BitConverter.ToUInt32(tail, eocdPos + 16);

    byte[] cdData; int cdStart;
    long cdInTail = tail.Length - (totalSize - cdOffset);
    if (cdInTail >= 0) { cdData = tail; cdStart = (int)cdInTail; }
    else
    {
        Console.Error.WriteLine("Downloading central directory...");
        cdData = DownloadRange(http, artifactUrl, cdOffset, totalSize - 1);
        cdStart = 0;
    }

    // Collect every *.dll anywhere under a ServiceTier .../Service/ path, then keep one
    // copy per file name preferring the shallowest path (the server runtime's own copy in
    // .../Service/ over a tooling copy in .../Service/Admin|Management/). We need this FULL
    // closure — not just the top-level Nav DLLs — for TWO reasons:
    //   1. The load-time Cecil rewrite of Ncl.dll re-serializes the whole module, so
    //      Mono.Cecil must resolve every type referenced by a constant (default parameter
    //      value), including third-party assemblies like Microsoft.Exchange.WebServices.NETStandard.
    //   2. At runtime the Default-ALC Resolving handler (DependencyLoader) loads BC
    //      dependencies from this dir by simple name. It is fallback-only (fires only when
    //      default resolution fails), so it never shadows the net10 BCL — but it MUST be
    //      able to find version-pinned assemblies the net10 SDK does not carry, e.g.
    //      Microsoft.Extensions.Logging.Abstractions v8.0.0.0 (BcRuntime.ApplyAllPatches
    //      binds to it). Those runtime assemblies live only under Service/Admin|Management/,
    //      not the top level, which is why subdirectories must be included.
    // A partial set fails the cold first run — either exit 134 (Cecil) or a runtime
    // FileNotFoundException. The fallback-only resolver makes the full closure safe.
    // See handoff_2026_05_27_cold_ci_artifact_closure.
    var byName = new Dictionary<string, (string Name, int Method, long CompSize, long Offset, int Depth)>();
    int pos = cdStart;
    for (int i = 0; i < entryCount && pos + 46 <= cdData.Length; i++)
    {
        if (cdData[pos] != 0x50 || cdData[pos + 1] != 0x4b || cdData[pos + 2] != 0x01 || cdData[pos + 3] != 0x02) break;
        int cm = BitConverter.ToUInt16(cdData, pos + 10);
        uint cs = BitConverter.ToUInt32(cdData, pos + 20);
        int nl = BitConverter.ToUInt16(cdData, pos + 28);
        int el = BitConverter.ToUInt16(cdData, pos + 30);
        int cl = BitConverter.ToUInt16(cdData, pos + 32);
        uint lo = BitConverter.ToUInt32(cdData, pos + 42);
        if (pos + 46 + nl > cdData.Length) break;
        var name = Encoding.UTF8.GetString(cdData, pos + 46, nl).Replace('\\', '/');
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

    var matching = byName.Values
        .Select(v => (v.Name, v.Method, v.CompSize, v.Offset))
        .ToList();

    if (matching.Count == 0) { Console.Error.WriteLine("Error: no service-tier DLLs found"); return 1; }
    Console.Error.WriteLine($"Found {matching.Count} service-tier DLLs (full /service/ closure, deduped by name)");

    // Download each entry's byte range individually. The matching DLLs are scattered
    // through the artifact among files we don't want (a single first→last contiguous
    // range would pull hundreds of MB of interstitial content), so a per-entry range
    // request fetches only the ~290 MB closure we actually need.
    matching.Sort((a, b) => a.Offset.CompareTo(b.Offset));
    long totalBytes = 0;
    int extracted = 0;
    foreach (var (name, method, compSize, offset) in matching)
    {
        // Local file header (30 bytes) + filename + extra field, then compressed data.
        // The extra field length in the local header can differ from the central
        // directory's, so over-fetch a generous header margin and parse the real
        // lengths from the downloaded local header.
        long headerMargin = 30 + name.Length + 4096;
        long entryEnd = Math.Min(offset + headerMargin + compSize, totalSize - 1);
        var data = DownloadRange(http, artifactUrl, offset, entryEnd);

        if (data.Length < 30 || data[0] != 0x50 || data[1] != 0x4b || data[2] != 0x03 || data[3] != 0x04)
        {
            Console.Error.WriteLine($"  WARNING: bad local header for {Path.GetFileName(name)} — skipping");
            continue;
        }
        int nl2 = BitConverter.ToUInt16(data, 26);
        int el2 = BitConverter.ToUInt16(data, 28);
        int ds = 30 + nl2 + el2;
        if (ds + compSize > data.Length)
        {
            // Extra field larger than our margin (rare) — re-fetch with exact bounds.
            entryEnd = Math.Min(offset + ds + compSize, totalSize - 1);
            data = DownloadRange(http, artifactUrl, offset, entryEnd);
            if (ds + compSize > data.Length)
            {
                Console.Error.WriteLine($"  WARNING: truncated data for {Path.GetFileName(name)} — skipping");
                continue;
            }
        }

        byte[] fileData;
        if (method == 0)
        {
            fileData = new byte[compSize];
            Array.Copy(data, ds, fileData, 0, (int)compSize);
        }
        else if (method == 8)
        {
            using var cs2 = new MemoryStream(data, ds, (int)compSize);
            using var df = new DeflateStream(cs2, CompressionMode.Decompress);
            using var o = new MemoryStream();
            df.CopyTo(o);
            fileData = o.ToArray();
        }
        else continue;

        File.WriteAllBytes(Path.Combine(outputDir, Path.GetFileName(name)), fileData);
        totalBytes += fileData.Length;
        extracted++;
        if (extracted % 50 == 0)
            Console.Error.WriteLine($"  …{extracted}/{matching.Count} extracted ({totalBytes / 1048576} MB)");
    }

    Console.Error.WriteLine($"Downloaded {extracted} DLLs ({totalBytes / 1048576} MB) to {outputDir}");
    return extracted > 0 ? 0 : 1;
}

// ---------------------------------------------------------------------------
// Platform Apps: download Microsoft .app files via HTTP range requests
// ---------------------------------------------------------------------------
static int DownloadPlatformApps(string version, string outputDir)
{
    // The platform apps (Base Application, System Application, etc.) live in the /w1 artifact.
    var artifactUrl = $"https://bcartifacts-exdbf9fwegejdqak.b02.azurefd.net/sandbox/{version}/w1";
    Directory.CreateDirectory(outputDir);

    using var handler = new HttpClientHandler();
    using var http = new HttpClient(handler);
    http.Timeout = TimeSpan.FromMinutes(10);

    Console.Error.WriteLine($"Resolving artifact size for BC {version} (w1)...");
    var headReq = new HttpRequestMessage(HttpMethod.Head, artifactUrl);
    var headResp = http.Send(headReq);
    headResp.EnsureSuccessStatusCode();
    var totalSize = headResp.Content.Headers.ContentLength ?? 0;
    headResp.Dispose();
    if (totalSize == 0) { Console.Error.WriteLine("Error: unknown size"); return 1; }
    Console.Error.WriteLine($"w1 artifact: {totalSize / 1048576} MB");

    Console.Error.WriteLine("Downloading ZIP directory...");
    var tail = DownloadRange(http, artifactUrl, totalSize - 65536, totalSize - 1);

    int eocdPos = -1;
    for (int i = tail.Length - 22; i >= 0; i--)
        if (tail[i] == 0x50 && tail[i + 1] == 0x4b && tail[i + 2] == 0x05 && tail[i + 3] == 0x06)
        { eocdPos = i; break; }
    if (eocdPos < 0) { Console.Error.WriteLine("Error: EOCD not found"); return 1; }

    int entryCount = BitConverter.ToUInt16(tail, eocdPos + 10);
    uint cdOffset = BitConverter.ToUInt32(tail, eocdPos + 16);

    byte[] cdData; int cdStart;
    long cdInTail = tail.Length - (totalSize - cdOffset);
    if (cdInTail >= 0) { cdData = tail; cdStart = (int)cdInTail; }
    else
    {
        Console.Error.WriteLine("Downloading central directory...");
        cdData = DownloadRange(http, artifactUrl, cdOffset, totalSize - 1);
        cdStart = 0;
    }

    // Basename prefixes for the platform apps we care about.
    var wantedPrefixes = new[]
    {
        "microsoft_base application_",
        "microsoft_system application_",
        "microsoft_business foundation_",
        "microsoft_application_",
    };

    var matching = new List<(string Name, int Method, long CompSize, long Offset)>();
    int pos = cdStart;
    for (int i = 0; i < entryCount && pos + 46 <= cdData.Length; i++)
    {
        if (cdData[pos] != 0x50 || cdData[pos + 1] != 0x4b || cdData[pos + 2] != 0x01 || cdData[pos + 3] != 0x02) break;
        int cm = BitConverter.ToUInt16(cdData, pos + 10);
        uint cs = BitConverter.ToUInt32(cdData, pos + 20);
        int nl = BitConverter.ToUInt16(cdData, pos + 28);
        int el = BitConverter.ToUInt16(cdData, pos + 30);
        int cl = BitConverter.ToUInt16(cdData, pos + 32);
        uint lo = BitConverter.ToUInt32(cdData, pos + 42);
        if (pos + 46 + nl > cdData.Length) break;
        var name = Encoding.UTF8.GetString(cdData, pos + 46, nl).Replace('\\', '/');
        var lower = name.ToLowerInvariant();
        var bn = Path.GetFileName(lower);
        if (lower.StartsWith("extensions/") && lower.EndsWith(".app") && cs > 0)
        {
            if (Array.Exists(wantedPrefixes, p => bn.StartsWith(p)))
                matching.Add((name, cm, cs, lo));
        }
        pos += 46 + nl + el + cl;
    }

    if (matching.Count == 0) { Console.Error.WriteLine("Error: no platform .app files found"); return 1; }
    Console.Error.WriteLine($"Found {matching.Count} platform app(s):");
    foreach (var (name, _, compSize, _) in matching)
        Console.Error.WriteLine($"  {Path.GetFileName(name)}  ({compSize / 1048576} MB compressed)");

    matching.Sort((a, b) => a.Offset.CompareTo(b.Offset));
    long totalBytes = 0;
    int extracted = 0;
    foreach (var (name, method, compSize, offset) in matching)
    {
        var basename = Path.GetFileName(name);
        Console.Error.WriteLine($"  Downloading {basename}...");

        // Local file header (30 bytes) + filename + extra field, then compressed data.
        // Over-fetch a generous header margin and parse the real lengths from the local header.
        long headerMargin = 30 + name.Length + 4096;
        long entryEnd = Math.Min(offset + headerMargin + compSize, totalSize - 1);
        var data = DownloadRange(http, artifactUrl, offset, entryEnd);

        if (data.Length < 30 || data[0] != 0x50 || data[1] != 0x4b || data[2] != 0x03 || data[3] != 0x04)
        {
            Console.Error.WriteLine($"  WARNING: bad local header for {basename} — skipping");
            continue;
        }
        int nl2 = BitConverter.ToUInt16(data, 26);
        int el2 = BitConverter.ToUInt16(data, 28);
        int ds = 30 + nl2 + el2;
        if (ds + compSize > data.Length)
        {
            // Extra field larger than our margin — re-fetch with exact bounds.
            entryEnd = Math.Min(offset + ds + compSize, totalSize - 1);
            data = DownloadRange(http, artifactUrl, offset, entryEnd);
            if (ds + compSize > data.Length)
            {
                Console.Error.WriteLine($"  WARNING: truncated data for {basename} — skipping");
                continue;
            }
        }

        byte[] fileData;
        if (method == 0)
        {
            fileData = new byte[compSize];
            Array.Copy(data, ds, fileData, 0, (int)compSize);
        }
        else if (method == 8)
        {
            using var cs2 = new MemoryStream(data, ds, (int)compSize);
            using var df = new DeflateStream(cs2, CompressionMode.Decompress);
            using var o = new MemoryStream();
            df.CopyTo(o);
            fileData = o.ToArray();
        }
        else
        {
            Console.Error.WriteLine($"  WARNING: unsupported compression method {method} for {basename} — skipping");
            continue;
        }

        File.WriteAllBytes(Path.Combine(outputDir, basename), fileData);
        totalBytes += fileData.Length;
        extracted++;
        Console.Error.WriteLine($"  Written {basename} ({fileData.Length / 1048576} MB)");
    }

    Console.Error.WriteLine($"Downloaded {extracted} app(s) ({totalBytes / 1048576} MB total) to {outputDir}");
    return extracted > 0 ? 0 : 1;
}

// ---------------------------------------------------------------------------
// Resolve version: query Microsoft's index to find latest full version
// ---------------------------------------------------------------------------
static int ResolveVersion(string prefix)
{
    // Microsoft's index file: https://bcartifacts-exdbf9fwegejdqak.b02.azurefd.net/sandbox/indexes/w1.json
    // Returns array of {Version: "27.5.46862.0", ...}
    using var http = new HttpClient();
    var indexUrl = "https://bcartifacts-exdbf9fwegejdqak.b02.azurefd.net/sandbox/indexes/w1.json";
    Console.Error.WriteLine($"Resolving BC version prefix '{prefix}'...");

    string json;
    try
    {
        json = http.GetStringAsync(indexUrl).Result;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error fetching index: {ex.Message}");
        return 1;
    }

    // Simple JSON parsing — find all "Version":"X.Y.Z.W" values matching prefix
    var searchPrefix = prefix + ".";
    var versions = new List<string>();
    int idx = 0;
    while ((idx = json.IndexOf("\"Version\"", idx, StringComparison.OrdinalIgnoreCase)) >= 0)
    {
        idx = json.IndexOf(':', idx);
        if (idx < 0) break;
        idx = json.IndexOf('"', idx + 1);
        if (idx < 0) break;
        int end = json.IndexOf('"', idx + 1);
        if (end < 0) break;
        var ver = json.Substring(idx + 1, end - idx - 1);
        if (ver.StartsWith(searchPrefix))
            versions.Add(ver);
        idx = end + 1;
    }

    if (versions.Count == 0)
    {
        Console.Error.WriteLine($"No versions found for prefix '{prefix}'");
        return 1;
    }

    // Sort by version components and pick the latest
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
    // Output to stdout (for script consumption), status to stderr
    Console.Error.WriteLine($"Resolved: {prefix} -> {resolved}");
    Console.WriteLine(resolved);
    return 0;
}

static byte[] DownloadRange(HttpClient http, string url, long from, long to)
{
    // Retry once on transient failures
    for (int attempt = 0; attempt < 2; attempt++)
    {
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Range = new RangeHeaderValue(from, to);
            var resp = http.Send(req);
            resp.EnsureSuccessStatusCode();
            using var ms = new MemoryStream();
            resp.Content.ReadAsStream().CopyTo(ms);
            resp.Dispose();
            return ms.ToArray();
        }
        catch when (attempt == 0)
        {
            Console.Error.WriteLine("  Retrying download...");
        }
    }
    throw new Exception($"Failed to download range {from}-{to}");
}

static int Error(string msg) { Console.Error.WriteLine($"Error: {msg}"); return 1; }

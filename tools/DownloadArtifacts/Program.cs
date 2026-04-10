// Standalone tool to download BC dependencies via HTTP range requests.
// Used by MSBuild pre-build targets when DLLs aren't present locally.
//
// Modes:
//   DownloadArtifacts service-tier <bc-version> <output-dir>
//     Downloads ~55 Microsoft.Dynamics.Nav.*.dll from the BC platform artifact (~11 MB).
//
//   DownloadArtifacts al-compiler <tool-version> <output-dir>
//     Downloads the AL compiler NuGet package and extracts the needed DLLs (~57 MB).

using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: DownloadArtifacts service-tier <bc-version> <output-dir>");
    Console.Error.WriteLine("       DownloadArtifacts al-compiler <tool-version> <output-dir>");
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
        if (!name.StartsWith("tools/net8.0/any/", StringComparison.OrdinalIgnoreCase))
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
        if (lower.Contains("servicetier/") && lower.Contains("/service/") &&
            bn.StartsWith("microsoft.dynamics.nav.") && bn.EndsWith(".dll") && cs > 0 &&
            !lower.Split("/service/").Last().Contains('/'))
            matching.Add((name, cm, cs, lo));
        pos += 46 + nl + el + cl;
    }

    if (matching.Count == 0) { Console.Error.WriteLine("Error: no Nav DLLs found"); return 1; }
    Console.Error.WriteLine($"Found {matching.Count} Nav DLLs");

    matching.Sort((a, b) => a.Offset.CompareTo(b.Offset));
    long firstOffset = matching[0].Offset;
    var last = matching[^1];
    long rangeEnd = Math.Min(last.Offset + 30 + last.Name.Length + 512 + last.CompSize, totalSize - 1);
    Console.Error.WriteLine($"Downloading {(rangeEnd - firstOffset) / 1048576} MB ({(int)((1.0 - (double)(rangeEnd - firstOffset) / totalSize) * 100)}% savings)...");
    var data = DownloadRange(http, artifactUrl, firstOffset, rangeEnd);

    int extracted = 0;
    foreach (var (name, method, compSize, offset) in matching)
    {
        int p = (int)(offset - firstOffset);
        if (p < 0 || p + 30 > data.Length || data[p] != 0x50 || data[p + 1] != 0x4b || data[p + 2] != 0x03 || data[p + 3] != 0x04)
            continue;
        int nl2 = BitConverter.ToUInt16(data, p + 26);
        int el2 = BitConverter.ToUInt16(data, p + 28);
        int ds = p + 30 + nl2 + el2;
        if (ds + compSize > data.Length) continue;

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
        extracted++;
    }

    Console.Error.WriteLine($"Downloaded {extracted} DLLs to {outputDir}");
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

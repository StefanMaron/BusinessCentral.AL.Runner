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
        using var http = ArtifactHttpClient.Create(TimeSpan.FromMinutes(5), logf);
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
            // Issue #2926: same shape as the CDN messages — one line for every possible
            // failure. This one fetches from nuget.org rather than the BC CDN, so a message
            // blaming "the CDN" would be wrong twice over.
            NetworkDiagnosis.Describe(ex, $"the AL compiler package {packageId} {version}", url).WriteTo(logf);
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

        using var http = ArtifactHttpClient.Create(TimeSpan.FromMinutes(5), logf);

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

        using var http = ArtifactHttpClient.Create(TimeSpan.FromMinutes(10), logf);

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
    // Test Sources (issue #2724): ONE Microsoft BaseApp test bucket's AL source. The
    // 33 `Tests-*.Source.zip` files ship in the SAME platform artifact TestApps reads,
    // under Applications/BaseApp/Test/ beside the compiled .app files — TestApps' filter
    // simply excluded them by extension. Each zip is flat (app.json at the root beside
    // the .al files, measured on 28.4.53241.54318: Tests-ERM = 297 files, 2.4 MB
    // deflated) and needs no edits, so it is unpacked straight into <outputDir>/<bucket>/
    // and that directory is a runnable bundle.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Whether a ZIP central-directory entry is <paramref name="bucket"/>'s Source.zip.
    /// Pure over the entry name so the rule — the BaseApp Test/ folder AND an exact
    /// basename match — is unit-testable without a central directory or a network round
    /// trip. Exact, not prefix: <c>Tests-ERM</c> must not match a hypothetical
    /// <c>Tests-ERM-Extra</c>, and the compiled <c>Microsoft_Tests-ERM_….app</c> beside it
    /// is a different file for a different mode.
    /// </summary>
    internal static bool IsBaseAppTestSourceEntry(string entryName, string bucket)
    {
        if (string.IsNullOrWhiteSpace(entryName) || string.IsNullOrWhiteSpace(bucket)) return false;
        const string folder = "applications/baseapp/test/";
        var lower = entryName.Replace('\\', '/').ToLowerInvariant();
        if (!lower.StartsWith(folder, StringComparison.Ordinal)) return false;
        // Equality on the remainder also rejects anything nested deeper than the folder.
        return lower[folder.Length..] == bucket.Trim().ToLowerInvariant() + ".source.zip";
    }

    public static int TestSources(string version, string outputDir, string bucket, Action<string>? log = null)
    {
        var logf = L(log);
        if (string.IsNullOrWhiteSpace(bucket))
        {
            logf("Error: test-sources needs a bucket name, e.g. Tests-ERM");
            return 1;
        }
        bucket = bucket.Trim();
        var artifactUrl = BuildArtifactUrl(version, "platform");
        Directory.CreateDirectory(outputDir);

        using var http = ArtifactHttpClient.Create(TimeSpan.FromMinutes(10), logf);

        logf($"Resolving artifact size for BC {version} (platform)...");
        if (!TryHeadContentLength(http, artifactUrl, version, "platform", logf, out long totalSize)) return 1;
        if (totalSize == 0) { logf("Error: unknown size"); return 1; }

        logf("Downloading ZIP directory...");
        if (!TryReadCentralDirectory(http, artifactUrl, totalSize, logf, out var cdData, out var cdStart, out var entryCount))
            return 1;

        (string Name, int Method, long CompSize, long Offset)? found = null;
        var shipped = new List<string>();
        int pos = cdStart;
        for (int i = 0; i < entryCount && pos + 46 <= cdData.Length; i++)
        {
            if (!IsCentralHeader(cdData, pos)) break;
            var (cm, cs, nl, el, cl, lo, name) = ReadCentralEntry(cdData, pos);
            pos += 46 + nl + el + cl;
            if (cs == 0) continue;
            if (IsBaseAppTestSourceEntry(name, bucket)) { found = (name, cm, cs, lo); break; }
            var lower = name.ToLowerInvariant();
            if (lower.StartsWith("applications/baseapp/test/", StringComparison.Ordinal)
                && lower.EndsWith(".source.zip", StringComparison.Ordinal))
                shipped.Add(Path.GetFileName(name)[..^".Source.zip".Length]);
        }

        if (found == null)
        {
            // Name what IS there: a typo'd bucket ("Tests-Erm", "Tests-CashFlow") is the
            // likely cause, and the list is the only way to fix it without a second run.
            logf($"Error: no Applications/BaseApp/Test/{bucket}.Source.zip in the BC {version} platform artifact.");
            shipped.Sort(StringComparer.OrdinalIgnoreCase);
            logf($"       Buckets that artifact ships ({shipped.Count}): {string.Join(", ", shipped)}");
            return 1;
        }

        var (entryName, method, compSize, offset) = found.Value;
        logf($"  Downloading {Path.GetFileName(entryName)} ({compSize / 1024} KB compressed)...");
        var zipBytes = ExtractEntry(http, artifactUrl, totalSize, entryName, method, compSize, offset, logf);
        if (zipBytes == null) return 1;

        // A fresh directory every time: a previous partial or older-version unpack must not
        // merge with this one and leave stray .al files the compiler then picks up.
        var bundleDir = Path.Combine(outputDir, bucket);
        if (Directory.Exists(bundleDir)) Directory.Delete(bundleDir, recursive: true);
        Directory.CreateDirectory(bundleDir);

        int files;
        try
        {
            using var ms = new MemoryStream(zipBytes);
            files = UnpackSourceZip(ms, bundleDir);
        }
        catch (InvalidDataException ex)
        {
            logf($"Error: {Path.GetFileName(entryName)}: {ex.Message}");
            return 1;
        }

        logf($"Unpacked {files} file(s) from {Path.GetFileName(entryName)} to {bundleDir}");
        return 0;
    }

    /// <summary>
    /// Unpacks a bucket Source.zip into <paramref name="destDir"/>. Validates EVERY entry
    /// before writing ANY: an entry that would land outside the destination
    /// (<c>../</c>, an absolute path) or a zip with no root <c>app.json</c> throws
    /// <see cref="InvalidDataException"/> with nothing on disk — a half-unpacked bundle
    /// would otherwise compile and run a subset while looking complete. Returns the number
    /// of files written.
    /// </summary>
    internal static int UnpackSourceZip(Stream sourceZip, string destDir)
    {
        using var zip = new ZipArchive(sourceZip, ZipArchiveMode.Read, leaveOpen: true);
        var destRoot = Path.GetFullPath(destDir);
        var destRootWithSep = destRoot.EndsWith(Path.DirectorySeparatorChar) ? destRoot : destRoot + Path.DirectorySeparatorChar;

        var plan = new List<(ZipArchiveEntry Entry, string Target)>();
        var hasRootAppJson = false;
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (name.Length == 0 || name.EndsWith('/')) continue; // directory entry
            var target = Path.GetFullPath(Path.Combine(destRoot, name));
            if (!target.StartsWith(destRootWithSep, StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"entry '{entry.FullName}' escapes the destination directory '{destRoot}' — refusing to unpack any of it");
            if (string.Equals(name, "app.json", StringComparison.OrdinalIgnoreCase)) hasRootAppJson = true;
            plan.Add((entry, target));
        }

        if (!hasRootAppJson)
            throw new InvalidDataException(
                "no app.json at the root of the zip, so the runner would see no bundle at all (first entries: "
                + string.Join(", ", zip.Entries.Take(5).Select(e => e.FullName)) + ")");

        foreach (var (entry, target) in plan)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = entry.Open();
            using var output = File.Create(target);
            input.CopyTo(output);
        }
        return plan.Count;
    }

    // -----------------------------------------------------------------------
    // Test Data (issue #2724): the sandbox demo-database backup `--test-data` hydrates
    // from. It sits at the ROOT of the country artifact (w1: BusinessCentral-W1.bak,
    // measured 610 MB deflated / 977 MB uncompressed on 28.4.53241.54318 — the whole
    // artifact is 955 MB, so this one entry is most of it). ExtractEntry buffers an entry
    // in memory twice over and cannot hold this one; the .bak is streamed to disk instead
    // (ExtractEntryToFile / CopyZipEntryData) and landed atomically, so a partial download
    // never sits at the path TestDataOptions would open and trust.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Whether a ZIP central-directory entry is the demo backup for <paramref name="country"/>.
    /// Root-level only: the artifact ships exactly one, at the root, and anchoring there is the
    /// same defence <see cref="IsWantedPlatformAppEntry"/> uses against a same-basename file in
    /// another folder. Pure over the entry name so it is testable without a network round trip.
    /// </summary>
    internal static bool IsTestDataBackupEntry(string entryName, string country)
    {
        if (string.IsNullOrWhiteSpace(entryName)) return false;
        var lower = entryName.Replace('\\', '/').ToLowerInvariant();
        return lower == $"businesscentral-{NormalizeCountry(country)}.bak";
    }

    public static int TestData(string version, string outputDir, string country = "w1", Action<string>? log = null)
    {
        var logf = L(log);
        var countryLower = NormalizeCountry(country);
        var artifactUrl = BuildArtifactUrl(version, countryLower);
        Directory.CreateDirectory(outputDir);

        // Generous: this client streams the whole ~600 MB entry through one response.
        using var http = ArtifactHttpClient.Create(TimeSpan.FromMinutes(30), logf);

        logf($"Resolving artifact size for BC {version} ({countryLower})...");
        if (!TryHeadContentLength(http, artifactUrl, version, countryLower, logf, out long totalSize)) return 1;
        if (totalSize == 0) { logf("Error: unknown size"); return 1; }
        logf($"{countryLower} artifact: {totalSize / 1048576} MB");

        logf("Downloading ZIP directory...");
        if (!TryReadCentralDirectory(http, artifactUrl, totalSize, logf, out var cdData, out var cdStart, out var entryCount))
            return 1;

        (string Name, int Method, long CompSize, long UncompSize, long Offset)? found = null;
        int pos = cdStart;
        for (int i = 0; i < entryCount && pos + 46 <= cdData.Length; i++)
        {
            if (!IsCentralHeader(cdData, pos)) break;
            var (cm, cs, nl, el, cl, lo, name) = ReadCentralEntry(cdData, pos);
            if (cs > 0 && IsTestDataBackupEntry(name, countryLower))
            {
                found = (name, cm, cs, ReadCentralUncompressedSize(cdData, pos), lo);
                break;
            }
            pos += 46 + nl + el + cl;
        }

        var expectedName = $"BusinessCentral-{countryLower.ToUpperInvariant()}.bak";
        if (found == null)
        {
            logf($"Error: no {expectedName} at the root of the BC {version} ({countryLower}) artifact.");
            return 1;
        }

        var (entryName, method, compSize, uncompSize, offset) = found.Value;
        if (uncompSize == uint.MaxValue)
        {
            // ZIP64 sizes live in the extra field; nothing here reads them, and guessing the
            // length would defeat the truncation check that makes the download trustworthy.
            logf($"Error: {entryName} is a ZIP64 entry (uncompressed size not in the central directory); not supported.");
            return 1;
        }

        var destPath = Path.Combine(outputDir, Path.GetFileName(entryName));
        logf($"  Downloading {entryName} ({compSize / 1048576} MB compressed, {uncompSize / 1048576} MB uncompressed), streaming to disk...");
        if (!ExtractEntryToFile(http, artifactUrl, totalSize, entryName, method, compSize, uncompSize, offset, destPath, logf))
            return 1;

        logf($"Written {destPath} ({new FileInfo(destPath).Length / 1048576} MB)");
        return 0;
    }

    /// <summary>
    /// Copies one ZIP entry's data from <paramref name="compressedSource"/> to
    /// <paramref name="destination"/>, inflating deflate (method 8) or copying stored
    /// (method 0) bytes, and returns the byte count written. Throws
    /// <see cref="NotSupportedException"/> for any other method and
    /// <see cref="InvalidDataException"/> when the count differs from
    /// <paramref name="expectedUncompressedLength"/> — a stream that stopped short must
    /// surface as an error, never as a shorter file. Pure over streams so it is testable
    /// against an in-memory deflate buffer.
    /// </summary>
    internal static long CopyZipEntryData(Stream compressedSource, int method, long expectedUncompressedLength, Stream destination)
    {
        Stream data = method switch
        {
            0 => compressedSource,
            8 => new DeflateStream(compressedSource, CompressionMode.Decompress, leaveOpen: true),
            _ => throw new NotSupportedException(
                $"unsupported ZIP compression method {method} (only stored = 0 and deflate = 8 are handled)"),
        };

        long copied = 0;
        try
        {
            var buffer = new byte[1 << 20];
            int n;
            while ((n = data.Read(buffer, 0, buffer.Length)) > 0)
            {
                destination.Write(buffer, 0, n);
                copied += n;
            }
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException(
                $"corrupt deflate stream after {copied} of the expected {expectedUncompressedLength} bytes: {ex.Message}", ex);
        }
        finally
        {
            if (method == 8) data.Dispose();
        }

        if (copied != expectedUncompressedLength)
            throw new InvalidDataException(
                $"truncated entry: expected {expectedUncompressedLength} bytes, got {copied}");
        return copied;
    }

    // Streamed sibling of ExtractEntry for entries too large to buffer (the .bak). Reads the
    // local header with a small ranged request exactly as ExtractEntry does, then streams the
    // compressed data range through CopyZipEntryData into <dest>.partial and moves it into
    // place only after the length check passed. One retry from scratch, like DownloadRange.
    private static bool ExtractEntryToFile(
        HttpClient http, string url, long totalSize,
        string name, int method, long compSize, long uncompSize, long offset,
        string destPath, Action<string> logf)
    {
        long headerEnd = Math.Min(offset + 30 + name.Length + 4096, totalSize - 1);
        var header = DownloadRange(http, url, offset, headerEnd);
        if (header.Length < 30 || header[0] != 0x50 || header[1] != 0x4b || header[2] != 0x03 || header[3] != 0x04)
        {
            logf($"  WARNING: bad local header for {Path.GetFileName(name)} — skipping");
            return false;
        }
        int nl2 = BitConverter.ToUInt16(header, 26);
        int el2 = BitConverter.ToUInt16(header, 28);
        long dataStart = offset + 30 + nl2 + el2;
        long dataEnd = dataStart + compSize - 1;
        if (dataEnd > totalSize - 1)
        {
            logf($"  WARNING: truncated data for {Path.GetFileName(name)} — skipping");
            return false;
        }

        var partial = destPath + ".partial";
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Range = new RangeHeaderValue(dataStart, dataEnd);
                using var resp = http.Send(req, HttpCompletionOption.ResponseHeadersRead);
                resp.EnsureSuccessStatusCode();
                using (var body = resp.Content.ReadAsStream())
                using (var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                    CopyZipEntryData(body, method, uncompSize, file);
                File.Move(partial, destPath, overwrite: true);
                return true;
            }
            catch (NotSupportedException ex)
            {
                logf($"  WARNING: {Path.GetFileName(name)}: {ex.Message} — skipping");
                TryDelete(partial);
                return false;
            }
            catch (Exception ex) when (attempt == 0)
            {
                logf($"  Retrying download of {Path.GetFileName(name)} ({ex.Message})...");
            }
            catch (Exception ex)
            {
                logf($"  WARNING: {Path.GetFileName(name)}: {ex.Message} — skipping");
                TryDelete(partial);
                return false;
            }
        }
        return false;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    // -----------------------------------------------------------------------
    // Platform Apps: Microsoft Base/System/BusinessFoundation/Application .app files
    // from the w1 (or, since #2236, a country-localized) artifact's Extensions/ folder.
    // -----------------------------------------------------------------------
    /// <param name="country">
    /// BC artifact country/localization channel — "w1" (worldwide, default) or a
    /// country code such as "us"/"de"/"gb" (issue #2236). Not validated against an
    /// allowlist here: an unresolvable code 404s against the CDN and
    /// <see cref="TryHeadContentLength"/> reports the exact URL that failed, which is
    /// the only "maintainable" validation for a set of codes Microsoft adds to on its
    /// own schedule.
    /// </param>
    // w1 (the default): the curated 5-app core set, unchanged since #1653/#2210 — narrow
    // on purpose so the default download stays ~135 MB for the overwhelming majority of
    // projects, which need nothing country-specific.
    internal static readonly string[] W1PlatformAppPrefixes =
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

    /// <summary>
    /// Normalizes a <c>--country</c> value the same way every entry point does: trim,
    /// lowercase, empty/whitespace -> "w1". Pulled out as its own testable function so the
    /// normalization rule is pinned once instead of copy-pasted at each call site.
    /// </summary>
    internal static string NormalizeCountry(string? country)
        => string.IsNullOrWhiteSpace(country) ? "w1" : country.Trim().ToLowerInvariant();

    /// <summary>The CDN URL PlatformApps/TestApps/ServiceTier download from for a given
    /// version + channel ("w1", a country code, or "platform"). Pure string composition —
    /// no I/O — so URL construction is directly testable without a network round trip.</summary>
    internal static string BuildArtifactUrl(string version, string channel)
        => $"{CdnBase}/{version}/{channel}";

    /// <summary>
    /// Whether a ZIP central-directory entry name is one PlatformApps should download for
    /// the given country (issue #2236). Pure over the entry name alone — no I/O — so the
    /// selection rule (including the Extensions/-vs-Applications.&lt;CC&gt;/ trap and the
    /// w1-curated-list-vs-country-broad-match split) is directly unit-testable without
    /// faking a ZIP central directory or an HTTP round trip.
    /// </summary>
    /// <param name="entryName">Raw ZIP entry name (any case, either slash style).</param>
    /// <param name="isW1">True for the w1 (worldwide) channel; false for any country code.</param>
    internal static bool IsWantedPlatformAppEntry(string entryName, bool isW1)
    {
        var lower = entryName.ToLowerInvariant();
        // Anchor on Extensions/ specifically: a country artifact ALSO carries an
        // Applications.<CC>/ folder holding a DIFFERENT, smaller file with the identical
        // basename (e.g. Applications.US/Microsoft_Base Application_...app, 48.7 MB, vs
        // the 110.6 MB localized one under Extensions/) — a basename-only match would
        // silently pick the non-localized one from the wrong folder.
        if (!lower.StartsWith("extensions/") || !lower.EndsWith(".app")) return false;
        var bn = Path.GetFileName(lower);
        // Any other country (#2236): a country artifact is not "w1 plus extras" — its
        // Base/System Application etc. are DIFFERENT FILES from w1's (measured: the US
        // Base Application is 110.6 MB vs w1's 98.6 MB for the same build), and a project
        // depending on a country-specific Microsoft app (e.g. "IRS Forms") needs an app
        // the curated w1 list above has never heard of and never will, by name, for every
        // country Microsoft ships. Rather than hand-maintain a second curated list per
        // country, fetch every Microsoft-published app the country artifact ships under
        // Extensions/ — this naturally covers the localized core set AND whatever
        // country-specific app(s) a project actually depends on, with nothing to guess.
        return isW1
            ? Array.Exists(W1PlatformAppPrefixes, p => bn.StartsWith(p))
            : bn.StartsWith("microsoft_");
    }

    public static int PlatformApps(string version, string outputDir, string country = "w1", Action<string>? log = null)
    {
        var logf = L(log);
        var countryLower = NormalizeCountry(country);
        bool isW1 = countryLower == "w1";
        var artifactUrl = BuildArtifactUrl(version, countryLower);
        Directory.CreateDirectory(outputDir);

        using var http = ArtifactHttpClient.Create(TimeSpan.FromMinutes(10), logf);

        logf($"Resolving artifact size for BC {version} ({countryLower})...");
        if (!TryHeadContentLength(http, artifactUrl, version, countryLower, logf, out long totalSize)) return 1;
        if (totalSize == 0) { logf("Error: unknown size"); return 1; }
        logf($"{countryLower} artifact: {totalSize / 1048576} MB");

        logf("Downloading ZIP directory...");
        if (!TryReadCentralDirectory(http, artifactUrl, totalSize, logf, out var cdData, out var cdStart, out var entryCount))
            return 1;

        var matching = new List<(string Name, int Method, long CompSize, long Offset)>();
        int pos = cdStart;
        for (int i = 0; i < entryCount && pos + 46 <= cdData.Length; i++)
        {
            if (!IsCentralHeader(cdData, pos)) break;
            var (cm, cs, nl, el, cl, lo, name) = ReadCentralEntry(cdData, pos);
            if (cs > 0 && IsWantedPlatformAppEntry(name, isW1))
                matching.Add((name, cm, cs, lo));
            pos += 46 + nl + el + cl;
        }

        if (matching.Count == 0) { logf("Error: no platform .app files found"); return 1; }
        logf($"Found {matching.Count} platform app(s) for country '{countryLower}':");
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
        using var http = ArtifactHttpClient.Create(TimeSpan.FromMinutes(10), logf);

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
            using var http = ArtifactHttpClient.Create(log: logf);
            using var resp = http.Send(new HttpRequestMessage(HttpMethod.Head, url));
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            // Issue #2926. Two things wrong here, and the second is the one that bites.
            //
            // Only HttpRequestException was caught, so a timeout escaped as an unhandled
            // exception from a method whose whole contract is "answer true or false".
            //
            // And `false` here does not mean what the caller reads it as.
            // BcArtifacts.ResolveProvisionTargetCore treats false as "this exact build is not
            // published" and walks down to the major-fallback tier, whose own comment calls
            // that "the one genuinely degraded outcome" — so a five-second network blip gets
            // reported to the user as Microsoft having withdrawn the build. The signature
            // cannot carry the third state without changing that contract (tracked separately),
            // so the log at least has to stop asserting something that was never established.
            NetworkDiagnosis.Describe(ex, $"BC {version}", url).WriteTo(logf);
            logf($"       Could not determine whether BC {version} is published. Treating it as " +
                 "unavailable and falling back; that is a consequence of the failure above, not " +
                 "evidence the build was withdrawn.");
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
        try { using var http = ArtifactHttpClient.Create(log: logf); json = http.GetStringAsync(indexUrl).Result; }
        catch (Exception ex)
        {
            // Issue #2926: this used to print "Error fetching index: One or more errors
            // occurred. (A task was canceled.)" — an AggregateException's ToString, which names
            // neither what failed nor where. NetworkDiagnosis unwraps it and reports the
            // observation.
            NetworkDiagnosis.Describe(ex, $"the BC version index (prefix '{prefix}')", indexUrl).WriteTo(logf);
            return null;
        }

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
            // Issue #2236: name the exact URL that 404'd, not just the version/channel —
            // this is the only channel this method has (w1, platform, or a country code
            // like "us"), so when the country is wrong the URL is what tells the reader
            // which one to check.
            logf($"Error: no BC artifact published for {version} ({channel}): {url}");
            logf("       Check the version, or resolve the latest for a prefix:");
            // Issue #2085: this fires both from the standalone tools/DownloadArtifacts CLI
            // (repo-checkout only) AND in-process from the shipped `al-runner` binary's own
            // auto-provision path — a `dotnet run --project tools/DownloadArtifacts` hint
            // here would be a dead end for anyone using the latter, which is the common
            // case. `al-runner provision --resolve-version` works from both.
            logf($"         al-runner provision --resolve-version {prefix}");
            if (!string.Equals(channel, "w1", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(channel, "platform", StringComparison.OrdinalIgnoreCase))
                logf($"       If '{channel}' is a --country code, double-check the spelling — " +
                     "the runner does not maintain its own list of valid codes.");
            size = 0;
            return false;
        }
        catch (Exception ex)
        {
            // Issue #2926, two defects in one catch block.
            //
            // It caught only HttpRequestException, so a client-timeout — which .NET raises as
            // TaskCanceledException, not HttpRequestException — escaped this method as an
            // unhandled exception with a raw stack trace: exactly the crash #1659 fixed for
            // 404s, still live for the most common transient failure there is.
            //
            // And the message it did emit named a cause the observation did not support. "could
            // not reach the BC artifact CDN" is how a host with no IPv6 route was told Azure was
            // down. NetworkDiagnosis reports what was observed and only speaks about the CDN
            // when the CDN actually answered.
            NetworkDiagnosis.Describe(ex, $"BC {version} ({channel})", url).WriteTo(logf);
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

    // Central-directory uncompressed size (offset 24). Kept out of ReadCentralEntry's tuple so
    // its five existing deconstruction sites stay untouched; only the streamed path needs it.
    private static long ReadCentralUncompressedSize(byte[] cd, int pos)
        => BitConverter.ToUInt32(cd, pos + 24);

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
        Exception? last = null;
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
            catch (Exception ex) when (attempt == 0)
            {
                last = ex;
                Console.Error.WriteLine($"  Retrying download... ({ex.Message})");
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }
        // Issue #2926: this used to throw a bare Exception with no inner, discarding the only
        // record of WHY both attempts failed. The caller then reported "Failed to download
        // range 0-65535" — a message that cannot be acted on. Keep the cause attached so the
        // diagnosis above it has something to classify.
        throw new HttpRequestException(
            $"Failed to download range {from}-{to} from {url} after 2 attempts", last);
    }
}

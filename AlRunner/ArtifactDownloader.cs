using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;

/// <summary>
/// Downloads BC Service Tier DLLs from the platform artifact ZIP using HTTP range
/// requests. Only fetches the ~55 Microsoft.Dynamics.Nav.*.dll files (~11 MB) instead
/// of the full ~1.2 GB artifact. Works cross-platform (Windows, Linux, macOS).
/// </summary>
public static class ArtifactDownloader
{
    private const string NavDllPrefix = "microsoft.dynamics.nav.";

    /// <summary>
    /// Downloads BC Service Tier DLLs to the specified directory.
    /// Returns the path to the directory, or null on failure.
    /// </summary>
    public static string? DownloadServiceTierDlls(string artifactUrl, string outputDir)
    {
        Directory.CreateDirectory(outputDir);

        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromMinutes(5);

        // Step 1: Get total size via HEAD request
        Log.Info($"Resolving artifact size...");
        long totalSize;
        try
        {
            using var headReq = new HttpRequestMessage(HttpMethod.Head, artifactUrl);
            using var headResp = http.Send(headReq);
            headResp.EnsureSuccessStatusCode();
            totalSize = headResp.Content.Headers.ContentLength ?? 0;
            if (totalSize == 0)
            {
                Console.Error.WriteLine("Error: Could not determine artifact size");
                return null;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: Could not reach artifact URL: {ex.Message}");
            return null;
        }

        Log.Info($"Platform artifact: {totalSize / 1048576} MB");

        // Step 2: Download last 64 KB to find EOCD (End of Central Directory)
        int tailSize = 65536;
        long tailStart = totalSize - tailSize;
        Log.Info("Downloading ZIP directory index...");
        var tail = DownloadRange(http, artifactUrl, tailStart, totalSize - 1);
        if (tail == null) return null;

        // Find EOCD signature (scan backward)
        int eocdPos = FindEocd(tail);
        if (eocdPos < 0)
        {
            Console.Error.WriteLine("Error: ZIP EOCD not found — archive may be ZIP64 or corrupted");
            return null;
        }

        int entryCount = BitConverter.ToUInt16(tail, eocdPos + 10);
        uint cdSize = BitConverter.ToUInt32(tail, eocdPos + 12);
        uint cdOffset = BitConverter.ToUInt32(tail, eocdPos + 16);

        // Step 3: Download central directory if not in tail
        byte[] cdData;
        int cdStartInData;
        long cdStartInTail = tail.Length - (totalSize - cdOffset);
        if (cdStartInTail >= 0)
        {
            cdData = tail;
            cdStartInData = (int)cdStartInTail;
        }
        else
        {
            Log.Info("Downloading central directory...");
            cdData = DownloadRange(http, artifactUrl, cdOffset, totalSize - 1)!;
            if (cdData == null) return null;
            cdStartInData = 0;
        }

        // Step 4: Parse central directory entries
        var entries = ParseCentralDirectory(cdData, cdStartInData, entryCount);
        Log.Info($"Parsed {entries.Count} entries in archive");

        // Step 5: Find matching Nav DLLs
        var matching = entries.Where(e =>
        {
            var nameLower = e.Name.ToLowerInvariant();
            if (!nameLower.Contains("servicetier/") || !nameLower.Contains("/service/"))
                return false;
            // Only direct children of Service/, not subdirectories
            var afterService = nameLower.Split("/service/").Last();
            if (afterService.Contains('/'))
                return false;
            var basename = Path.GetFileName(nameLower);
            return basename.StartsWith(NavDllPrefix) && basename.EndsWith(".dll") && e.CompressedSize > 0;
        }).OrderBy(e => e.LocalOffset).ToList();

        if (matching.Count == 0)
        {
            Console.Error.WriteLine("Error: No Nav DLLs found in artifact");
            return null;
        }

        Log.Info($"Found {matching.Count} Nav DLLs");

        // Step 6: Download all matching DLLs in a single range request
        long firstOffset = matching.Min(e => e.LocalOffset);
        var lastEntry = matching.OrderByDescending(e => e.LocalOffset).First();
        long rangeEnd = lastEntry.LocalOffset + 30 + lastEntry.Name.Length + 512 + lastEntry.CompressedSize;
        rangeEnd = Math.Min(rangeEnd, totalSize - 1);
        long downloadSize = rangeEnd - firstOffset;
        int savings = (int)((1.0 - (double)downloadSize / totalSize) * 100);

        Log.Info($"Downloading {downloadSize / 1048576} MB ({savings}% savings vs full artifact)...");
        var rangeData = DownloadRange(http, artifactUrl, firstOffset, rangeEnd);
        if (rangeData == null) return null;

        // Step 7: Extract each DLL from the range
        int extracted = 0;
        long totalBytes = 0;
        foreach (var entry in matching)
        {
            var basename = Path.GetFileName(entry.Name);
            int pos = (int)(entry.LocalOffset - firstOffset);

            if (pos < 0 || pos + 30 > rangeData.Length)
                continue;

            // Validate local file header
            if (rangeData[pos] != 0x50 || rangeData[pos + 1] != 0x4b ||
                rangeData[pos + 2] != 0x03 || rangeData[pos + 3] != 0x04)
                continue;

            int nameLen = BitConverter.ToUInt16(rangeData, pos + 26);
            int extraLen = BitConverter.ToUInt16(rangeData, pos + 28);
            int dataStart = pos + 30 + nameLen + extraLen;

            if (dataStart + entry.CompressedSize > rangeData.Length)
                continue;

            byte[] fileData;
            if (entry.CompressionMethod == 0) // stored
            {
                fileData = new byte[entry.CompressedSize];
                Array.Copy(rangeData, dataStart, fileData, 0, entry.CompressedSize);
            }
            else if (entry.CompressionMethod == 8) // deflate
            {
                try
                {
                    using var compStream = new MemoryStream(rangeData, dataStart, (int)entry.CompressedSize);
                    using var deflate = new DeflateStream(compStream, CompressionMode.Decompress);
                    using var output = new MemoryStream();
                    deflate.CopyTo(output);
                    fileData = output.ToArray();
                }
                catch
                {
                    Console.Error.WriteLine($"  Warning: decompression failed for {basename}");
                    continue;
                }
            }
            else
                continue;

            File.WriteAllBytes(Path.Combine(outputDir, basename), fileData);
            extracted++;
            totalBytes += fileData.Length;
        }

        Log.Info($"Extracted {extracted} DLLs ({totalBytes / 1024} KB) to {outputDir}");
        return extracted > 0 ? outputDir : null;
    }

    /// <summary>
    /// Returns the default cache directory for BC artifacts.
    /// Linux/Mac: ~/.local/share/al-runner/artifacts
    /// Windows:   %LOCALAPPDATA%/al-runner/artifacts
    /// </summary>
    public static string GetDefaultCacheDir(string version)
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(baseDir))
            baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        return Path.Combine(baseDir, "al-runner", "artifacts", version);
    }

    private static byte[]? DownloadRange(HttpClient http, string url, long from, long to)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Range = new RangeHeaderValue(from, to);
            using var resp = http.Send(req);
            if (!resp.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"Error: HTTP {(int)resp.StatusCode} downloading range {from}-{to}");
                return null;
            }
            using var ms = new MemoryStream();
            resp.Content.ReadAsStream().CopyTo(ms);
            return ms.ToArray();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: download failed: {ex.Message}");
            return null;
        }
    }

    private static int FindEocd(byte[] data)
    {
        // Scan backward for EOCD signature: 0x50 0x4b 0x05 0x06
        for (int i = data.Length - 22; i >= 0; i--)
        {
            if (data[i] == 0x50 && data[i + 1] == 0x4b &&
                data[i + 2] == 0x05 && data[i + 3] == 0x06)
                return i;
        }
        return -1;
    }

    private record ZipEntry(string Name, int CompressionMethod, long CompressedSize, long UncompressedSize, long LocalOffset);

    private static List<ZipEntry> ParseCentralDirectory(byte[] data, int start, int count)
    {
        var entries = new List<ZipEntry>();
        int pos = start;
        for (int i = 0; i < count; i++)
        {
            if (pos + 46 > data.Length) break;
            if (data[pos] != 0x50 || data[pos + 1] != 0x4b ||
                data[pos + 2] != 0x01 || data[pos + 3] != 0x02)
                break;

            int compMethod = BitConverter.ToUInt16(data, pos + 10);
            uint compSize = BitConverter.ToUInt32(data, pos + 20);
            uint uncompSize = BitConverter.ToUInt32(data, pos + 24);
            int nameLen = BitConverter.ToUInt16(data, pos + 28);
            int extraLen = BitConverter.ToUInt16(data, pos + 30);
            int commentLen = BitConverter.ToUInt16(data, pos + 32);
            uint localOffset = BitConverter.ToUInt32(data, pos + 42);

            if (pos + 46 + nameLen > data.Length) break;
            var name = Encoding.UTF8.GetString(data, pos + 46, nameLen).Replace('\\', '/');

            entries.Add(new ZipEntry(name, compMethod, compSize, uncompSize, localOffset));
            pos += 46 + nameLen + extraLen + commentLen;
        }
        return entries;
    }
}

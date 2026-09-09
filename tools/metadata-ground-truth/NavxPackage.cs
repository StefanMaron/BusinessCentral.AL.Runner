// Reading a .app: the NAVX header, the outer/inner zip nesting, and the manifest fields the
// compiler needs back. Standalone rather than calling into AlRunner, so this tool has no
// build-order dependency on the runner it exists to check.

using System.IO.Compression;
using System.Xml;

namespace AlRunner.Tools.MetadataGroundTruth;

internal sealed record AppIdentity(
    Guid Id, string Name, string Publisher, Version Version, string Platform,
    string Target, string ContextSensitiveHelpUrl,
    IReadOnlyList<string> Features, IReadOnlyList<string> PreprocessorSymbols,
    IReadOnlyList<(Guid Id, string Name, string Publisher, Version Version)> Dependencies);

internal static class NavxPackage
{
    private const string Ns = "http://schemas.microsoft.com/navx/2015/manifest";

    /// <summary>Extract a .app to <paramref name="destination"/>, unwrapping the R2R nesting.</summary>
    /// <remarks>
    /// A shipped Microsoft app is a NAVX-prefixed zip whose only real entry is ANOTHER
    /// NAVX-prefixed zip (`&lt;guid&gt;_&lt;version&gt;_&lt;plat&gt;_&lt;build&gt;.app`), and the
    /// inner one holds src/, SymbolReference.json and the resources. Stopping at the outer
    /// layer yields a directory with no NavxManifest.xml, which reads as "this app has no
    /// source" rather than as an unfinished extraction.
    /// </remarks>
    public static void ExtractTo(string appPath, string destination)
    {
        Directory.CreateDirectory(destination);
        var bytes = File.ReadAllBytes(appPath);
        ExtractBytes(bytes, destination, depth: 0);
    }

    private static void ExtractBytes(byte[] bytes, string destination, int depth)
    {
        using var zip = OpenNavx(bytes);
        var inner = zip.Entries.FirstOrDefault(e =>
            e.FullName.EndsWith(".app", StringComparison.OrdinalIgnoreCase) && !e.FullName.Contains('/'));
        if (inner is not null && depth < 4)
        {
            using var s = inner.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            ExtractBytes(ms.ToArray(), destination, depth + 1);
            return;
        }

        foreach (var e in zip.Entries)
        {
            if (e.FullName.EndsWith("/")) continue;
            var target = Path.GetFullPath(Path.Combine(destination, e.FullName.Replace('\\', '/')));
            if (!target.StartsWith(Path.GetFullPath(destination), StringComparison.Ordinal)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var src = e.Open();
            using var dst = File.Create(target);
            src.CopyTo(dst);
        }
    }

    private static ZipArchive OpenNavx(byte[] bytes)
    {
        var offset = bytes.Length >= 8 && bytes[0] == (byte)'N' && bytes[1] == (byte)'A'
                     && bytes[2] == (byte)'V' && bytes[3] == (byte)'X'
            ? (int)BitConverter.ToUInt32(bytes, 4)
            : 0;
        return new ZipArchive(new MemoryStream(bytes, offset, bytes.Length - offset, writable: false),
            ZipArchiveMode.Read);
    }

    /// <summary>Read a manifest out of a .app without extracting the whole package.</summary>
    public static AppIdentity ReadIdentity(string appPath)
    {
        var bytes = File.ReadAllBytes(appPath);
        var xml = ReadManifestXml(bytes, depth: 0)
                  ?? throw new InvalidDataException($"{appPath}: no NavxManifest.xml in the package.");
        return ParseIdentity(xml);
    }

    private static string? ReadManifestXml(byte[] bytes, int depth)
    {
        using var zip = OpenNavx(bytes);
        var manifest = zip.GetEntry("NavxManifest.xml");
        if (manifest is not null)
        {
            using var r = new StreamReader(manifest.Open());
            return r.ReadToEnd();
        }
        var inner = zip.Entries.FirstOrDefault(e =>
            e.FullName.EndsWith(".app", StringComparison.OrdinalIgnoreCase) && !e.FullName.Contains('/'));
        if (inner is null || depth >= 4) return null;
        using var s = inner.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ReadManifestXml(ms.ToArray(), depth + 1);
    }

    public static AppIdentity ParseIdentity(string manifestXml)
    {
        var doc = new XmlDocument();
        doc.LoadXml(manifestXml);
        var ns = new XmlNamespaceManager(doc.NameTable);
        ns.AddNamespace("m", Ns);
        var app = (XmlElement?)doc.SelectSingleNode("//m:App", ns)
                  ?? throw new InvalidDataException("NavxManifest.xml has no <App> element.");

        var deps = new List<(Guid, string, string, Version)>();
        foreach (XmlElement d in doc.SelectNodes("//m:Dependencies/m:Dependency", ns)!)
            deps.Add((Guid.Parse(d.GetAttribute("Id")), d.GetAttribute("Name"),
                d.GetAttribute("Publisher"), Version.Parse(d.GetAttribute("MinVersion"))));

        return new AppIdentity(
            Guid.Parse(app.GetAttribute("Id")),
            app.GetAttribute("Name"),
            app.GetAttribute("Publisher"),
            Version.Parse(app.GetAttribute("Version")),
            app.GetAttribute("Platform"),
            app.GetAttribute("Target"),
            app.GetAttribute("ContextSensitiveHelpUrl"),
            doc.SelectNodes("//m:Features/m:Feature", ns)!.Cast<XmlNode>().Select(n => n.InnerText).ToArray(),
            doc.SelectNodes("//m:PreprocessorSymbols/m:PreprocessorSymbol", ns)!.Cast<XmlNode>().Select(n => n.InnerText).ToArray(),
            deps);
    }
}

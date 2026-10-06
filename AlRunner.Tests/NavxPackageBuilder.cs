using System.IO.Compression;
using System.Text;

namespace AlRunner.Tests;

/// <summary>A hand-built .app package (NAVX header around an OPC zip) for tests that need a library or
/// dependency whose bytes they control. Shared by the fixtures that rebuild one in place.</summary>
internal static class NavxPackageBuilder
{
    internal static byte[] Build(string appId, string manifest, string symbols, (string Name, string Content)[] sources)
    {
        using var zipBuffer = new MemoryStream();
        using (var zip = new ZipArchive(zipBuffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string content)
            {
                var entry = zip.CreateEntry(name);
                // Fixed, so a package built twice from the same inputs is byte-identical and the
                // third run can match the first run's content term.
                entry.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var w = new StreamWriter(entry.Open());
                w.Write(content);
            }
            // Without the OPC content-types part BC's compiler rejects the package (AL1023).
            Add("[Content_Types].xml", """
                <?xml version="1.0" encoding="utf-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="xml" ContentType="" /><Default Extension="al" ContentType="" /><Default Extension="json" ContentType="" /></Types>
                """);
            Add("NavxManifest.xml", manifest);
            Add("SymbolReference.json", symbols);
            foreach (var (name, content) in sources) Add(name, content);
        }
        var payload = zipBuffer.ToArray();
        using var app = new MemoryStream();
        using var bw = new BinaryWriter(app);
        bw.Write(Encoding.ASCII.GetBytes("NAVX"));
        bw.Write(40);
        bw.Write(2);
        bw.Write(Guid.Parse(appId).ToByteArray());
        bw.Write((long)payload.Length);
        bw.Write(Encoding.ASCII.GetBytes("NAVX"));
        bw.Write(payload);
        bw.Flush();
        return app.ToArray();
    }
}

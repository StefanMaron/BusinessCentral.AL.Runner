// #5430: --tdd with a test that uses a FIELD a package's table does not declare. The package is a .app in the
// test folder's .alpackages (symbols plus the app's own source, Tier 3, or plus a precompiled DLL, Tier 1); the
// field is generated beside the table, in a tableextension of the test's own compile. Runner-specific (--tdd
// turning a compile error into a generated object the tests run against), so it lives here, not in the
// al-language corpus. The design: docs/tdd-precompiled.md.
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public sealed class TddPackageMemberTests : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string FixtureRoot = Path.Combine(RepoRoot, "AlRunner.Tests", "Fixtures", "TddPackageMembers");

    private const string DepAppId = "e2b4c6a8-1d3f-4a5b-9c7e-8f0a2b4d6e10";
    private const string DepFileStem = "AL_Runner_Fixtures_Tdd_Package_Members_Dep_1.0.0.0";

    private readonly string _scratch;

    public TddPackageMemberTests()
    {
        _scratch = TestScratch.Dir("al-runner-tdd-package-members");
        Directory.CreateDirectory(_scratch);
    }

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch { }
    }

    /// <summary>The package of the fixture's dep app: what a compiled .app carries (symbols), and optionally the source.</summary>
    internal static byte[] BuildDepApp(bool embedSource)
    {
        var manifest = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/navx/2015/manifest">
              <App Id="{DepAppId}" Name="Tdd Package Members Dep" Publisher="AL Runner Fixtures" Version="1.0.0.0" Target="Cloud" ShowMyCode="true" PropagateDependencies="false" />
              <IdRanges><IdRange MinObjectId="65380" MaxObjectId="65399" /></IdRanges>
              <Dependencies />
            </Package>
            """;
        var symbols = """
            {"Tables":[{"Id":65380,"Name":"Loyalty Entry","Fields":[
              {"Id":1,"Name":"Id","TypeDefinition":{"Name":"Integer"},"Properties":[]},
              {"Id":2,"Name":"Existing Points","TypeDefinition":{"Name":"Integer"},"Properties":[]}
            ],"Keys":[{"Name":"PK","FieldNames":["Id"],"Properties":[{"Name":"Clustered","Value":"1"}]}],
            "ReferenceSourceFileName":"LoyaltyEntry.Table.al","Properties":[]}],
            "EnumTypes":[{"Id":65381,"Name":"Loyalty Tier","Values":[
              {"Ordinal":0,"Name":"Bronze","Properties":[]},{"Ordinal":1,"Name":"Silver","Properties":[]}
            ],"ReferenceSourceFileName":"LoyaltyTier.Enum.al","Properties":[{"Name":"Extensible","Value":"true"}]}],
            "AppId":"@APPID@","Name":"Tdd Package Members Dep","Publisher":"AL Runner Fixtures","Version":"1.0.0.0"}
            """.Replace("@APPID@", DepAppId);
        using var zipBuffer = new MemoryStream();
        using (var zip = new ZipArchive(zipBuffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string content)
            {
                var entry = zip.CreateEntry(name);
                using var w = new StreamWriter(entry.Open());
                w.Write(content);
            }
            Add("[Content_Types].xml", """<?xml version="1.0" encoding="utf-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="xml" ContentType="" /><Default Extension="json" ContentType="" /><Default Extension="al" ContentType="" /></Types>""");
            Add("NavxManifest.xml", manifest);
            Add("SymbolReference.json", symbols);
            if (embedSource)
                foreach (var f in Directory.GetFiles(Path.Combine(FixtureRoot, "dep"), "*.al"))
                    Add("src/" + Path.GetFileName(f), File.ReadAllText(f));
        }
        var payload = zipBuffer.ToArray();
        using var app = new MemoryStream();
        using var bw = new BinaryWriter(app);
        bw.Write(Encoding.ASCII.GetBytes("NAVX"));
        bw.Write(40);
        bw.Write(2);
        bw.Write(Guid.Parse(DepAppId).ToByteArray());
        bw.Write((long)payload.Length);
        bw.Write(Encoding.ASCII.GetBytes("NAVX"));
        bw.Write(payload);
        bw.Flush();
        return app.ToArray();
    }

    private string MakeTestFolder(string name)
    {
        var dir = TddMissingObjectTests.CopyFolder(Path.Combine(FixtureRoot, "test"), Path.Combine(_scratch, name));
        Directory.CreateDirectory(Path.Combine(dir, ".alpackages"));
        File.WriteAllBytes(Path.Combine(dir, ".alpackages", DepFileStem + ".app"), BuildDepApp(embedSource: true));
        return dir;
    }

    [SkippableFact]
    public void Probe()
    {
        TestArtifacts.SkipIfMissing();
        var test = MakeTestFolder("probe");
        var (stdout, stderr, exit) = TddMissingObjectTests.RunRunner(null, "--tdd", $"--cache \"{Path.Combine(_scratch, "cache")}\"", "--output-json", $"\"{test}\"");
        File.WriteAllText("/home/stefan/.cache/claude-tmp/claude-1000/-home-stefan-Documents-Repos-Comunity-BusinessCentral-AL-Runner/3fb969c6-0b1e-4615-8cb2-09ad566b1b50/scratchpad/agent-stma-auto-7-issue-5430-01812535/probe.txt", $"exit {exit}\n--- stdout\n{stdout}\n--- stderr\n{stderr}");
    }
}

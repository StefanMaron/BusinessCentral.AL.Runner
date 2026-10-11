// #5491: --tdd generates a field of a package's table (a tableextension beside it) and a value of a package's enum (an enumextension)
// on the first id of the test app's idRanges that nothing uses. "Nothing" must include what ANOTHER app's extension of the same table
// or enum already adds: a second package here, whose extensions sit in the ids the test app may allocate from (an app and its test app
// sharing a range, the PTE case). Runner-specific (a --tdd choice of id), so it lives here, not in the al-language corpus.
// The design: docs/tdd-precompiled.md.
using System.Text;
using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

public sealed class TddPackageMemberOtherAppExtensionTests : IDisposable
{
    private static readonly string FixtureRoot = Path.Combine(TddPackageMemberTests.FixtureRoot, "..", "TddPackageMemberExt");
    private const string DepAppId = "8a88783b-4518-4c72-bf00-f4690193d949";
    private const string Pkg2AppId = "9f0c2ae1-c0fa-4d6f-84e7-a2f8c2f55413";

    private readonly string _scratch;

    public TddPackageMemberOtherAppExtensionTests()
    {
        _scratch = TestScratch.Dir("al-runner-tdd-package-member-ext");
        Directory.CreateDirectory(_scratch);
    }

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch { }
    }

    private static string FieldJson(int id, string name) =>
        $"{{\"Id\":{id},\"Name\":\"{name}\",\"TypeDefinition\":{{\"Name\":\"Integer\"}},\"Properties\":[]}}";

    private static string Table(int id, string name, int? ownExtra) =>
        $"{{\"Id\":{id},\"Name\":\"{name}\",\"Fields\":[{FieldJson(1, "Id")}{(ownExtra is { } x ? "," + FieldJson(x, "Existing Points") : "")}]," +
        "\"Keys\":[{\"Name\":\"PK\",\"FieldNames\":[\"Id\"],\"Properties\":[{\"Name\":\"Clustered\",\"Value\":\"1\"}]}]," +
        $"\"ReferenceSourceFileName\":\"{name.Replace(" ", "")}.Table.al\",\"Properties\":[]}}";

    private static string Enum(int id, string name, int? ownExtra) =>
        $"{{\"Id\":{id},\"Name\":\"{name}\",\"Values\":[{{\"Ordinal\":0,\"Name\":\"Bronze\",\"Properties\":[]}}" +
        (ownExtra is { } x ? $",{{\"Ordinal\":{x},\"Name\":\"Legacy\",\"Properties\":[]}}" : "") + "]," +
        $"\"ReferenceSourceFileName\":\"{name.Replace(" ", "")}.Enum.al\",\"Properties\":[{{\"Name\":\"Extensible\",\"Value\":\"true\"}}]}}";

    /// <summary>The package the test's table and enum come from: its own members are id 1 (and 65400 for the Loyalty ones) and ordinal 0 (and 65400).</summary>
    private static readonly string DepSymbols =
        "{\"Tables\":[" + Table(65380, "Loyalty Entry", 65400) + "," + Table(65381, "Edge Entry", null) + "," + Table(65382, "Full Entry", null) + "]," +
        "\"EnumTypes\":[" + Enum(65383, "Loyalty Tier", 65400) + "," + Enum(65384, "Edge Tier", null) + "," + Enum(65385, "Full Tier", null) + "]," +
        "\"AppId\":\"@APPID@\",\"Name\":\"Tdd Member Ext Dep\",\"Publisher\":\"AL Runner Fixtures\",\"Version\":\"1.0.0.0\"}";

    /// <summary>The second package: extensions of the dep's objects whose members hold ids of the test app's range 65400-65419
    /// ("Loyalty": 65401; "Edge": all but 65419; "Full": all of them).</summary>
    private static readonly string Pkg2Symbols = BuildPkg2Symbols();

    private static string BuildPkg2Symbols()
    {
        string TableExt(int id, string name, string target, int from, int to) =>
            $"{{\"Id\":{id},\"Name\":\"{name}\",\"TargetObject\":\"{target}\",\"Fields\":[" +
            string.Join(",", Enumerable.Range(from, to - from + 1).Select(n => FieldJson(n, $"{name} F{n}"))) + "],\"Properties\":[]}";
        string EnumExt(int id, string name, string target, int from, int to) =>
            $"{{\"Id\":{id},\"Name\":\"{name}\",\"TargetObject\":\"{target}\",\"Values\":[" +
            string.Join(",", Enumerable.Range(from, to - from + 1).Select(n => $"{{\"Ordinal\":{n},\"Name\":\"{name} V{n}\",\"Properties\":[]}}")) + "],\"Properties\":[]}";
        return "{\"TableExtensions\":[" +
               TableExt(65395, "Pkg2 Loyalty Ext", "Loyalty Entry", 65401, 65401) + "," +
               TableExt(65396, "Pkg2 Edge Ext", "Edge Entry", 65400, 65418) + "," +
               TableExt(65397, "Pkg2 Full Ext", "Full Entry", 65400, 65419) + "]," +
               "\"EnumExtensionTypes\":[" +
               EnumExt(65395, "Pkg2 Loyalty Tier Ext", "Loyalty Tier", 65401, 65401) + "," +
               EnumExt(65396, "Pkg2 Edge Tier Ext", "Edge Tier", 65400, 65418) + "," +
               EnumExt(65397, "Pkg2 Full Tier Ext", "Full Tier", 65400, 65419) + "]," +
               "\"AppId\":\"@APPID@\",\"Name\":\"Tdd Member Ext Pkg2\",\"Publisher\":\"AL Runner Fixtures\",\"Version\":\"1.0.0.0\"}";
    }

    /// <summary>A package as a compiled .app carries it: symbols only, and a dependency on <paramref name="dependsOn"/> when given
    /// (the extensions of the second package can name the first one's objects only through it).</summary>
    private static byte[] BuildApp(string appId, string name, int idFrom, int idTo, string symbols, (string Id, string Name)? dependsOn, string? sourceDir = null)
    {
        var dependencies = dependsOn is { } d
            ? $"<Dependencies><Dependency Id=\"{d.Id}\" Name=\"{d.Name}\" Publisher=\"AL Runner Fixtures\" MinVersion=\"1.0.0.0\" /></Dependencies>"
            : "<Dependencies />";
        var manifest = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/navx/2015/manifest">
              <App Id="{appId}" Name="{name}" Publisher="AL Runner Fixtures" Version="1.0.0.0" Target="Cloud" ShowMyCode="true" PropagateDependencies="false" />
              <IdRanges><IdRange MinObjectId="{idFrom}" MaxObjectId="{idTo}" /></IdRanges>
              {dependencies}
            </Package>
            """;
        using var zipBuffer = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(zipBuffer, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string entryName, string content)
            {
                using var w = new StreamWriter(zip.CreateEntry(entryName).Open());
                w.Write(content);
            }
            Add("[Content_Types].xml", """<?xml version="1.0" encoding="utf-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="xml" ContentType="" /><Default Extension="json" ContentType="" /><Default Extension="al" ContentType="" /></Types>""");
            Add("NavxManifest.xml", manifest);
            Add("SymbolReference.json", symbols.Replace("@APPID@", appId));
            if (sourceDir != null)
                foreach (var f in Directory.GetFiles(sourceDir, "*.al"))
                    Add("src/" + Path.GetFileName(f), File.ReadAllText(f));
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

    [SkippableFact]
    public void GeneratedMembers_SkipTheIdsAnotherAppsExtensionsAdd()
    {
        TestArtifacts.SkipIfMissing();
        var test = TddMissingObjectTests.CopyFolder(Path.Combine(FixtureRoot, "test"), Path.Combine(_scratch, "test"));
        Directory.CreateDirectory(Path.Combine(test, ".alpackages"));
        File.WriteAllBytes(Path.Combine(test, ".alpackages", "AL_Runner_Fixtures_Tdd_Member_Ext_Dep_1.0.0.0.app"),
            BuildApp(DepAppId, "Tdd Member Ext Dep", 65380, 65389, DepSymbols, null, Path.Combine(FixtureRoot, "dep")));
        File.WriteAllBytes(Path.Combine(test, ".alpackages", "AL_Runner_Fixtures_Tdd_Member_Ext_Pkg2_1.0.0.0.app"),
            BuildApp(Pkg2AppId, "Tdd Member Ext Pkg2", 65395, 65399, Pkg2Symbols, (DepAppId, "Tdd Member Ext Dep")));

        var (stdout, stderr, exit) = TddMissingObjectTests.RunRunner(null, "--tdd", $"--cache \"{Path.Combine(_scratch, "cache")}\"", "--output-json", $"\"{test}\"");

        // The other package's extensions hold 65401 (Loyalty), 65400-65418 (Edge) and 65400-65419 (Full): a generated member takes a free id
        // of the range, with the table's / enum's own 65400 skipped as well, and the compile accepts it.
        Assert.DoesNotContain("AL0206", stdout + stderr);
        Assert.DoesNotContain("AL0231", stdout + stderr);
        Assert.DoesNotContain("AL-DIAGNOSTIC-FAIL", stdout + stderr);
        Assert.True(exit == 1, $"exit {exit}\n{stderr}"); // the Full file fails, as it did before --tdd
        using var doc = JsonDocument.Parse(stdout.Trim());
        var tests = doc.RootElement.GetProperty("tests").EnumerateArray().ToList();
        JsonElement Find(string name) => tests.Single(t => t.GetProperty("name").GetString()!.EndsWith("." + name));
        void AssertPasses(string name, string stub)
        {
            var t = Find(name);
            Assert.True(t.GetProperty("status").GetString() == "pass", $"{name}: {t}");
            Assert.Equal(new[] { stub }, TddMissingObjectTests.StubsOf(t));
        }
        AssertPasses("NewField_TakesAFreeId", "Loyalty Entry: field \"Points\": Integer");
        AssertPasses("NewValue_TakesAFreeOrdinal", "Loyalty Tier: enum value \"Gold\" = 65402");
        AssertPasses("EdgeField_TakesTheLastId", "Edge Entry: field \"Edged\": Integer");
        AssertPasses("EdgeValue_TakesTheLastOrdinal", "Edge Tier: enum value \"Edged\" = 65419");
        Assert.Equal(6, doc.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(4, doc.RootElement.GetProperty("passed").GetInt32());

        // Nothing is free for "Full": refused with the reason, and its tests are FAILED on the original AL0132, naming no stub.
        foreach (var name in new[] { "FullField_IsRefused", "FullValue_IsRefused" })
        {
            var t = Find(name);
            Assert.Equal("fail", t.GetProperty("status").GetString());
            Assert.Contains("AL0132", t.GetProperty("message").GetString());
            Assert.Empty(TddMissingObjectTests.StubsOf(t));
        }
        var lines = stderr.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        Assert.Equal(new[]
        {
            "--tdd: table extension \"TDD Table Extension 65400\" (id 65400) beside package table Edge Entry in A2Edge.Codeunit.al: \"Edged\" (field 65419)",
            "--tdd: table extension \"TDD Table Extension 65401\" (id 65401) beside package table Loyalty Entry in A1Loyalty.Codeunit.al: \"Points\" (field 65402)",
            "--tdd: enum extension \"TDD Enum Extension 65400\" (id 65400) beside package enum Edge Tier in A2Edge.Codeunit.al: \"Edged\" (ordinal 65419)",
            "--tdd: enum extension \"TDD Enum Extension 65401\" (id 65401) beside package enum Loyalty Tier in A1Loyalty.Codeunit.al: \"Gold\" (ordinal 65402)",
        }, lines.Where(l => l.StartsWith("--tdd: table extension") || l.StartsWith("--tdd: enum extension")).ToList());
        Assert.Contains("--tdd: not generated beside package table Full Entry: \"Fulled\" - no free field id in the app's idRanges for it", stderr);
        Assert.Contains("--tdd: not generated beside package enum Full Tier: \"Fulled\" - no free ordinal in the app's idRanges for it", stderr);
        Assert.Contains("--tdd: generated 4 member(s) this run:", stderr);
    }
}

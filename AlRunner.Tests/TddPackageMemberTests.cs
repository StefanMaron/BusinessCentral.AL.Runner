// #5430: --tdd with tests that use a FIELD a package's table does not declare, or a VALUE a package's enum does not
// declare. The package is a .app in the test folder's .alpackages (symbols plus a precompiled DLL, Tier 1, or plus the
// app's own source, Tier 3); the field is generated beside the table, in a tableextension of the test's own compile,
// and the value in an enumextension. Runner-specific (--tdd turning a compile error into a generated object the
// tests run against), so it lives here, not in the al-language corpus. The design: docs/tdd-precompiled.md.
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Threading;
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

    private const string TwinAppId = "5c3a1f87-9d24-4e60-b8a2-6f1e0d3c7b95";
    private const string TwinFileStem = "AL_Runner_Fixtures_Tdd_Package_Members_Twin_1.0.0.0";

    private const string DepSymbols = """
        {"Tables":[
          {"Id":65380,"Name":"Loyalty Entry","Fields":[
            {"Id":1,"Name":"Id","TypeDefinition":{"Name":"Integer"},"Properties":[]},
            {"Id":65400,"Name":"Existing Points","TypeDefinition":{"Name":"Integer"},"Properties":[]}
          ],"Keys":[{"Name":"PK","FieldNames":["Id"],"Properties":[{"Name":"Clustered","Value":"1"}]}],
          "ReferenceSourceFileName":"LoyaltyEntry.Table.al","Properties":[]},
          {"Id":65384,"Name":"Sealed Entry","Fields":[
            {"Id":1,"Name":"Id","TypeDefinition":{"Name":"Integer"},"Properties":[]}
          ],"Keys":[{"Name":"PK","FieldNames":["Id"],"Properties":[{"Name":"Clustered","Value":"1"}]}],
          "ReferenceSourceFileName":"SealedEntry.Table.al","Properties":[{"Name":"Extensible","Value":"false"}]}
        ],
        "EnumTypes":[
          {"Id":65381,"Name":"Loyalty Tier","Values":[
            {"Ordinal":0,"Name":"Bronze","Properties":[]},{"Ordinal":1,"Name":"Silver","Properties":[]},{"Ordinal":65400,"Name":"Legacy","Properties":[]}
          ],"ReferenceSourceFileName":"LoyaltyTier.Enum.al","Properties":[{"Name":"Extensible","Value":"true"}]},
          {"Id":65382,"Name":"Closed Tier","Values":[
            {"Ordinal":0,"Name":"Alpha","Properties":[]}
          ],"ReferenceSourceFileName":"ClosedTier.Enum.al","Properties":[]}
        ],
        "Namespaces":[{"Name":"Loyalty","Namespaces":[{"Name":"Data","Tables":[
          {"Id":65383,"Name":"Namespaced Entry","Fields":[
            {"Id":1,"Name":"Id","TypeDefinition":{"Name":"Integer"},"Properties":[]},
            {"Id":2,"Name":"Existing Points","TypeDefinition":{"Name":"Integer"},"Properties":[]}
          ],"Keys":[{"Name":"PK","FieldNames":["Id"],"Properties":[{"Name":"Clustered","Value":"1"}]}],
          "ReferenceSourceFileName":"NamespacedEntry.Table.al","Properties":[]}
        ]}]}],
        "AppId":"@APPID@","Name":"Tdd Package Members Dep","Publisher":"AL Runner Fixtures","Version":"1.0.0.0"}
        """;

    /// <summary>A second package declaring a table of the same name as the dep's "Namespaced Entry", in another namespace. One
    /// app cannot declare two tables of one name (AL0197) whatever their namespaces, so the twin is another app.</summary>
    private const string TwinSymbols = """
        {"Namespaces":[{"Name":"Other","Namespaces":[{"Name":"Data","Tables":[
          {"Id":65385,"Name":"Namespaced Entry","Fields":[
            {"Id":1,"Name":"Id","TypeDefinition":{"Name":"Integer"},"Properties":[]},
            {"Id":2,"Name":"Twin Points","TypeDefinition":{"Name":"Integer"},"Properties":[]}
          ],"Keys":[{"Name":"PK","FieldNames":["Id"],"Properties":[{"Name":"Clustered","Value":"1"}]}],
          "ReferenceSourceFileName":"TwinEntry.Table.al","Properties":[]}
        ]}]}],
        "AppId":"@APPID@","Name":"Tdd Package Members Twin","Publisher":"AL Runner Fixtures","Version":"1.0.0.0"}
        """;

    /// <summary>A package as a compiled .app carries it: symbols, and optionally the app's own source.</summary>
    private static byte[] BuildApp(string appId, string name, int idFrom, int idTo, string symbols, string? sourceDir)
    {
        var manifest = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/navx/2015/manifest">
              <App Id="{appId}" Name="{name}" Publisher="AL Runner Fixtures" Version="1.0.0.0" Target="Cloud" ShowMyCode="true" PropagateDependencies="false" />
              <IdRanges><IdRange MinObjectId="{idFrom}" MaxObjectId="{idTo}" /></IdRanges>
              <Dependencies />
            </Package>
            """;
        using var zipBuffer = new MemoryStream();
        using (var zip = new ZipArchive(zipBuffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string entryName, string content)
            {
                var entry = zip.CreateEntry(entryName);
                using var w = new StreamWriter(entry.Open());
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

    /// <summary>The fixture's test folder copied under the scratch directory, with the dep app as a package in its
    /// .alpackages and, for the DLL shape, the precompiled DLL in its .deps-bin.</summary>
    private string MakeTestFolder(string name, bool withDll)
    {
        var dir = TddMissingObjectTests.CopyFolder(Path.Combine(FixtureRoot, "test"), Path.Combine(_scratch, name));
        Directory.CreateDirectory(Path.Combine(dir, ".alpackages"));
        File.WriteAllBytes(Path.Combine(dir, ".alpackages", DepFileStem + ".app"),
            BuildApp(DepAppId, "Tdd Package Members Dep", 65380, 65384, DepSymbols, withDll ? null : Path.Combine(FixtureRoot, "dep")));
        // The twin always ships its source: one package of each tier in the DLL run.
        File.WriteAllBytes(Path.Combine(dir, ".alpackages", TwinFileStem + ".app"),
            BuildApp(TwinAppId, "Tdd Package Members Twin", 65385, 65389, TwinSymbols, Path.Combine(FixtureRoot, "twin")));
        if (withDll)
        {
            Directory.CreateDirectory(Path.Combine(dir, ".deps-bin"));
            File.WriteAllBytes(Path.Combine(dir, ".deps-bin", DepFileStem + ".dll"), DepDll.Value);
        }
        return dir;
    }

    /// <summary>The fixture's dep/ app compiled by the runner under test, as the DLL a package ships (a DLL checked in
    /// was built against one BC major's Ncl and threw on another; see TddPrecompiledTests).</summary>
    private static readonly Lazy<byte[]> DepDll = new(() =>
    {
        var cache = Path.Combine(TestScratch.Dir("al-runner-tdd-package-members-dep"), "cache");
        var (stdout, stderr, exit) = TddMissingObjectTests.RunRunner(null, $"--cache \"{cache}\"", $"\"{Path.Combine(FixtureRoot, "dep")}\"");
        Assert.True(exit == 0, $"compiling dep/ exit {exit}\n{stdout}\n{stderr}");
        var dlls = Directory.GetFiles(cache, "*.dll", SearchOption.TopDirectoryOnly);
        Assert.True(dlls.Length == 1, $"expected one DLL in {cache}, found {dlls.Length}: {string.Join(", ", dlls)}");
        return File.ReadAllBytes(dlls[0]);
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    private (string StdOut, string StdErr, int Exit) RunTdd(string test, (string, string)? env = null) =>
        TddMissingObjectTests.RunRunner(env, "--tdd", $"--cache \"{Path.Combine(_scratch, "cache-" + Path.GetFileName(test))}\"", "--output-json", $"\"{test}\"");

    private const string PointsStub = "Loyalty Entry: field \"Points\": Integer";
    private const string RankStub = "Loyalty Entry: field \"Rank\": Integer";

    /// <summary>The outcome both package shapes, and either order of the compile's diagnostics, must give.</summary>
    private static void AssertTheRun((string StdOut, string StdErr, int Exit) run)
    {
        var (stdout, stderr, exit) = run;
        Assert.True(exit == 1, $"exit {exit}\n{stderr}");
        using var doc = JsonDocument.Parse(stdout.Trim());
        var tests = doc.RootElement.GetProperty("tests").EnumerateArray().ToList();
        JsonElement Find(string name) => tests.Single(t => t.GetProperty("name").GetString()!.EndsWith("." + name));
        void AssertPasses(string name, params string[] stubs)
        {
            var t = Find(name);
            Assert.True(t.GetProperty("status").GetString() == "pass", $"{name}: {t}");
            Assert.Equal(stubs.OrderBy(x => x, StringComparer.Ordinal), TddMissingObjectTests.StubsOf(t).OrderBy(x => x, StringComparer.Ordinal));
        }
        void AssertRefused(string name, string diagnostic)
        {
            var t = Find(name);
            Assert.Equal("fail", t.GetProperty("status").GetString());
            Assert.Contains(diagnostic, t.GetProperty("message").GetString());
            Assert.Empty(TddMissingObjectTests.StubsOf(t));
        }

        // A field the package table lacks is read back through the table, in either file that names it; the package's own
        // fields and values keep running as before and name no stub.
        AssertPasses("NewField_RoundTripsThroughTheTable", PointsStub);
        AssertPasses("SameField_InASecondFile", PointsStub);
        AssertPasses("Rank_TheFirstFileDecidesItsType", RankStub);
        AssertPasses("ExistingField_IsThePackagesOwn");
        AssertPasses("NewValue_IsItsOwnValue", "Loyalty Tier: enum value \"Gold\" = 65402");
        AssertPasses("TwoNewValues_AreTwoValues", "Loyalty Tier: enum value \"Gold\" = 65402", "Loyalty Tier: enum value \"Platinum\" = 65403");
        AssertPasses("ExistingValue_IsThePackagesOwn");
        AssertPasses("NamespacedTable_GetsItsFieldFromAnExtension", "Loyalty.Data.Namespaced Entry: field \"Bonus\": Integer");
        AssertPasses("TwinTable_GetsItsOwnExtension", "Other.Data.Namespaced Entry: field \"Bonus\": Integer");

        // "Rank" is typed by the first file (file order) that assigns it; the second file, assigning a Boolean, no longer compiles.
        var loser = Find("Rank_TheSecondFileLoses");
        Assert.Equal("fail", loser.GetProperty("status").GetString());
        Assert.Contains("'Boolean'", loser.GetProperty("message").GetString());
        Assert.Empty(TddMissingObjectTests.StubsOf(loser));

        // Not generated: an enum or a table the package does not let anyone extend, and a field nothing assigns.
        AssertRefused("NonExtensibleEnum_IsRefused", "error AL0132: '\"Closed Tier\"' does not contain a definition for 'Omega'");
        AssertRefused("NonExtensibleTable_IsRefused", "error AL0132: 'Record \"Sealed Entry\"' does not contain a definition for 'Extra'");
        AssertRefused("FieldOnlyRead_IsRefused", "error AL0132: 'Record \"Loyalty Entry\"' does not contain a definition for 'Unassigned'");

        Assert.Equal(13, doc.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(9, doc.RootElement.GetProperty("passed").GetInt32());
        Assert.Equal(4, doc.RootElement.GetProperty("failed").GetInt32());

        // One extension per table or enum, in the first file (file order) that names a missing member of it. The test app's range
        // is 65400-65419 and the ids are free for THAT object type: the nine codeunits hold 65400-65408 and do not matter, the
        // app's own tableextension and enumextension (A0Existing.al) hold 65400 and do. A field id also skips the package
        // table's own "Existing Points" (65400) and the app's extension field (65401); an ordinal skips the package enum's
        // "Legacy" (65400) and the app's extension value (65401). Tables and enums are taken in name order.
        var lines = stderr.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        Assert.Equal(new[]
        {
            "--tdd: table extension \"TDD Table Extension 65401\" (id 65401) beside package table Loyalty Entry in A1FieldTests.Codeunit.al: \"Points\" (field 65402), \"Rank\" (field 65403)",
            "--tdd: table extension \"TDD Table Extension 65402\" (id 65402) beside package table Loyalty.Data.Namespaced Entry in A4NamespaceTests.Codeunit.al: \"Bonus\" (field 65400)",
            "--tdd: table extension \"TDD Table Extension 65403\" (id 65403) beside package table Other.Data.Namespaced Entry in A9TwinTests.Codeunit.al: \"Bonus\" (field 65400)",
            "--tdd: enum extension \"TDD Enum Extension 65401\" (id 65401) beside package enum Loyalty Tier in A2ValueTests.Codeunit.al: \"Gold\" (ordinal 65402), \"Platinum\" (ordinal 65403)",
        }, lines.Where(l => l.StartsWith("--tdd: table extension") || l.StartsWith("--tdd: enum extension")).ToList());
        Assert.Contains("--tdd: not generated beside package table Loyalty Entry: \"Unassigned\" - no assignment `Rec.Field := <expression>` fixes its type", stderr);
        Assert.Contains("--tdd: not generated beside package table Sealed Entry: \"Extra\" - the table is not extensible", stderr);
        Assert.Contains("--tdd: not generated beside package enum Closed Tier: \"Omega\" - the enum is not Extensible", stderr);
        Assert.DoesNotContain("no members were generated", stdout + stderr);
        Assert.Contains("--tdd: generated 6 member(s) this run:", stderr);
    }

    /// <summary>
    /// The package is the table's and enum's DLL plus symbols (Tier 1): no source of them is read by the run, and the
    /// record engine's shape of the table comes from the symbols. Each missing field and value is generated in an
    /// extension of the test's own compile; what the package declares runs as before; nothing is written.
    /// </summary>
    [SkippableFact]
    public void PackageDll_MissingFieldsAndValuesRunInExtensionsBesideTheObjects()
    {
        TestArtifacts.SkipIfMissing();
        var test = MakeTestFolder("test-dll", withDll: true);
        var before = TddMissingObjectTests.HashTree(test);

        var run = RunTdd(test);

        AssertTheRun(run);
        Assert.Equal(before, TddMissingObjectTests.HashTree(test));
    }

    /// <summary>
    /// The same outcome when the package carries the app's own source and no DLL (Tier 3: the run compiles it, so the record
    /// engine has the table from its source) and the compile's diagnostics are fed backwards (they are reported in an order
    /// that changes between runs): the ids, the files and the shapes do not follow it.
    /// </summary>
    [SkippableFact]
    public void PackagedSource_ReversedDiagnosticOrder_GivesTheSameOutcome()
    {
        TestArtifacts.SkipIfMissing();
        var test = MakeTestFolder("test-src", withDll: false);

        var run = RunTdd(test, ("AL_RUNNER_TDD_DIAG_ORDER", "reverse"));

        AssertTheRun(run);
    }

    /// <summary>
    /// Every tableextension and enumextension id of the app's range is already taken (by extensions the test app declares), so
    /// the extensions cannot be generated: nothing is, every test that needs one is FAILED as before, and the run says why.
    /// Ids are per object type: the nine codeunits holding the same numbers do not matter, and the fillers do.
    /// </summary>
    [SkippableFact]
    public void NoFreeExtensionId_RefusesAndSaysSo()
    {
        TestArtifacts.SkipIfMissing();
        var test = MakeTestFolder("test-no-ids", withDll: false);
        var manifest = Path.Combine(test, "app.json");
        File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("\"to\": 65419", "\"to\": 65408"));
        var fillers = new StringBuilder();
        for (var id = 65401; id <= 65408; id++) // 65400 is A0Existing.al's
        {
            fillers.AppendLine($"tableextension {id} \"Filler Table {id}\" extends \"Loyalty Entry\" {{ fields {{ field({id + 100}; \"Filler {id}\"; Integer) {{ }} }} }}");
            fillers.AppendLine($"enumextension {id} \"Filler Enum {id}\" extends \"Loyalty Tier\" {{ value({id + 100}; \"Filler {id}\") {{ }} }}");
        }
        File.WriteAllText(Path.Combine(test, "Fillers.al"), fillers.ToString());

        var (stdout, stderr, exit) = RunTdd(test);

        Assert.True(exit == 1, $"exit {exit}\n{stderr}");
        using var doc = JsonDocument.Parse(stdout.Trim());
        var tests = doc.RootElement.GetProperty("tests").EnumerateArray().ToList();
        Assert.All(tests, t => Assert.Empty(TddMissingObjectTests.StubsOf(t)));
        Assert.Contains("--tdd: not generated beside package table Loyalty Entry: \"Points\" - no free tableextension id in the app's idRanges for it", stderr);
        Assert.Contains("--tdd: not generated beside package table Loyalty.Data.Namespaced Entry: \"Bonus\" - no free tableextension id in the app's idRanges for it", stderr);
        Assert.Contains("--tdd: not generated beside package enum Loyalty Tier: \"Gold\" - no free enumextension id in the app's idRanges for it", stderr);
        Assert.DoesNotContain("TDD Table Extension", stderr);
        Assert.DoesNotContain("TDD Enum Extension", stderr);
        Assert.Contains("no members were generated", stdout + stderr);
        // Every file names a missing member of a package table or enum, so every file is dropped and every test FAILED on an
        // AL0132 (the same count as without --tdd), none of them against a generated member.
        Assert.Equal(13, tests.Count);
        Assert.All(tests, t =>
        {
            Assert.Equal("fail", t.GetProperty("status").GetString());
            Assert.Contains("AL0132", t.GetProperty("message").GetString());
        });
    }
}

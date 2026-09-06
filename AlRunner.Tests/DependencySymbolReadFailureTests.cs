// DependencySymbolReadFailureTests — issue #3143.
//
// THE DEFECT
//   Ten dependency-symbol reads outside the permission slice #3031 fixed each swallowed
//   EVERY exception from BcAppSymbolCache.Get(appPath) and `continue`d, so "the runner could
//   not find out what this .app declares" became "this .app declares nothing". Each fed a
//   different surface, and in every case the caller reads the result as a fact:
//
//     AllObj                        objects absent      -> a table AL enumerates, short rows
//     CodeUnit Metadata             codeunits absent
//     Table Metadata                tables absent       -> "does this table exist" answered no
//     Page Metadata / Page Ctrl Fld pages absent
//     Report Metadata               reports absent
//     All Profile                   profiles absent
//     dependency page metadata      page symbol null    -> InsertAllowed TRUE, SourceTableId 0,
//                                                          PageType null, IsPageShapeKnown FALSE
//     dependency report metadata    report symbol null  -> "report N has no metadata"
//     query symbol index            query absent, and the PARTIAL index then published
//
//   The only trace was a `[RecordPatches]`-tagged stderr line, and Log's default-verbosity
//   filter drops lines that START with a bracketed component tag (measured in #3031 by
//   driving the real filter). At the verbosity users actually run at, the loss was silent.
//
// WHAT THIS FILE PROVES
//   Per surface, three rows:
//     * healthy  — the positive control. Without it every refusal row below could pass
//                  against a pipeline that never reads the .app at all.
//     * poisoned — the read fails AFTER registration; the site must REFUSE, naming the .app
//                  and the surface, instead of answering as though the app declared nothing.
//     * vanished — the .app is gone from disk; the site must SKIP it and keep going, because
//                  that is a legitimate --watch / --server state, and must say so on a
//                  channel Log does not filter.
//
//   Four rows go through the real CALLERS rather than the private walk, because that is
//   where the wrongness was observable: IsPageShapeKnown answered false, GetInsertAllowedForPage
//   answered true, TryGetQuerySymbol answered null and TryBuildDependencyReportMetadata
//   answered null — four confident wrong answers, none distinguishable from a real one.
//
// THE POISON
//   Same technique as PermissionSetSymbolReadFailureTests (#3031) and
//   RecordPatchesBcAppSymbolReadFailureTests (#2712): a SymbolReference.json whose root
//   "AppId" is a JSON NUMBER. BcAppSymbolCache.Parse calls ReadAppIdentity LAST, after every
//   container has already been collected, and ReadAppIdentity's unguarded GetString() throws
//   InvalidOperationException on a non-string. That is a parse that dies part-way AFTER the
//   data the sites want has been read — the reported shape — not a whole-file failure any
//   code path would notice. The poisoned literal is also a different LENGTH, which is what
//   gives the content-hash memo and the symbol cache a new key so the read really re-parses.
//
// No Base Application floor (.claude/rules/no-base-app-in-csharp-tests.md).

using System.Collections;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// RecordPatchesSerialCollection: calls RecordPatches.ResetForReload() directly (#1696).
[Collection(RecordPatchesSerialCollection.Name)]
public sealed class DependencySymbolReadFailureTests : IDisposable
{
    private readonly string _root;

    public DependencySymbolReadFailureTests()
    {
        _root = TestScratch.Dir("al-runner-3143-tests");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private const string AppGuid = "c3143a00-3143-4a31-9a31-000000003143";
    private const string AppName = "Bug3143 Symbol App";

    // Ids process-wide unique among AlRunner.Tests statics (RecordPatches' dependency state is
    // process-global, so an id another fixture also declares could answer from its payload).
    private const int TableId = 88314301;
    private const int CodeunitId = 88314302;
    private const int PageId = 88314303;
    private const int ReportId = 88314304;
    private const int QueryId = 88314305;
    private const string ProfileId = "BUG3143 PROFILE";

    private static string SymbolReference(bool poison)
    {
        // The root "AppId" is the ONLY difference: a string (parseable) or a number, which
        // throws in ReadAppIdentity after every container above has been collected.
        var appId = poison ? "\"AppId\": 3143" : $"\"AppId\": \"{AppGuid}\"";
        return $$"""
            {
              "RuntimeVersion": "15.1",
              {{appId}},
              "Name": "{{AppName}}",
              "Tables": [
                {
                  "Id": {{TableId}},
                  "Name": "Bug3143 Table",
                  "Properties": [ { "Name": "Caption", "Value": "Bug3143 Table" } ],
                  "Fields": [ { "Id": 1, "Name": "Code", "TypeDefinition": { "Name": "Code" } } ]
                }
              ],
              "Codeunits": [
                {
                  "Id": {{CodeunitId}},
                  "Name": "Bug3143 Codeunit",
                  "Properties": [ { "Name": "SingleInstance", "Value": "true" } ]
                }
              ],
              "Pages": [
                {
                  "Id": {{PageId}},
                  "Name": "Bug3143 Page",
                  "Properties": [
                    { "Name": "PageType", "Value": "List" },
                    { "Name": "SourceTable", "Value": "{{TableId}}" },
                    { "Name": "InsertAllowed", "Value": "false" }
                  ]
                }
              ],
              "Reports": [
                { "Id": {{ReportId}}, "Name": "Bug3143 Report", "Properties": [] }
              ],
              "Queries": [
                { "Id": {{QueryId}}, "Name": "Bug3143 Query", "Properties": [] }
              ],
              "Profiles": [
                {
                  "Name": "{{ProfileId}}",
                  "Properties": [ { "Name": "Caption", "Value": "Bug3143 Profile" } ]
                }
              ]
            }
            """;
    }

    private static void WriteApp(string path, string symbolReferenceJson)
    {
        using var zip = new FileStream(path, FileMode.Create);
        using var za = new ZipArchive(zip, ZipArchiveMode.Create);
        var entry = za.CreateEntry("SymbolReference.json");
        using var w = new StreamWriter(entry.Open(), Encoding.UTF8);
        w.Write(symbolReferenceJson);
    }

    /// <summary>Register a healthy .app and leave it healthy.</summary>
    private string RegisterHealthy(string fileName)
    {
        var appPath = Path.Combine(_root, fileName);
        WriteApp(appPath, SymbolReference(poison: false));
        RecordPatches.ResetForReload();
        RecordPatches.AddBcAppPath(appPath);
        return appPath;
    }

    /// <summary>
    /// Register a healthy .app, then rewrite it on disk into the poisoned shape — the
    /// --watch window where a recompile lands after registration and before the lazy read.
    /// A different length plus a bumped mtime give the content-hash memo and the symbol
    /// cache a new key, so the read really re-parses instead of replaying the good result.
    /// </summary>
    private string RegisterThenPoison(string fileName = "dep.app")
    {
        var appPath = RegisterHealthy(fileName);
        WriteApp(appPath, SymbolReference(poison: true));
        File.SetLastWriteTimeUtc(appPath, File.GetLastWriteTimeUtc(appPath).AddSeconds(5));
        return appPath;
    }

    /// <summary>Register a healthy .app, then delete it — the tolerated condition.</summary>
    private void RegisterThenVanish(string fileName = "vanishing.app")
        => File.Delete(RegisterHealthy(fileName));

    // ── reflection into the private walks ────────────────────────────────────────────────
    //
    // Every one of these is a lazy iterator, so a refusal surfaces where the sequence is
    // DRAINED, never where the method is called. Drain() forces that, which is also the
    // property #3133 found the previous shape getting wrong.

    private static MethodInfo Method(string name)
    {
        var m = typeof(RecordPatches).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.True(m != null, $"RecordPatches.{name}() not found — renamed or removed.");
        return m!;
    }

    private static object? Invoke(string name, params object?[] args)
    {
        try { return Method(name).Invoke(null, args.Length == 0 ? null : args); }
        catch (TargetInvocationException tie) { throw tie.InnerException!; }
    }

    private static List<object> Drain(string name)
    {
        var seq = (IEnumerable)Invoke(name)!;
        var list = new List<object>();
        foreach (var item in seq) list.Add(item!);
        return list;
    }

    /// <summary>Every private walk this issue converted, with the surface text it must name.</summary>
    public static TheoryData<string, string> Walks() => new()
    {
        { "EnumerateBcAppObjects",         "objects (AllObj)" },
        { "EnumerateBcAppCodeunitSymbols", "objects (CodeUnit Metadata)" },
        { "EnumerateBcAppTableSymbols",    "tables (Table Metadata)" },
        { "EnumerateBcAppPageSymbols",     "pages (Page Metadata)" },
        { "EnumerateBcAppReportSymbols",   "reports (Report Metadata)" },
        { "EnumerateBcAppProfileSymbols",  "profiles (All Profile)" },
        { "DependencyAppSymbols",          "pages and pageextensions (dependency page metadata)" },
    };

    // ── the positive controls ────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Walks))]
    public void HealthyApp_EveryWalkYieldsItsSymbols(string walk, string _)
    {
        RegisterHealthy("healthy.app");
        Assert.NotEmpty(Drain(walk));
    }

    [Fact]
    public void HealthyApp_TheFourCallersAnswerFromTheDependency()
    {
        RegisterHealthy("healthy-callers.app");

        // Concrete values, not "not null": each is the exact thing the swallow used to
        // replace with a default of the same type.
        Assert.True(RecordPatches.IsPageShapeKnown(PageId));
        Assert.False(RecordPatches.GetInsertAllowedForPage(PageId),
            "the fixture page declares InsertAllowed = false; `true` here is the swallow's default");
        Assert.Equal(TableId, RecordPatches.TryGetDependencySourceTableIdForPage(PageId));
        Assert.Equal("List", RecordPatches.TryGetAnyPageType(PageId));

        var query = RecordPatches.TryGetQuerySymbol(QueryId);
        Assert.NotNull(query);
        Assert.Equal(QueryId, query!.Id);

        Assert.NotNull(RecordPatches.TryBuildDependencyReportMetadata(ReportId));
    }

    [Fact]
    public void HealthyApp_ObjectOwnerIndexStampsTheDeclaringApp()
    {
        RegisterHealthy("healthy-owner.app");
        var index = (IDictionary)Invoke("BuildObjectOwnerIndex")!;
        Assert.Equal(Guid.Parse(AppGuid), index[("codeunit", CodeunitId)]);
    }

    // ── the refusals ─────────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Walks))]
    public void SymbolReadFailsAfterRegistration_EveryWalkRefusesNamingTheAppAndSurface(
        string walk, string surface)
    {
        RegisterThenPoison();

        // Before the fix: an empty (or short) sequence, and a `[RecordPatches]` line Log's
        // default filter dropped. "This app declares nothing" is a WRONG answer here, and
        // nothing downstream could tell it from a real one.
        var ex = Assert.Throws<BcAppSymbolReadException>(() => Drain(walk));
        Assert.Contains("dep.app", ex.Message);
        Assert.Contains(surface, ex.Message);
        // The cause is preserved rather than flattened into a message.
        Assert.IsType<InvalidOperationException>(ex.InnerException);
        // And it is a REFUSAL, not a one-shot complaint: asking again fails the same way
        // rather than serving a partial answer for the rest of the process.
        Assert.Throws<BcAppSymbolReadException>(() => Drain(walk));
    }

    [Fact]
    public void SymbolReadFails_IsPageShapeKnown_RefusesInsteadOfAnsweringFalse()
    {
        RegisterThenPoison();

        // BEFORE: false — "no dependency describes page N". The page is right there in a
        // package the runner could not parse, and every TestPage decision downstream
        // (NavTestPageBase_GetMetaTable's refusal, PrimaryKeyFields) reads that false as fact.
        var ex = Assert.Throws<BcAppSymbolReadException>(() => RecordPatches.IsPageShapeKnown(PageId));
        Assert.Contains("pages and pageextensions", ex.Message);
    }

    [Fact]
    public void SymbolReadFails_GetInsertAllowedForPage_RefusesInsteadOfAnsweringTrue()
    {
        RegisterThenPoison();

        // BEFORE: true — AL's default for an unknown page — while the .app it could not read
        // states InsertAllowed = false. A TestPage would have allowed an insert the real page
        // forbids, and the test would have gone green.
        Assert.Throws<BcAppSymbolReadException>(() => RecordPatches.GetInsertAllowedForPage(PageId));
    }

    [Fact]
    public void SymbolReadFails_TryGetQuerySymbol_RefusesInsteadOfAnsweringNull()
    {
        RegisterThenPoison();

        // BEFORE: null, AND the partial index was published (_bcSymbolQueryIndex = idx), so
        // every later lookup in the process was served from it without re-reading anything.
        var ex = Assert.Throws<BcAppSymbolReadException>(() => RecordPatches.TryGetQuerySymbol(QueryId));
        Assert.Contains("queries (query symbol index)", ex.Message);
        // The publish-a-partial-index half: a second call must not answer from a cached
        // partial result.
        Assert.Throws<BcAppSymbolReadException>(() => RecordPatches.TryGetQuerySymbol(QueryId));
    }

    [Fact]
    public void SymbolReadFails_TryBuildDependencyReportMetadata_RefusesInsteadOfAnsweringNull()
    {
        RegisterThenPoison();

        // BEFORE: null, which the caller turns into "report N has no runtime metadata" — the
        // AL author blamed for a report the runner simply could not read.
        var ex = Assert.Throws<BcAppSymbolReadException>(
            () => RecordPatches.TryBuildDependencyReportMetadata(ReportId));
        Assert.Contains("reports (dependency report metadata)", ex.Message);
    }

    [Fact]
    public void SymbolReadFails_ObjectOwnerIndex_StillRefuses()
    {
        // #3133's refusal, unchanged by #3143's vanished skip in front of it. This row exists
        // so the skip cannot be widened into a swallow without failing.
        RegisterThenPoison();
        var ex = Assert.Throws<RunnerOutOfScopeException>(() => Invoke("BuildObjectOwnerIndex"));
        Assert.Contains("dep.app", ex.Message);
        Assert.Contains("allobj-virtual-table", ex.Message);
    }

    // ── the tolerated condition ──────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Walks))]
    public void VanishedApp_EveryWalkSkipsItRatherThanRefusing(string walk, string _)
    {
        RegisterThenVanish();
        Assert.Empty(Drain(walk));
    }

    [Fact]
    public void VanishedApp_ObjectOwnerIndexSkipsItRatherThanAbortingAllObj()
    {
        // #3143: #3133 refused on ANY exception here, including the file simply being gone —
        // so a --watch iteration that removed a dependency aborted AllObj outright, while
        // EnumerateBcAppObjects (same table, same registry) skipped the same .app and carried
        // on. One table's two walks must not disagree about whether vanished is survivable.
        RegisterThenVanish();
        var index = (IDictionary)Invoke("BuildObjectOwnerIndex")!;
        Assert.False(index.Contains(("codeunit", CodeunitId)));
    }

    [Fact]
    public void VanishedApp_WarningSurvivesLogsDefaultFilter()
    {
        // The skip is only honest if the user is TOLD. This drives Log's REAL filter rather
        // than re-implementing its regex, because the bug being guarded against is precisely
        // a tag the regex eats — `[RecordPatches]` did, which is why the pre-fix line was
        // invisible at default verbosity.
        RegisterThenVanish("vanishing2.app");

        var savedOut = Console.Out;
        var savedErr = Console.Error;
        var savedVerbose = Log.Verbose;
        var captured = new StringWriter();
        string text;
        try
        {
            Console.SetError(captured);
            Console.SetOut(TextWriter.Null);
            Log.Verbose = false;      // the DEFAULT verbosity, where the old line vanished
            Log.Install();            // wrap `captured` in the same FilteredWriter users get
            Drain("EnumerateBcAppObjects");
            Invoke("BuildObjectOwnerIndex");
            Console.Error.Flush();
            text = captured.ToString();
        }
        finally
        {
            Console.SetOut(savedOut);
            Console.SetError(savedErr);
            Log.Verbose = savedVerbose;
        }

        Assert.Contains("vanishing2.app", text);
        Assert.Contains("[warn]", text);
        Assert.Contains("no longer on disk", text);
        // The specific claim: a `[Component]`-tagged line would NOT have survived.
        Assert.DoesNotContain("[RecordPatches]", text);
    }

    [Fact]
    public void RefusalMessage_SurvivesLogsDefaultFilter_UnlikeAComponentTaggedLine()
    {
        // A refusal that is thrown but then filtered out of the terminal is no better than
        // the swallow it replaced, and this repository has shipped that bug repeatedly (see
        // Log.cs's [bc] / [expectations] / [reexec] / [dap] / [warn] notes). So the message
        // goes through the REAL FilteredWriter at default verbosity rather than trusting
        // BcAppSymbolReadException's "no leading [tag]" comment to still be true.
        var refusal = new BcAppSymbolReadException(
            Path.Combine(_root, "dep.app"), "objects (AllObj)", new InvalidOperationException("boom"));

        var savedOut = Console.Out;
        var savedErr = Console.Error;
        var savedVerbose = Log.Verbose;
        var captured = new StringWriter();
        string text;
        try
        {
            Console.SetError(captured);
            Console.SetOut(TextWriter.Null);
            Log.Verbose = false;
            Log.Install();
            Console.Error.WriteLine(refusal.Message);
            // The control: the tag the OLD code used, through the same writer, same call.
            Console.Error.WriteLine("[RecordPatches] AllObj: SymbolReference read failed");
            Console.Error.Flush();
            text = captured.ToString();
        }
        finally
        {
            Console.SetOut(savedOut);
            Console.SetError(savedErr);
            Log.Verbose = savedVerbose;
        }

        Assert.Contains("symbol-read-fail", text);
        Assert.Contains("dep.app", text);
        Assert.Contains("objects (AllObj)", text);
        Assert.DoesNotContain("[RecordPatches]", text);
    }
}

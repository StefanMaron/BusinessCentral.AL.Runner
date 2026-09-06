// PermissionSetSymbolReadFailureTests — issue #3031.
//
// THE DEFECT
//   Two places in the permission slice read a registered dependency .app's
//   SymbolReference.json and swallowed EVERY exception, so "the runner could not find out"
//   became indistinguishable from "this app declares nothing":
//
//     1. RecordPatches.AggregatePermissionSetVirtualTable.BuildKnownAppNameIndex —
//        `catch { continue; }`, fully silent. The app's id never entered the index, so every
//        Aggregate Permission Set (2000000167) row belonging to it reported a BLANK "App
//        Name" rather than refusing.
//     2. RecordPatches.MetadataPermissionSetVirtualTable.EnumerateKnownPermissionSets —
//        `catch (Exception ex)` + a `[RecordPatches]`-tagged stderr line. Every permissionset
//        that app declares vanished from Metadata Permission Set (2000000250) AND from the
//        NavAppGroup inventory built from the same enumeration, so AL asking for one got
//        "does not exist" — a WRONG answer, not a missing one.
//
//   Site 2's warning did not even reach the user: Log's default-verbosity filter drops lines
//   starting with a `[Component]` tag, and `[RecordPatches]` matches it. Measured by
//   VanishedApp_WarningSurvivesLogsDefaultFilter below, which drives the REAL filter rather
//   than re-implementing its regex.
//
// WHICH FAILURES ARE STILL TOLERATED, AND WHY
//   Exactly the distinction #2712 already settled for the table-symbol read in
//   RecordPatches.BcAppFallback.EnsureBcSymbolTableIndex, applied here so the permission
//   slice answers the question the same way the table index does:
//
//     * VANISHED (`!File.Exists`) — a legitimate, expected state: a --watch dependency
//       removed between iterations, a --server process outliving a rebuild, a test fixture's
//       temp dir deleted. Skip the .app as a whole and say so on `[warn]`, which Log exempts.
//     * PRESENT BUT UNREADABLE — never legitimate. Every path in _bcAppPaths already passed
//       AddBcAppPath's eager read (#2712), so a failure here means the bytes changed into
//       something unparseable, or the parser has a bug. Both are runner defects that would
//       otherwise be reported as ordinary-looking permission results, so they propagate as
//       BcAppSymbolReadException and abort the run.
//
//   The check is a File.Exists precondition rather than an exception filter on purpose: a
//   deleted file surfaces as FileNotFoundException, DirectoryNotFoundException or IOException
//   depending on platform and timing, so filtering on type would classify the expected state
//   by accident. The narrow TOCTOU window (file deleted between the check and the read) fails
//   loudly, which is the conservative direction.
//
// THE POISON
//   A SymbolReference.json whose root "AppId" is a JSON NUMBER. BcAppSymbolCache.Parse calls
//   ReadAppIdentity LAST, after CollectPermissionSets has already collected the app's
//   permission sets, and ReadAppIdentity's unguarded GetString() throws
//   InvalidOperationException on a non-string. That reproduces the reported shape exactly —
//   a parse that fails part-way, after the data the two sites want has been read — rather
//   than a whole-file failure that any code path would notice.
//
// Same synthetic-.app + rewrite-after-registration technique as
// RecordPatchesBcAppSymbolReadFailureTests (#2712). No Base Application floor
// (.claude/rules/no-base-app-in-csharp-tests.md).

using System.IO.Compression;
using System.Reflection;
using System.Text;
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// RecordPatchesSerialCollection: calls RecordPatches.ResetForReload() directly (#1696).
[Collection(RecordPatchesSerialCollection.Name)]
public sealed class PermissionSetSymbolReadFailureTests : IDisposable
{
    private readonly string _root;

    public PermissionSetSymbolReadFailureTests()
    {
        _root = TestScratch.Dir("al-runner-3031-tests");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static void WriteApp(string path, string symbolReferenceJson)
    {
        using var zip = new FileStream(path, FileMode.Create);
        using var za = new ZipArchive(zip, ZipArchiveMode.Create);
        var entry = za.CreateEntry("SymbolReference.json");
        using var w = new StreamWriter(entry.Open(), Encoding.UTF8);
        w.Write(symbolReferenceJson);
    }

    private const string AppGuid = "b1b0d3e4-3031-4a31-9a31-000000003031";

    // Object ids process-wide unique among AlRunner.Tests statics: 939xx is taken by the
    // #2712 / warm-reload / eviction tests, so this file uses 94100-94102.
    private static string SymbolReference(int permissionSetId, string roleId, bool poison)
    {
        // The root "AppId" is the ONLY difference: a string (parseable) or a number (throws in
        // ReadAppIdentity, after CollectPermissionSets has already run).
        var appId = poison ? $"\"AppId\": 30031" : $"\"AppId\": \"{AppGuid}\"";
        return $$"""
            {
              "RuntimeVersion": "15.1",
              {{appId}},
              "Name": "Bug3031 Permission App",
              "Namespaces": [],
              "PermissionSets": [
                {
                  "Id": {{permissionSetId}},
                  "Name": "{{roleId}}",
                  "Properties": [ { "Name": "Caption", "Value": "Bug3031 Set" } ],
                  "Permissions": []
                }
              ]
            }
            """;
    }

    private static object InvokeBuildKnownAppNameIndex()
    {
        var m = typeof(RecordPatches).GetMethod(
            "BuildKnownAppNameIndex", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.True(m != null,
            "RecordPatches.BuildKnownAppNameIndex() not found — the method was renamed or removed.");
        try { return m!.Invoke(null, null)!; }
        catch (TargetInvocationException tie) { throw tie.InnerException!; }
    }

    // Fully drains the iterator, which is where a lazily-thrown failure actually surfaces.
    private static List<object> DrainKnownPermissionSets()
    {
        var m = typeof(RecordPatches).GetMethod(
            "EnumerateKnownPermissionSets", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.True(m != null,
            "RecordPatches.EnumerateKnownPermissionSets() not found — the method was renamed or removed.");
        System.Collections.IEnumerable seq;
        try { seq = (System.Collections.IEnumerable)m!.Invoke(null, null)!; }
        catch (TargetInvocationException tie) { throw tie.InnerException!; }
        var list = new List<object>();
        foreach (var item in seq) list.Add(item!);
        return list;
    }

    /// <summary>Role ids the drained tuples carry, via the tuple's PermissionSet field.</summary>
    private static List<string> RoleIdsOf(List<object> drained)
    {
        var names = new List<string>();
        foreach (var t in drained)
        {
            var permissionSet = t.GetType().GetField("Item1")!.GetValue(t)!;
            names.Add((string)permissionSet.GetType().GetProperty("Name")!.GetValue(permissionSet)!);
        }
        return names;
    }

    /// <summary>
    /// Register a healthy .app, then rewrite it on disk into the poisoned shape — the --watch
    /// window where a recompile lands after registration and before the lazy read. A different
    /// length + a bumped mtime give the content-hash memo and the symbol cache a new key, so
    /// the read really re-parses instead of serving the earlier good result.
    /// </summary>
    private string RegisterThenPoison(int permissionSetId, string roleId)
    {
        var appPath = Path.Combine(_root, "perm.app");
        WriteApp(appPath, SymbolReference(permissionSetId, roleId, poison: false));
        RecordPatches.ResetForReload();
        RecordPatches.AddBcAppPath(appPath);
        WriteApp(appPath, SymbolReference(permissionSetId, roleId, poison: true));
        File.SetLastWriteTimeUtc(appPath, File.GetLastWriteTimeUtc(appPath).AddSeconds(5));
        return appPath;
    }

    [Fact]
    public void BuildKnownAppNameIndex_HealthyApp_IndexesTheAppName()
    {
        // Positive control: without this, every assertion below could pass against a pipeline
        // that never reads the .app at all.
        var appPath = Path.Combine(_root, "healthy.app");
        WriteApp(appPath, SymbolReference(94100, "BUG3031 HEALTHY", poison: false));
        RecordPatches.ResetForReload();
        RecordPatches.AddBcAppPath(appPath);

        var index = (System.Collections.IDictionary)InvokeBuildKnownAppNameIndex();
        Assert.Equal("Bug3031 Permission App", index[Guid.Parse(AppGuid)]);
    }

    [Fact]
    public void EnumerateKnownPermissionSets_HealthyApp_YieldsItsPermissionSet()
    {
        var appPath = Path.Combine(_root, "healthy2.app");
        WriteApp(appPath, SymbolReference(94101, "BUG3031 HEALTHY2", poison: false));
        RecordPatches.ResetForReload();
        RecordPatches.AddBcAppPath(appPath);

        Assert.Contains("BUG3031 HEALTHY2", RoleIdsOf(DrainKnownPermissionSets()));
    }

    [Fact]
    public void BuildKnownAppNameIndex_SymbolReadFailsAfterRegistration_ThrowsNamingTheAppAndSurface()
    {
        RegisterThenPoison(94102, "BUG3031 SILENT");

        // Before the fix: `catch { continue; }` returned an index WITHOUT this app, so the
        // App Name column answered "" — a blank that reads as "this app has no name".
        var ex = Assert.Throws<BcAppSymbolReadException>(() => InvokeBuildKnownAppNameIndex());
        Assert.Contains("perm.app", ex.Message);
        Assert.Contains("app identity", ex.Message);
        // The inner cause is preserved, not flattened into a message.
        Assert.IsType<InvalidOperationException>(ex.InnerException);
        // And it is a REFUSAL, not a re-attempt that eventually gives up quietly: asking
        // again fails the same loud way rather than returning a partial index.
        Assert.Throws<BcAppSymbolReadException>(() => InvokeBuildKnownAppNameIndex());
    }

    [Fact]
    public void EnumerateKnownPermissionSets_SymbolReadFailsAfterRegistration_ThrowsInsteadOfDroppingTheSets()
    {
        RegisterThenPoison(94102, "BUG3031 DROPPED");

        // Before the fix: the app's permission sets silently disappeared from Metadata
        // Permission Set and from the NavAppGroup inventory, and the only trace was a
        // `[RecordPatches]` line Log's default filter dropped.
        var ex = Assert.Throws<BcAppSymbolReadException>(() => DrainKnownPermissionSets());
        Assert.Contains("perm.app", ex.Message);
        Assert.Contains("permission sets", ex.Message);
        Assert.IsType<InvalidOperationException>(ex.InnerException);
    }

    [Fact]
    public void VanishedApp_IsSkippedRatherThanRefused()
    {
        // The one condition that is NOT a runner defect: the .app is simply gone. Both sites
        // skip it and keep going, so a --watch iteration that removed a dependency still runs.
        var appPath = Path.Combine(_root, "vanishing.app");
        WriteApp(appPath, SymbolReference(94103, "BUG3031 VANISH", poison: false));
        RecordPatches.ResetForReload();
        RecordPatches.AddBcAppPath(appPath);
        File.Delete(appPath);

        var index = (System.Collections.IDictionary)InvokeBuildKnownAppNameIndex();
        Assert.False(index.Contains(Guid.Parse(AppGuid)));
        Assert.DoesNotContain("BUG3031 VANISH", RoleIdsOf(DrainKnownPermissionSets()));
    }

    [Fact]
    public void VanishedApp_WarningSurvivesLogsDefaultFilter()
    {
        // The skip above is only honest if the user is TOLD. This drives Log's REAL filter
        // (Log.Install wraps whatever Console.Error currently is) rather than re-implementing
        // its regex, because the bug being guarded against is precisely a tag that the regex
        // eats — `[RecordPatches]` did, which is why the pre-fix warning was invisible.
        var appPath = Path.Combine(_root, "vanishing2.app");
        WriteApp(appPath, SymbolReference(94104, "BUG3031 VANISH2", poison: false));
        RecordPatches.ResetForReload();
        RecordPatches.AddBcAppPath(appPath);
        File.Delete(appPath);

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
            InvokeBuildKnownAppNameIndex();
            DrainKnownPermissionSets();
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
        // The specific claim: a `[Component]`-tagged line would NOT have survived. Proven by
        // pushing one through the very same writer and finding it absent.
        Console.Error.Flush();
        Assert.DoesNotContain("[RecordPatches]", text);
    }

    [Fact]
    public void RefusalMessage_SurvivesLogsDefaultFilter_UnlikeAComponentTaggedLine()
    {
        // The other half of "the loud failure actually reaches the user". A refusal that is
        // thrown but then filtered out of the terminal is no better than the swallow it
        // replaced, and this repository has shipped that bug at least five times (see Log.cs's
        // [bc] / [expectations] / [reexec] / [dap] / [warn] notes). So the message is pushed
        // through the REAL FilteredWriter at DEFAULT verbosity rather than trusting that
        // BcAppSymbolReadException's "no leading [tag]" comment is still true.
        var refusal = new BcAppSymbolReadException(
            Path.Combine(_root, "perm.app"), "permission sets", new InvalidOperationException("boom"));

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
            Console.Error.WriteLine("[RecordPatches] Metadata Permission Set: SymbolReference read failed");
            Console.Error.Flush();
            text = captured.ToString();
        }
        finally
        {
            Console.SetOut(savedOut);
            Console.SetError(savedErr);
            Log.Verbose = savedVerbose;
        }

        // The refusal reaches the terminal, naming the .app and the surface ...
        Assert.Contains("symbol-read-fail", text);
        Assert.Contains("perm.app", text);
        Assert.Contains("permission sets", text);
        // ... and the old line, written through the very same writer, does not.
        Assert.DoesNotContain("[RecordPatches]", text);
    }
}

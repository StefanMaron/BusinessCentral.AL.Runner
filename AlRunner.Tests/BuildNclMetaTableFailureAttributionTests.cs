// BuildNclMetaTableFailureAttributionTests — issue #3590.
//
// WHAT IS BEING PROVED
//   RecordPatches.BuildNCLMetaTable wrapped its whole body in `catch (Exception) { ...; return
//   null; }`, and its caller is `_metaTableCache.GetOrAdd(tableId, BuildNCLMetaTable)`. So a
//   failure to CONSTRUCT a table's metadata became the same answer as "this table does not
//   exist", the null was CACHED, and every later consumer took its own not-found branch:
//   NavRecordHandle_CreateTarget's not-found path, TryBuildBlankRecord's `why` string,
//   GetMetaFieldEditable answering `true`. None of them names the table or the reason.
//
// THE DECISION THIS FILE PINS, AND WHY IT IS NOT "RETHROW EVERYTHING"
//   `.claude/rules/guards-need-a-third-state.md` states the constraint that decides this: a
//   genuinely ABSENT thing must stay a pass; only an UNMEASURABLE one becomes the third state.
//   A blanket rethrow would trade a false green for a false red, because every caller listed
//   above legitimately asks about tables that cannot exist.
//
//   The method's own structure already draws that line, and it draws it OUTSIDE the try:
//
//     _parsedTables miss + TryPopulateParsedTableFromBcApps miss   -> return null   (absent)
//     _tMetaTable / _mCreateFromMetaTable not resolved             -> return null   (absent)
//     ------------------------------- try starts here -------------------------------
//     anything that throws while BUILDING a table we know exists   -> was ALSO null (the bug)
//
//   So the catch never sees the absent case at all. Everything reaching it is a failure to
//   measure something the runner had already established was there — row 3, spelled as row 1.
//
// WHY A FILTER AND NOT A BARE RETHROW OF EVERYTHING
//   Two refusal types are raised deliberately from inside that try and are the ones whose whole
//   purpose is to name what could not be read: BcShapeGapException (a BC member moved) and
//   BcAppSymbolReadException (a dependency's symbols could not be read to completion). Both
//   tear through AL's two trapping seams BY CONTRACT — see BcShapeGapException.cs's table —
//   and this catch was a THIRD seam nobody enumerated, which turned them back into a null.
//
//   The same file already settled this exact shape one method over: WireFieldTriggerHandlers'
//   catch carries a `when (BcShapeGapException.Find(ex) is null && ...)` filter for #3026 and
//   #3048, with the reasoning that without it "this catch converts every refusal raised above
//   into a stderr line plus the same not-installed outcome the refusals exist to stop". This is
//   that precedent applied to its neighbour, not a new mechanism.
//
// THE NEGATIVE CONTROLS ARE THE POINT, NOT DECORATION
//   "A construction failure is attributable" is satisfiable by a change that rethrows
//   everything, which is the false-red trap above. Section 2 pins what must STILL be swallowed
//   into a null, and section 3 pins that the two absent paths never reach the catch at all.
//
// WHY RUNNER-LOCAL AND NOT THE CORPUS
//   No AL statement can make a BC member move or corrupt a .app's symbols, so a service tier
//   has nothing to adjudicate: the subject is which exceptions the runner's own catch may
//   absorb. Same reasoning as PageControlFieldEditableShapeGapTests and BcShapeGapConventionTests.
using System;
using System.Linq;
using System.Reflection;
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

public sealed class BuildNclMetaTableFailureAttributionTests
{
    // The classification the catch applies, driven directly. Returning false means "this catch
    // may not absorb it" — the exception carries on to the caller.
    private static bool MayAbsorb(Exception ex)
    {
        var m = typeof(RecordPatches).GetMethod(
            "BuildNclMetaTableCatchMayAbsorb",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(m);
        return (bool)m!.Invoke(null, new object?[] { ex })!;
    }

    // ══ 1. WHAT MUST TEAR THROUGH ════════════════════════════════════════════════════════
    //
    // Each arm names one refusal type whose entire purpose is to say what could not be read.
    // Absorbing one here re-creates the silent default it was raised to replace.

    [Fact]
    public void AShapeGap_IsNotAbsorbed_SoTheMemberThatMovedReachesTheDeveloper()
    {
        var gap = new BcShapeGapException(
            "Table metadata (table 50100)", "MetaTable.Fields", "field not found — BC moved it");

        Assert.False(MayAbsorb(gap));
    }

    [Fact]
    public void AShapeGapArrivingWrapped_IsNotAbsorbed_BecauseTheBuilderCallsBcThroughReflection()
    {
        // Every construction step in the builder goes through MethodBase.Invoke or a reflective
        // ctor, so a refusal raised underneath arrives inside a TargetInvocationException. An
        // `is` test would miss it — the same reason BcShapeGapException.Find is a chain walk.
        var wrapped = new TargetInvocationException(
            new BcShapeGapException("Table metadata (table 50100)", "MetaTable.Fields", "moved"));

        Assert.False(MayAbsorb(wrapped));
    }

    [Fact]
    public void ASymbolReadRefusal_IsNotAbsorbed_BecauseACorruptDependencyIsNotAMissingTable()
    {
        var read = new BcAppSymbolReadException(
            "/tmp/Some.app", "table symbols", new OutOfMemoryException("truncated"));

        Assert.False(MayAbsorb(read));
    }

    [Fact]
    public void ASymbolReadRefusalArrivingWrapped_IsNotAbsorbed_ForTheSameReason()
    {
        var wrapped = new TargetInvocationException(
            new BcAppSymbolReadException("/tmp/Some.app", "table extensions",
                new OutOfMemoryException("truncated")));

        Assert.False(MayAbsorb(wrapped));
    }

    [Fact]
    public void ATypedOutOfScopeRefusal_IsNotAbsorbed_MatchingTheNeighbouringCatchsContract()
    {
        // WireFieldTriggerHandlers' catch in this same file was widened to the TYPED
        // RunnerOutOfScopeException by #3048, on the argument that swallowing one "would restore
        // precisely the silent skip they replace". The same argument applies here.
        var oos = new RunnerOutOfScopeException(
            "NCLMetaTable construction", "not-yet-implemented — see docs/scope.md");

        Assert.False(MayAbsorb(oos));
    }

    // ══ 2. WHAT MUST STILL BE ABSORBED ═══════════════════════════════════════════════════
    //
    // Without these, the change is a blanket rethrow: every caller that legitimately asks about
    // a table that cannot exist would start failing the run instead of taking its not-found
    // branch. That is the false-red half of guards-need-a-third-state.md's constraint.

    [Fact]
    public void AnOrdinaryConstructionFailure_IsStillAbsorbed_SoNoCallerLosesItsNotFoundBranch()
    {
        Assert.True(MayAbsorb(new InvalidOperationException("ctor arity changed")));
        Assert.True(MayAbsorb(new NullReferenceException()));
        Assert.True(MayAbsorb(new TargetInvocationException(new ArgumentException("bad arg"))));
    }

    [Fact]
    public void AnUntypedOutOfScopeLookalike_IsStillAbsorbed_BecauseItIsNotOneOfOurs()
    {
        // A BC exception whose MESSAGE merely happens to carry the out-of-scope convention is
        // not a runner refusal. #3048 drew this line explicitly for the neighbouring catch:
        // "Typed only".
        var lookalike = new InvalidOperationException(
            OutOfScopeMessage.Prefix + "something that only looks like ours");

        Assert.True(MayAbsorb(lookalike));
    }

    // ══ 3. THE ABSENT CASES NEVER REACH THE CATCH ════════════════════════════════════════
    //
    // The claim that makes section 1 safe. If either early return sat inside the try, section
    // 1 would be converting "no such table" into a hard failure.

    [Fact]
    public void TheTwoAbsentReturns_SitOutsideTheTry_SoNoMissingTableCanReachTheFilter()
    {
        var body = typeof(RecordPatches)
            .GetMethod("BuildNCLMetaTable", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetMethodBody()!;

        // An exception handler's try block is a byte range in the method body. Both early
        // returns must lie before every handler's try offset.
        var firstTryOffset = body.ExceptionHandlingClauses
            .Select(c => c.TryOffset)
            .DefaultIfEmpty(int.MaxValue)
            .Min();

        Assert.NotEqual(int.MaxValue, firstTryOffset);

        // The IL before the first try must contain the two early-return paths, i.e. at least
        // one `ret` — a method whose try starts at offset 0 has folded them in.
        var il = body.GetILAsByteArray()!;
        Assert.True(firstTryOffset > 0,
            "BuildNCLMetaTable's try must start after the absent-table early returns; a try at "
            + "offset 0 means a missing table now reaches the catch filter.");
        Assert.Contains((byte)0x2A /* ret */, il.Take(firstTryOffset).ToArray());
    }

    // ══ 4. THE FAILURE IS VISIBLE AT DEFAULT VERBOSITY ═══════════════════════════════════
    //
    // The second half of #3590. The old write was `[RecordPatches] ...`, and Log.FilteredWriter
    // drops a line starting with a bracketed component tag unless --verbose. An invisible
    // diagnostic is the same defect in a new place, so the message the developer is meant to
    // read must not start with one. BcAppSymbolReadException.BuildMessage carries the same
    // constraint as a comment for the same reason.

    [Fact]
    public void TheRethrownRefusalsMessage_DoesNotStartWithAComponentTag_SoTheFilterCannotDropIt()
    {
        var gap = new BcShapeGapException(
            "Table metadata (table 50100)", "MetaTable.Fields", "field not found");

        Assert.False(gap.Message.StartsWith("[", StringComparison.Ordinal));
        Assert.StartsWith(BcShapeGapException.Prefix, gap.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAbsorbedFailuresLogLine_NamesTheTableAndTheCause_AndSurvivesTheDefaultFilter()
    {
        var m = typeof(RecordPatches).GetMethod(
            "BuildNclMetaTableFailureLine", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(m);

        var line = (string)m!.Invoke(null, new object?[]
        {
            50100, new InvalidOperationException("ctor arity changed"),
        })!;

        // Names the table, the exception type and the message — the three things the old
        // several-layers-later NRE could not name.
        Assert.Contains("50100", line, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), line, StringComparison.Ordinal);
        Assert.Contains("ctor arity changed", line, StringComparison.Ordinal);

        // And it is not dropped at default verbosity.
        Assert.False(line.StartsWith("[", StringComparison.Ordinal));
    }

    [Fact]
    public void TheFailureLine_UnwrapsAReflectionWrapper_SoTheCauseIsNamedRatherThanTheWrapper()
    {
        var m = typeof(RecordPatches).GetMethod(
            "BuildNclMetaTableFailureLine", BindingFlags.NonPublic | BindingFlags.Static)!;

        var line = (string)m.Invoke(null, new object?[]
        {
            50100, new TargetInvocationException(new ArgumentException("bad field id")),
        })!;

        Assert.Contains(nameof(ArgumentException), line, StringComparison.Ordinal);
        Assert.Contains("bad field id", line, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(TargetInvocationException), line, StringComparison.Ordinal);
    }

    // ══ 5. THE REFLECTION PIN: name, arity, uniqueness ═══════════════════════════════════
    //
    // ReflectionDrivenHelperLivenessTests measures liveness from IL for every RecordPatches
    // member a test names as a literal. Pinned here is the half that guard cannot see.

    [Fact]
    public void BothHelpersAreUniqueByName_AndTakeWhatTheseArmsDriveThemWith()
    {
        var declared = typeof(RecordPatches)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .ToList();

        var filter = declared.Where(x => x.Name == "BuildNclMetaTableCatchMayAbsorb").ToList();
        Assert.Single(filter);
        Assert.Equal(new[] { typeof(Exception) },
            filter[0].GetParameters().Select(p => p.ParameterType).ToArray());
        Assert.Equal(typeof(bool), filter[0].ReturnType);

        var line = declared.Where(x => x.Name == "BuildNclMetaTableFailureLine").ToList();
        Assert.Single(line);
        Assert.Equal(new[] { typeof(int), typeof(Exception) },
            line[0].GetParameters().Select(p => p.ParameterType).ToArray());
        Assert.Equal(typeof(string), line[0].ReturnType);
    }
}

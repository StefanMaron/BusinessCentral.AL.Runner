// PermissionMetadataMethodOverloadGuardTests — the wrong-TYPE half of the AssertError inversion (#3062).
//
// ── THE SECOND ROUTE INTO THE SAME INVERSION ─────────────────────────────────────────────
// #3046 / PR #3053 closed one way for a BC-internals read to reach AL as "no error at all": a
// lookup guarded only by `!` hands back a silent null, and the NullReferenceException at the
// first USE of it is swallowed by MethodScopePatches.NavMethodScope_AssertError, so AL's
// `asserterror` PASSES on a read real BC performs fine.
//
// A null is not the only way in. NavMethodScope_AssertError rethrows exactly one type
// (BcShapeGapException) and absorbs everything else, so a THROW OF THE WRONG TYPE inverts the
// result the same way — and no `!` appears in it, so #3051's sweep of null-forgiving lookups
// cannot find these by construction.
//
// ── THE SITE, AND WHY IT IS THE SHARPEST ONE ─────────────────────────────────────────────
// RecordPatches.PermissionMetadataPopulator.RequireBcMethod was ADDED by #3053, in the helper
// written to fix the first kind of inversion, and it resolved by Type.GetMethod(string). That
// overload throws AmbiguousMatchException the moment Microsoft ships a second method of the
// same name — which is precisely the BC-layout change the helper exists to NAME. Absorbed by
// the seam, it becomes a green asserterror over a population that silently did not happen.
//
// ── WHICH SEAM ACTUALLY INVERTS ──────────────────────────────────────────────────────────
// Only asserterror. NavApplicationObjectBase_TryInvoke rethrows anything that is not a
// trappable NavBaseException or a permanently out-of-scope refusal, so an AmbiguousMatchException
// already reached AL as a failure there. The TryFunction arm below is therefore a CONTROL —
// it pins that the corrected refusal still tears through — and not a second RED.
//
// ── MEASURED RED (pre-fix production file, this test file unchanged) ─────────────────────
//     AssertError_TearsThrough_InsteadOfSwallowingTheAmbiguousMatch
//         Assert.Throws() Failure: No exception was thrown
// That message IS the inversion: the seam swallowed the AmbiguousMatchException and the
// asserterror passed. Post-fix the overload resolves, the population runs on, and the only
// refusal that reaches AL is a named BcShapeGapException.
//
// ── WHAT THE FIX DOES ────────────────────────────────────────────────────────────────────
// RequireBcMethod enumerates candidates (which cannot throw) instead of calling
// GetMethod(string), and takes the signature the call site's Invoke argument array is built
// for. Three of the file's four method lookups can state that signature from something already
// in hand — the lookup dictionary's own generic arguments, the summary type, the instance being
// passed — so an added overload is RESOLVED. The fourth would have to resolve two more BC types
// by name just to state it, so it stays unique-by-name, where a second declaration is refused
// BY NAME rather than arriving as an anonymous AmbiguousMatchException.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

[Collection(PermissionMetadataStaticsSerialCollection.Name)]
public sealed class PermissionMetadataMethodOverloadGuardTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private const BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Static;

    // ══ 1. THE INVERSION, on the real call path ═════════════════════════════════════════
    //
    // InstallFreshPermissionSetLookup with a lookup whose dictionary type declares a second
    // Add — the shape a Microsoft overload produces. Both arms drive PRODUCTION code through
    // the AL seams; nothing here is a re-implementation of the helper.

    [Fact]
    public void AssertError_TearsThrough_InsteadOfSwallowingTheAmbiguousMatch()
    {
        var ex = Assert.Throws<BcShapeGapException>(() => BcRuntime.NavMethodScope_AssertError(
            null!, () => InstallLookupOverAnOverloadedDictionary()));

        // Getting as far as the LazyEx constructor is the proof that the overloaded Add was
        // RESOLVED rather than choked on: it is the next refusal after the Add lookup, and the
        // fake Lazy is what cannot satisfy it. Pre-fix this arm never reached here — the
        // AmbiguousMatchException was raised two steps earlier and the seam ate it.
        Assert.Equal("Permission metadata (NavAppGroup permission-set inventory)", ex.Surface);
        Assert.Equal("LazyEx<T>(Func<T>)", ex.Member);
    }

    // CONTROL, not a second RED: TryInvoke rethrew the AmbiguousMatchException before this
    // change too. What it pins is that the corrected path still ends in a refusal AL cannot
    // trap, rather than in a silent success.
    [Fact]
    public void TryFunction_StillCannotTrapTheRefusal_OnTheCorrectedPath()
    {
        var ex = Assert.Throws<BcShapeGapException>(() => BcRuntime.NavApplicationObjectBase_TryInvoke(
            null, () => InstallLookupOverAnOverloadedDictionary()));

        Assert.Equal("LazyEx<T>(Func<T>)", ex.Member);
    }

    // CONTROL: with a single-Add dictionary the same call reaches the same refusal, so the arms
    // above are about the overload being resolved and not about the injection itself failing.
    [Fact]
    public void TheSamePath_ReachesTheSameRefusal_WithASingleAddDictionary()
    {
        var ex = Assert.Throws<BcShapeGapException>(() => WithInjectedStatics(
            typeof(FakeAppGroup).GetField(nameof(FakeAppGroup.PlainLookup))!,
            typeof(FakeSummary),
            () => Invoke("InstallFreshPermissionSetLookup", new object(), new List<object>())));

        Assert.Equal("LazyEx<T>(Func<T>)", ex.Member);
    }

    private static void InstallLookupOverAnOverloadedDictionary() => WithInjectedStatics(
        typeof(FakeAppGroup).GetField(nameof(FakeAppGroup.OverloadedLookup))!,
        typeof(FakeSummary),
        () => Invoke("InstallFreshPermissionSetLookup", new object(), new List<object>()));

    // ══ 2. RequireBcMethod resolves the SIGNATURE, not just the name ════════════════════

    [Fact]
    public void RequireBcMethod_ResolvesTheDeclaredSignature_WhenBcAlsoShipsAnOverload()
    {
        var m = (MethodInfo)Invoke("RequireBcMethod",
            typeof(OverloadedAddDictionary<string, object>), "Add",
            new[] { typeof(string), typeof(object) }, null)!;

        Assert.Equal(new[] { typeof(string), typeof(object) },
            m.GetParameters().Select(p => p.ParameterType).ToArray());
    }

    // The selection DISCRIMINATES: asked for the three-parameter overload it returns that one,
    // so the arm above is not satisfied by a helper that always hands back the first candidate.
    [Fact]
    public void RequireBcMethod_ReturnsTheOtherOverload_WhenThatIsTheSignatureAskedFor()
    {
        var m = (MethodInfo)Invoke("RequireBcMethod",
            typeof(OverloadedAddDictionary<string, object>), "Add",
            new[] { typeof(string), typeof(object), typeof(bool) }, null)!;

        Assert.Equal(3, m.GetParameters().Length);
        Assert.Equal(typeof(bool), m.GetParameters()[2].ParameterType);
    }

    [Fact]
    public void RequireBcMethod_RaisesAShapeGapNamingTheSignature_WhenTheParametersHaveMoved()
    {
        var ex = Assert.Throws<BcShapeGapException>(() => Invoke("RequireBcMethod",
            typeof(OverloadedAddDictionary<string, object>), "Add",
            new[] { typeof(int) }, null));

        Assert.Equal("OverloadedAddDictionary`2.Add", ex.Member);
        Assert.Contains("method not found with signature (Int32)", ex.Detail, StringComparison.Ordinal);
    }

    // The unique-by-name path (CreateEmptyNCLMetaPermissionSet's): a second declaration is
    // refused BY NAME. Pre-fix this was an AmbiguousMatchException, which asserterror absorbed.
    [Fact]
    public void RequireBcMethod_RaisesAShapeGap_WhenNoSignatureIsGivenAndBcDeclaresTwo()
    {
        var ex = Assert.Throws<BcShapeGapException>(() => Invoke("RequireBcMethod",
            typeof(OverloadedAddDictionary<string, object>), "Add", null, null));

        Assert.Equal("OverloadedAddDictionary`2.Add", ex.Member);
        Assert.Contains("BC declares 2 methods named Add", ex.Detail, StringComparison.Ordinal);
        Assert.Contains("cannot tell which one", ex.Detail, StringComparison.Ordinal);
    }

    // CONTROL: unique by name still resolves with no signature given, so the refusal above is
    // about the second declaration and not about the null signature.
    [Fact]
    public void RequireBcMethod_StillResolvesByNameAlone_WhenBcDeclaresExactlyOne()
    {
        var m = (MethodInfo)Invoke("RequireBcMethod",
            typeof(PlainAddDictionary<string, object>), "Add", null, null)!;

        Assert.Equal(2, m.GetParameters().Length);
    }

    // A non-public method resolves: two of this file's four lookups are NonPublic on BC's own
    // types, so a helper narrower than they are would refuse a build that works.
    [Fact]
    public void RequireBcMethod_FindsANonPublicMethod_SoTheFourCallSitesAgree()
    {
        var m = (MethodInfo)Invoke("RequireBcMethod",
            typeof(PlainAddDictionary<string, object>), "Hidden", null, null)!;

        Assert.Equal("Hidden", m.Name);
    }

    // The custom member name the Compare call site passes survives, so the refusal still says
    // NavAppGroup.GroupSummaryComparer.Compare rather than the bare nested type's name.
    [Fact]
    public void RequireBcMethod_UsesTheCallSitesMemberName_WhenOneIsGiven()
    {
        var ex = Assert.Throws<BcShapeGapException>(() => Invoke("RequireBcMethod",
            typeof(PlainAddDictionary<string, object>), "Compare", null,
            "NavAppGroup.GroupSummaryComparer.Compare"));

        Assert.Equal("NavAppGroup.GroupSummaryComparer.Compare", ex.Member);
    }

    // ══ 3. RequireBcLookupKeyValueTypes ═════════════════════════════════════════════════

    [Fact]
    public void RequireBcLookupKeyValueTypes_ReturnsTheDictionarysOwnKeyAndValueTypes()
    {
        var kv = (Type[])Invoke("RequireBcLookupKeyValueTypes",
            typeof(Dictionary<string, int>))!;

        Assert.Equal(new[] { typeof(string), typeof(int) }, kv);
    }

    [Fact]
    public void RequireBcLookupKeyValueTypes_RaisesAShapeGap_WhenTheLookupIsNotATwoArgumentGeneric()
    {
        var ex = Assert.Throws<BcShapeGapException>(
            () => Invoke("RequireBcLookupKeyValueTypes", typeof(List<string>)));

        Assert.Equal("NavAppGroup.permissionSetLookup", ex.Member);
        Assert.Contains("two-argument Dictionary", ex.Detail, StringComparison.Ordinal);
    }

    // ══ 4. The shape, not just the one line the issue named ═════════════════════════════
    //
    // The two sibling files in the same slice already pin every signature they look up
    // (NavGuid.Create(Guid), AggregatePermissionSetDataProvider.GetSystemPermissionSets(NavValue,
    // NavCode), …) — the populator's helper was the outlier. This keeps the next edit from
    // reintroducing a name-only GetMethod anywhere in the slice.

    [Fact]
    public void NoMethodLookupInTheSlice_ResolvesByNameWithoutASignature()
    {
        var offenders = new List<string>();

        foreach (var file in SliceFiles)
        {
            var path = Path.Combine(RepoRoot, "AlRunner", "Patches", file);
            Assert.True(File.Exists(path), $"{file} not found at {path} — it was renamed or moved.");

            // Comments stripped first: this file's own siblings quote the retired shape in prose.
            var code = string.Join("\n", File.ReadAllLines(path).Select(StripLineComment));

            foreach (var statement in code.Split(';'))
            {
                var flat = string.Join(" ", statement.Split('\n').Select(l => l.Trim()));
                if (!flat.Contains(".GetMethod(", StringComparison.Ordinal)) continue;
                if (flat.Contains("types:", StringComparison.Ordinal)) continue;
                offenders.Add($"{file}: {flat.Trim()}");
            }
        }

        Assert.True(offenders.Count == 0,
            "these method lookups resolve by name alone, so an overload Microsoft ships raises "
            + "AmbiguousMatchException into NavMethodScope_AssertError instead of naming the gap "
            + $"(#3062):{Environment.NewLine}" + string.Join(Environment.NewLine, offenders));
    }

    private static string StripLineComment(string line)
    {
        var at = line.IndexOf("//", StringComparison.Ordinal);
        return at < 0 ? line : line.Substring(0, at);
    }

    private static readonly string[] SliceFiles =
    {
        "RecordPatches.AggregatePermissionSetVirtualTable.cs",
        "RecordPatches.MetadataPermissionSetVirtualTable.cs",
        "RecordPatches.PermissionMetadataPopulator.cs",
    };

    // ══ Plumbing ════════════════════════════════════════════════════════════════════════

    private static void WithInjectedStatics(FieldInfo lookupField, Type summaryType, Action body)
    {
        var fLookup = Static("_fPermissionSetLookup");
        var fSummary = Static("_tSummary");
        var savedLookup = fLookup.GetValue(null);
        var savedSummary = fSummary.GetValue(null);
        try
        {
            fLookup.SetValue(null, lookupField);
            fSummary.SetValue(null, summaryType);
            body();
        }
        finally
        {
            fLookup.SetValue(null, savedLookup);
            fSummary.SetValue(null, savedSummary);
        }
    }

    private static FieldInfo Static(string name)
        => typeof(RecordPatches).GetField(name, Priv)
           ?? throw new InvalidOperationException($"test setup: RecordPatches.{name} not found");

    private static object? Invoke(string name, params object?[] args)
    {
        var m = typeof(RecordPatches).GetMethod(name, Priv)
            ?? throw new InvalidOperationException($"test setup: RecordPatches.{name} not found");
        try
        {
            return m.Invoke(null, args);
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            throw tie.InnerException;   // the reflection wrapper is not part of the contract
        }
    }

    /// <summary>Stands in for the lookup dictionary after Microsoft ships a second Add.</summary>
    public sealed class OverloadedAddDictionary<TKey, TValue> where TKey : notnull
    {
        public void Add(TKey key, TValue value) { }
        public void Add(TKey key, TValue value, bool overwrite) { }
    }

    /// <summary>The shape BC declares today: exactly one Add.</summary>
    public sealed class PlainAddDictionary<TKey, TValue> where TKey : notnull
    {
        public void Add(TKey key, TValue value) { }
        internal void Hidden() { }
    }

    private sealed class FakeSummary
    {
        public string ObjectName => "SUPER";
    }

    private sealed class FakeLazy<T>
    {
        public T? Value { get; set; }
    }

    private sealed class FakeAppGroup
    {
        public FakeLazy<OverloadedAddDictionary<string, object>>? OverloadedLookup;
        public FakeLazy<PlainAddDictionary<string, object>>? PlainLookup;
    }
}

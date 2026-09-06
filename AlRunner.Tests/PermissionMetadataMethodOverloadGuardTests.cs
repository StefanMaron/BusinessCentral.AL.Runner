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

    // ══ 2b. RequireBcInstance — the MissingMethodException route ════════════════════════
    //
    // Activator.CreateInstance(Type) raises MissingMethodException when the parameterless
    // constructor is gone. Anonymous, and absorbed by the same seam, so `asserterror` PASSES on
    // an instantiation real BC performs fine. Both directions, like every other helper in this
    // family.

    [Fact]
    public void RequireBcInstance_RaisesAShapeGapNamingTheType_WhenTheConstructorIsGone()
    {
        var ex = Assert.Throws<BcShapeGapException>(() => Invoke("RequireBcInstance",
            typeof(NoParameterlessCtor), "MetaPermission"));

        Assert.Equal("Permission metadata (NavAppGroup permission-set inventory)", ex.Surface);
        Assert.Equal("MetaPermission", ex.Member);
        Assert.Contains("NoParameterlessCtor has no parameterless constructor", ex.Detail,
            StringComparison.Ordinal);
    }

    // THE INVERSION for this route: pre-fix the MissingMethodException was swallowed and the
    // asserterror passed. The refusal must tear through instead.
    [Fact]
    public void AssertError_TearsThrough_InsteadOfSwallowingTheMissingConstructor()
    {
        var ex = Assert.Throws<BcShapeGapException>(() => BcRuntime.NavMethodScope_AssertError(
            null!, () => Invoke("RequireBcInstance", typeof(NoParameterlessCtor), "MetaPermission")));

        Assert.Equal("MetaPermission", ex.Member);
    }

    // THE INVERSION on the REAL call path, which is what the pre-fix build can be measured
    // against: InstallFreshPermissionSetLookup over a lookup whose dictionary type lost its
    // parameterless constructor. Pre-fix that is Activator.CreateInstance raising
    // MissingMethodException, swallowed, asserterror green.
    [Fact]
    public void AssertError_TearsThrough_WhenTheLookupDictionaryLostItsConstructor()
    {
        var ex = Assert.Throws<BcShapeGapException>(() => BcRuntime.NavMethodScope_AssertError(
            null!, () => WithInjectedStatics(
                typeof(FakeAppGroup).GetField(nameof(FakeAppGroup.NoCtorLookup))!,
                typeof(FakeSummary),
                () => Invoke("InstallFreshPermissionSetLookup", new object(), new List<object>()))));

        Assert.Equal("NavAppGroup.permissionSetLookup", ex.Member);
        Assert.Contains("has no parameterless constructor", ex.Detail, StringComparison.Ordinal);
    }

    // CONTROL: a type that HAS one is still constructed, so the refusal is about the missing
    // constructor and not about the helper refusing everything.
    [Fact]
    public void RequireBcInstance_StillConstructs_WhenTheParameterlessConstructorIsThere()
    {
        var made = Invoke("RequireBcInstance", typeof(PlainAddDictionary<string, object>), "x");

        Assert.IsType<PlainAddDictionary<string, object>>(made);
    }

    // A NON-PUBLIC constructor is accepted. Activator.CreateInstance(Type) would refuse it, so
    // this is deliberately wider than what it replaced: BC's own types are frequently internal,
    // and refusing a constructor that exists would turn a working build into a shape gap.
    [Fact]
    public void RequireBcInstance_AcceptsANonPublicConstructor_WhichActivatorWouldHaveRefused()
    {
        Assert.Throws<MissingMethodException>(() => Activator.CreateInstance(typeof(NonPublicCtor)));

        Assert.IsType<NonPublicCtor>(Invoke("RequireBcInstance", typeof(NonPublicCtor), "x"));
    }

    // THE VALUE-TYPE CARVE-OUT, which is load-bearing and had nothing pinning it. A struct
    // declares NO parameterless constructor, so the reference-type path would refuse one — and
    // BC declares MetaPermission as a struct with ctors = 0 on every shipped build measured
    // (27.3.44313.53909, 27.5.46862.48827, 28.1.49838.53910, 28.1.49838.54308, 28.2.50931.54319).
    // Without this branch the permission-set inventory refuses to populate on all five.
    [Fact]
    public void RequireBcInstance_ConstructsAStruct_ThatDeclaresNoParameterlessConstructor()
    {
        Assert.Empty(typeof(StructWithNoCtor).GetConstructors(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));

        var made = Invoke("RequireBcInstance", typeof(StructWithNoCtor), "MetaPermission");

        Assert.IsType<StructWithNoCtor>(made);
        Assert.Equal(0, ((StructWithNoCtor)made!).Id);
    }

    // ══ 2c. Compare's return type — the InvalidCastException route ══════════════════════
    //
    // `(int)compare.Invoke(...)` is a BC-shape assumption. A Compare that stopped returning int
    // raises InvalidCastException inside the seam, so the asserterror passes on a sort real BC
    // performs fine. Driven through InstallPermissionSetSlot — the real call path — with the
    // four reflection statics it reads injected for one call.

    [Fact]
    public void InstallPermissionSetSlot_RaisesAShapeGap_WhenCompareNoLongerReturnsInt()
    {
        var ex = Assert.Throws<BcShapeGapException>(() => InstallSlotWithComparer(typeof(StringComparer2)));

        Assert.Equal("NavAppGroup.GroupSummaryComparer.Compare", ex.Member);
        Assert.Contains("returns String, not the Int32 this sort unboxes", ex.Detail,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AssertError_TearsThrough_InsteadOfSwallowingTheCompareReturnTypeChange()
    {
        var ex = Assert.Throws<BcShapeGapException>(() => BcRuntime.NavMethodScope_AssertError(
            null!, () => InstallSlotWithComparer(typeof(StringComparer2))));

        Assert.Equal("NavAppGroup.GroupSummaryComparer.Compare", ex.Member);
    }

    // CONTROL: an int-returning Compare gets PAST the guard and fails at the next refusal — the
    // FrozenSharingArray constructor the fake open generic does not provide. Without this the
    // arms above could be satisfied by a guard that refused every comparer.
    [Fact]
    public void InstallPermissionSetSlot_GetsPastTheGuard_WhenCompareStillReturnsInt()
    {
        var ex = Assert.Throws<BcShapeGapException>(() => InstallSlotWithComparer(typeof(IntComparer2)));

        Assert.Equal("FrozenSharingArray<T>(IReadOnlyList<T>, IComparer<T>)", ex.Member);
    }

    /// <summary>
    /// TWO summaries, not zero: List.Sort does not call the comparison delegate for a shorter
    /// list, so an empty one never reaches `(int)compare.Invoke(...)` and the pre-fix build would
    /// sail past the very cast these arms are about. With two, the pre-fix build raises
    /// InvalidCastException there — which is the failure the seam swallowed.
    /// </summary>
    private static void InstallSlotWithComparer(Type comparerType) => WithStatics(
        new (string, object?)[]
        {
            ("_fSummariesByType",       typeof(FakeGroupWithSlots).GetField(nameof(FakeGroupWithSlots.Slots))),
            ("_fComparerInstance",      comparerType.GetField("Instance")),
            ("_tGroupSummaryComparer",  comparerType),
            ("_tSummary",               typeof(FakeSummary)),
            ("_tFrozenSharingArrayOpen", typeof(FakeFrozen<>)),
        },
        () => Invoke("InstallPermissionSetSlot", new FakeGroupWithSlots(),
            new List<object> { new FakeSummary(), new FakeSummary() }));

    // ══ 2d. The property route — GetProperty carries it too ═════════════════════════════
    //
    // Type.GetProperty(name, flags) throws AmbiguousMatchException for a `new`-hidden property
    // whose TYPE changed, and for two same-name properties in one type. FindBcProperty
    // enumerates instead, so the outcome is the property or a named refusal.

    [Fact]
    public void FindBcProperty_CountsBothDeclarations_WhenAHiddenPropertyChangedItsType()
    {
        // The premise, measured rather than assumed: the call this replaced DOES throw here.
        Assert.Throws<AmbiguousMatchException>(() => typeof(HidesWithADifferentType).GetProperty(
            "P", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));

        var ex = Assert.Throws<BcShapeGapException>(
            () => Invoke("RequireBcProperty", typeof(HidesWithADifferentType), "P"));

        Assert.Equal("HidesWithADifferentType.P", ex.Member);
        Assert.Contains("BC declares 2 properties named P", ex.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AssertError_TearsThrough_InsteadOfSwallowingTheAmbiguousProperty()
    {
        var ex = Assert.Throws<BcShapeGapException>(() => BcRuntime.NavMethodScope_AssertError(
            null!, () => Invoke("RequireBcProperty", typeof(HidesWithADifferentType), "P")));

        Assert.Equal("HidesWithADifferentType.P", ex.Member);
    }

    // CONTROL: a `new`-hidden property of the SAME type is NOT ambiguous — reflection's
    // hiding-by-name-and-signature filter drops the base one — so it still resolves, to the
    // derived declaration. This is why the guard's comment says the trigger is narrower than
    // "a hidden property".
    [Fact]
    public void FindBcProperty_StillResolves_WhenTheHiddenPropertyKeptItsType()
    {
        var prop = (PropertyInfo)Invoke("RequireBcProperty", typeof(HidesWithTheSameType), "P")!;

        Assert.Equal(typeof(HidesWithTheSameType), prop.DeclaringType);
        Assert.Equal(2, prop.GetValue(new HidesWithTheSameType()));
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
    // TWO member kinds, and the guard covers BOTH — the first version covered only GetMethod,
    // which left the file able to drift straight back into the defect through GetProperty.
    // Measured on .NET 8.0.30 (the two shapes are NOT the same, and the difference matters):
    //
    //   Type.GetMethod(name, flags)    throws AmbiguousMatchException for an overload added in
    //                                  the SAME type, and for a `new`-hidden method whose
    //                                  signature changed.
    //   Type.GetProperty(name, flags)  throws for a `new`-hidden property whose TYPE changed
    //                                  (int P -> string P) and for two same-name properties in
    //                                  one type (an added indexer). It does NOT throw for a
    //                                  `new`-hidden property of the SAME type — reflection's
    //                                  hiding-by-name-and-signature filter drops the base one.
    //   Type.GetField(name, flags)     throws in NONE of those shapes; it returns the
    //                                  most-derived field. Field lookups are therefore not
    //                                  offenders and are deliberately not listed here.
    //
    // A method lookup clears the guard by pinning `types:`; a property lookup clears it by going
    // through FindBcProperty, which enumerates and so cannot throw. The two sibling files in the
    // slice already pinned every method signature (NavGuid.Create(Guid),
    // GetSystemPermissionSets(NavValue, NavCode), …) — the populator's helper was the outlier —
    // and their four property lookups were converted with it.

    [Fact]
    public void NoMemberLookupInTheSlice_ResolvesByNameWithoutASignature()
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

                // A method lookup is acceptable only with an explicit signature.
                if (flat.Contains(".GetMethod(", StringComparison.Ordinal)
                    && !flat.Contains("types:", StringComparison.Ordinal))
                    offenders.Add($"{file}: {flat.Trim()}");

                // A property lookup has no signature to pin — the plural enumeration is the
                // only acceptable form, and it is spelled GetProperties, which does not match.
                if (flat.Contains(".GetProperty(", StringComparison.Ordinal))
                    offenders.Add($"{file}: {flat.Trim()}");
            }
        }

        Assert.True(offenders.Count == 0,
            "these member lookups resolve by name alone, so a declaration Microsoft adds raises "
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

    /// <summary>
    /// <see cref="WithInjectedStatics"/> generalised to any set of RecordPatches statics, for the
    /// call paths that read more than two. Same contract: overwrite for exactly one call, restore
    /// every one of them in a finally, and make nothing in production settable for the test.
    /// </summary>
    private static void WithStatics((string Name, object? Value)[] statics, Action body)
    {
        var fields = statics.Select(x => (Field: Static(x.Name), x.Value)).ToArray();
        var saved = fields.Select(f => f.Field.GetValue(null)).ToArray();
        try
        {
            foreach (var (field, value) in fields) field.SetValue(null, value);
            body();
        }
        finally
        {
            for (var i = 0; i < fields.Length; i++) fields[i].Field.SetValue(null, saved[i]);
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

    /// <summary>A lookup dictionary whose parameterless constructor Microsoft removed.</summary>
    public sealed class NoCtorDictionary<TKey, TValue> where TKey : notnull
    {
        public NoCtorDictionary(int required) => Required = required;
        public int Required { get; }
        public void Add(TKey key, TValue value) { }
    }

    /// <summary>The shape BC declares today: exactly one Add.</summary>
    public sealed class PlainAddDictionary<TKey, TValue> where TKey : notnull
    {
        public void Add(TKey key, TValue value) { }
        internal void Hidden() { }
    }

    public sealed class FakeSummary
    {
        public string ObjectName => "SUPER";
    }

    /// <summary>A BC type whose parameterless constructor Microsoft removed.</summary>
    public sealed class NoParameterlessCtor
    {
        public NoParameterlessCtor(int required) => Required = required;
        public int Required { get; }
    }

    /// <summary>A BC type whose only constructor is non-public — common for Ncl internals.</summary>
    public sealed class NonPublicCtor
    {
        internal NonPublicCtor() { }
    }

    /// <summary>Stands in for MetaPermission, which BC declares as a struct.</summary>
    public struct StructWithNoCtor
    {
        public int Id;
    }

    private sealed class FakeGroupWithSlots
    {
        // Longer than ObjectType.PermissionSet's ordinal (20), so the slot is in range.
        public object?[] Slots = new object?[64];
    }

    public sealed class StringComparer2
    {
        public static readonly StringComparer2 Instance = new();
        public string Compare(FakeSummary x, FakeSummary y) => "not an int";
    }

    public sealed class IntComparer2
    {
        public static readonly IntComparer2 Instance = new();
        public int Compare(FakeSummary x, FakeSummary y) => 0;
    }

    /// <summary>An open generic standing in for FrozenSharingArray&lt;T&gt;, with no two-argument
    /// constructor — the refusal the int-returning control arm is expected to reach.</summary>
    public sealed class FakeFrozen<T>
    {
    }

    private class BaseWithP { public int P => 1; }

    /// <summary>The shape GetProperty(name, flags) cannot resolve: both declarations are
    /// returned by GetProperties because the types differ.</summary>
    private sealed class HidesWithADifferentType : BaseWithP { public new string P => "two"; }

    /// <summary>The shape it CAN resolve: same type, so the base one is filtered out.</summary>
    private sealed class HidesWithTheSameType : BaseWithP { public new int P => 2; }

    private sealed class FakeLazy<T>
    {
        public T? Value { get; set; }
    }

    private sealed class FakeAppGroup
    {
        public FakeLazy<OverloadedAddDictionary<string, object>>? OverloadedLookup;
        public FakeLazy<PlainAddDictionary<string, object>>? PlainLookup;
        public FakeLazy<NoCtorDictionary<string, object>>? NoCtorLookup;
    }
}

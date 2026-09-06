// PermissionMetadataNullForgivingGuardTests — the `!` half of the permission-metadata slice (#3046).
//
// WHAT #3034 LEFT, AND WHY ITS SWEEP COULD NOT SEE IT
//   #3034 converted 56 of the 61 InvalidOperationException refusals in
//   RecordPatches.PermissionMetadataPopulator.cs to BcShapeGapException, and its header says
//   every refusal in the file was read and classified. That claim is true as written — but its
//   search shape was `throw new InvalidOperationException`, and five BC-internals reads in the
//   same file are not `throw` sites at all. They are null-forgiving `!` operators:
//
//     _tNavAppGroupPM!.GetProperty("PermissionSetGroupObjectMetadataSummaries", ...)!
//     lazyType.GetGenericArguments()[0]                       (permissionSetLookup's LazyEx<T>)
//     dictType.GetMethod("Add")!                              (that LazyEx's dictionary)
//     _tSummary!.GetProperty("ObjectName", ...)!
//     _tMetaPermissionSet.GetProperty("Included/ExcludedPermissionSets")!   (x2)
//
//   `!` is a compiler annotation and throws nothing by itself, so a member Microsoft moves does
//   not fail at the lookup: the lookup hands back a silent null, and the NullReferenceException
//   lands at the first USE of it (.PropertyType, .Invoke, .GetValue). The generic-argument read
//   is the one that does fail on the spot, with IndexOutOfRangeException. Neither says which
//   member moved, which is the message #3034 exists to produce.
//
// THE INVERSION, WHICH IS THE POINT
//   MethodScopePatches.NavMethodScope_AssertError is an unfiltered catch(Exception), so it
//   swallows an NRE exactly as it swallowed the retired InvalidOperationException: AL sees an
//   absent or default answer and `asserterror` PASSES, while real BC reads the member fine and
//   would have failed it. The AssertError/TryFunction arms below fail on a pre-fix build with
//   "No exception was thrown" — that failure IS the inversion, and it is what the guards remove.
//
// THE INCLUDED/EXCLUDED PAIR IS THE SHARPEST OF THE FIVE
//   SetProperty(mps, "IncludedPermissionSets", ...) WAS converted by #3034 — but the unguarded
//   GetProperty("IncludedPermissionSets")! is an ARGUMENT to that call, so it evaluates first
//   and the new guard on the very same property is unreachable through that path. The
//   source-shape arm at the bottom is what keeps a future edit from reintroducing that.
//
// HOW THESE ARE DRIVEN
//   Two ways, both against real production code and neither needing a BC install:
//     * directly, with a fake type standing in for a BC type whose member moved — the shape
//       PermissionMetadataShapeGapTests' four arms already use, since the helpers take the
//       declaring type as a PARAMETER; and
//     * through InstallFreshPermissionSetLookup itself, by injecting a fake into the cached
//       reflection statics for one call and restoring them in a finally (#3041's shape) — so
//       the refusal is proved on the real call path, not only on the helper.
//   Every refusal arm is paired with a control arm that still succeeds, so a guard that threw
//   unconditionally would fail here rather than pass.
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

public sealed class PermissionMetadataNullForgivingGuardTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private const BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Static;

    // ══ 1. RequireBcProperty — MetaPermissionSet.IncludedPermissionSets et al ════════════

    [Fact]
    public void RequireBcProperty_RaisesAShapeGapNamingTheMember_WhenBcsPropertyHasMoved()
    {
        var ex = Assert.Throws<BcShapeGapException>(
            () => Invoke("RequireBcProperty", typeof(FakeMetaPermissionSetWithoutIncludes), "IncludedPermissionSets"));

        Assert.Equal("Permission metadata (NavAppGroup permission-set inventory)", ex.Surface);
        Assert.Equal($"{nameof(FakeMetaPermissionSetWithoutIncludes)}.IncludedPermissionSets", ex.Member);
        Assert.Contains("property not found", ex.Detail, StringComparison.Ordinal);
        Assert.StartsWith("bc-shape-gap: ", ex.Message, StringComparison.Ordinal);
        Assert.EndsWith(" — see docs/limitations.md#bc-shape-gaps", ex.Message, StringComparison.Ordinal);
    }

    // CONTROL: the property that IS there is still returned, with its real PropertyType — the
    // value BuildIncludeList is handed at the call site.
    [Fact]
    public void RequireBcProperty_StillReturnsTheProperty_AndItsElementType_WhenPresent()
    {
        var prop = (PropertyInfo)Invoke("RequireBcProperty",
            typeof(FakeMetaPermissionSet), "IncludedPermissionSets")!;

        Assert.Equal("IncludedPermissionSets", prop.Name);
        Assert.Equal(typeof(List<int>), prop.PropertyType);
    }

    // A non-public property still resolves: BC declares these publicly today, and SetProperty —
    // which writes the very same member — has always looked with NonPublic too. The argument
    // read used the default flags and so could have missed what the write then found.
    [Fact]
    public void RequireBcProperty_FindsANonPublicProperty_SoTheReadAndTheWriteAgree()
    {
        var prop = (PropertyInfo)Invoke("RequireBcProperty",
            typeof(FakeMetaPermissionSet), "HiddenPermissionSets")!;

        Assert.Equal(typeof(List<string>), prop.PropertyType);
    }

    // ══ 2. RequireBcMethod — the lookup dictionary's Add ═════════════════════════════════

    [Fact]
    public void RequireBcMethod_RaisesAShapeGapNamingTheMember_WhenTheMethodHasMoved()
    {
        var ex = Assert.Throws<BcShapeGapException>(
            () => Invoke("RequireBcMethod", typeof(NoAddCollection), "Add"));

        Assert.Equal($"{nameof(NoAddCollection)}.Add", ex.Member);
        Assert.Contains("method not found", ex.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void RequireBcMethod_StillReturnsTheMethod_WhenPresent()
    {
        var m = (MethodInfo)Invoke("RequireBcMethod", typeof(Dictionary<string, object>), "Add")!;

        Assert.Equal("Add", m.Name);
        Assert.Equal(2, m.GetParameters().Length);
    }

    // ══ 3. RequireBcLookupDictionaryType — permissionSetLookup's LazyEx<T> ═══════════════

    [Fact]
    public void RequireBcLookupDictionaryType_RaisesAShapeGapNamingTheField_WhenTheFieldIsNotAOneArgumentGeneric()
    {
        var ex = Assert.Throws<BcShapeGapException>(
            () => Invoke("RequireBcLookupDictionaryType", typeof(string)));

        Assert.Equal("NavAppGroup.permissionSetLookup", ex.Member);
        Assert.Contains("String", ex.Detail, StringComparison.Ordinal);
        Assert.Contains("LazyEx<T>", ex.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void RequireBcLookupDictionaryType_StillUnwrapsTheDictionary_ForTheShapeBcDeclares()
    {
        var dictType = (Type)Invoke("RequireBcLookupDictionaryType",
            typeof(FakeLazy<Dictionary<string, object>>))!;

        Assert.Equal(typeof(Dictionary<string, object>), dictType);
    }

    // ══ 4. THE INVERSION — both AL seams, on all three guards ═══════════════════════════
    //
    // Measured on the pre-fix build: every one of these fails with "Assert.Throws() Failure: No
    // exception was thrown", because the seam swallowed the NullReferenceException /
    // IndexOutOfRangeException and AL's asserterror PASSED on a read real BC performs fine. That
    // failure text is the inversion, and removing it is what the guards are for.

    public static TheoryData<string, string> UnreadableMembers() => new()
    {
        { "IncludedPermissionSets", "FakeMetaPermissionSetWithoutIncludes.IncludedPermissionSets" },
        { "ExcludedPermissionSets", "FakeMetaPermissionSetWithoutIncludes.ExcludedPermissionSets" },
        { "ObjectName",             "NoAddCollection.ObjectName" },
        { "Add",                    "NoAddCollection.Add" },
        { "permissionSetLookup",    "NavAppGroup.permissionSetLookup" },
    };

    /// <summary>
    /// Drive the production helper whose BC member the named case has moved, AND dereference
    /// what it hands back exactly as the call site does. The dereference is the point: `!` is a
    /// compiler annotation and throws nothing by itself, so a pre-fix build gets a silent null
    /// out of the lookup and NREs at the first use of it — <c>.PropertyType</c> fed to
    /// BuildIncludeList, <c>.Invoke</c> on the dictionary's Add, <c>.GetValue</c> on the
    /// summary. That NRE is what the seam then swallows.
    /// </summary>
    private static object? ReadMovedMember(string @case) => @case switch
    {
        "IncludedPermissionSets" => ((PropertyInfo)Invoke("RequireBcProperty", typeof(FakeMetaPermissionSetWithoutIncludes), "IncludedPermissionSets")!).PropertyType,
        "ExcludedPermissionSets" => ((PropertyInfo)Invoke("RequireBcProperty", typeof(FakeMetaPermissionSetWithoutIncludes), "ExcludedPermissionSets")!).PropertyType,
        "ObjectName"             => ((PropertyInfo)Invoke("RequireBcProperty", typeof(NoAddCollection), "ObjectName")!).Name,
        "Add"                    => ((MethodInfo)Invoke("RequireBcMethod", typeof(NoAddCollection), "Add")!).Name,
        "permissionSetLookup"    => Invoke("RequireBcLookupDictionaryType", typeof(string)),
        _ => throw new InvalidOperationException($"test setup: unknown case {@case}"),
    };

    [Theory]
    [MemberData(nameof(UnreadableMembers))]
    public void AssertError_TearsThrough_InsteadOfSwallowingTheNullForgivingFailure(string @case, string member)
    {
        var ex = Assert.Throws<BcShapeGapException>(
            () => BcRuntime.NavMethodScope_AssertError(null!, () => ReadMovedMember(@case)));

        Assert.Equal("Permission metadata (NavAppGroup permission-set inventory)", ex.Surface);
        Assert.Equal(member, ex.Member);
    }

    [Theory]
    [MemberData(nameof(UnreadableMembers))]
    public void TryFunction_TearsThrough_InsteadOfSwallowingTheNullForgivingFailure(string @case, string member)
    {
        var ex = Assert.Throws<BcShapeGapException>(
            () => BcRuntime.NavApplicationObjectBase_TryInvoke(null, () => ReadMovedMember(@case)));

        Assert.Equal("Permission metadata (NavAppGroup permission-set inventory)", ex.Surface);
        Assert.Equal(member, ex.Member);
    }

    // CONTROL: the seams still trap what they are supposed to trap, so "tears through" above is
    // a statement about BcShapeGapException and not about a seam that catches nothing.
    [Fact]
    public void BothSeams_StillTrapAPermanentRefusal_SoTearThroughIsNotVacuous()
    {
        Assert.False(BcRuntime.NavApplicationObjectBase_TryInvoke(
            null, () => throw new RunnerOutOfScopeException(
                "NavEmail.Send", "email-smtp — no SMTP transport in the runner", "email")));

        BcRuntime.NavMethodScope_AssertError(null!, () => throw new RunnerOutOfScopeException(
            "NavEmail.Send", "email-smtp — no SMTP transport in the runner", "email"));
    }

    // ══ 5. The real call path — InstallFreshPermissionSetLookup, with a fake injected ════
    //
    // #3041's shape: overwrite the cached reflection statics for exactly one call and restore
    // them in a finally. Nothing in production is made settable for the test. The arms below
    // prove the refusal on the path AL actually reaches, not only on the helper in isolation.

    [Fact]
    public void InstallFreshPermissionSetLookup_RaisesAShapeGap_WhenSummaryObjectNameHasMoved()
    {
        var ex = Assert.Throws<BcShapeGapException>(() => WithInjectedStatics(
            lookupField: typeof(FakeAppGroup).GetField(nameof(FakeAppGroup.PermissionSetLookup))!,
            summaryType: typeof(NoAddCollection),           // stands in for a Summary with no ObjectName
            () => Invoke("InstallFreshPermissionSetLookup", new object(), new List<object>())));

        Assert.Equal($"{nameof(NoAddCollection)}.ObjectName", ex.Member);
        Assert.Contains("property not found", ex.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallFreshPermissionSetLookup_RaisesAShapeGap_WhenPermissionSetLookupIsNoLongerAGenericLazy()
    {
        var ex = Assert.Throws<BcShapeGapException>(() => WithInjectedStatics(
            lookupField: typeof(FakeAppGroup).GetField(nameof(FakeAppGroup.NotALazy))!,
            summaryType: typeof(FakeSummary),
            () => Invoke("InstallFreshPermissionSetLookup", new object(), new List<object>())));

        Assert.Equal("NavAppGroup.permissionSetLookup", ex.Member);
        Assert.Contains("String", ex.Detail, StringComparison.Ordinal);
    }

    // CONTROL: with a summary type that HAS ObjectName the same call gets past all three guards
    // and fails later, on the LazyEx constructor a fake Lazy does not provide — proving the
    // guards above refused on the member and not on the injection itself.
    [Fact]
    public void InstallFreshPermissionSetLookup_GetsPastAllThreeGuards_WhenTheMembersArePresent()
    {
        var ex = Assert.Throws<BcShapeGapException>(() => WithInjectedStatics(
            lookupField: typeof(FakeAppGroup).GetField(nameof(FakeAppGroup.PermissionSetLookup))!,
            summaryType: typeof(FakeSummary),
            () => Invoke("InstallFreshPermissionSetLookup", new object(), new List<object>())));

        Assert.Equal("LazyEx<T>(Func<T>)", ex.Member);
    }

    // ══ 6. The shape, not just the four lines the issue named ═══════════════════════════
    //
    // A null-forgiving `!` on a BC-internals member lookup is invisible to #3034's search shape
    // by construction, so a source-level assertion is the only thing that keeps the next edit
    // from adding one back. Scoped to the three files of #3034's slice.

    [Fact]
    public void NoBcInternalsMemberLookupInTheSlice_IsGuardedOnlyByANullForgivingOperator()
    {
        var lookup = new Regex(@"\.Get(Propert|Method|Field|NestedType|Constructor)[a-z]*\s*\([^;]*?\)\s*!");
        var offenders = new List<string>();

        foreach (var file in SliceFiles)
        {
            var path = Path.Combine(RepoRoot, "AlRunner", "Patches", file);
            Assert.True(File.Exists(path), $"{file} not found at {path} — it was renamed or moved.");

            // Comments are stripped first, for the same reason the sibling guard in
            // PermissionMetadataShapeGapTests skips them: prose describing the shape — this
            // file's own header does exactly that — is not a live read.
            var code = string.Join("\n", File.ReadAllLines(path).Select(StripLineComment));

            foreach (var statement in code.Split(';'))
            {
                var flat = string.Join(" ", statement.Split('\n').Select(l => l.Trim()));
                if (lookup.IsMatch(flat)) offenders.Add($"{file}: {flat.Trim()}");
            }
        }

        Assert.True(offenders.Count == 0,
            "these BC-internals member lookups are guarded only by `!`, so a member Microsoft moves "
            + $"NREs into NavMethodScope_AssertError instead of naming the gap (#3046):{Environment.NewLine}"
            + string.Join(Environment.NewLine, offenders));
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

    private sealed class FakeMetaPermissionSet
    {
        public List<int>? IncludedPermissionSets { get; set; }
        public List<int>? ExcludedPermissionSets { get; set; }
        internal List<string>? HiddenPermissionSets { get; set; }
    }

    private sealed class FakeMetaPermissionSetWithoutIncludes
    {
        public int Id { get; set; }
    }

    private sealed class NoAddCollection
    {
        public int Count => 0;
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
        public FakeLazy<Dictionary<string, object>>? PermissionSetLookup;
        public string? NotALazy;
    }
}

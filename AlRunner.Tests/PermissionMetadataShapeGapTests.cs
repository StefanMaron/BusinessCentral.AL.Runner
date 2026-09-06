// PermissionMetadataShapeGapTests — the permission-metadata slice of #2994.
//
// WHAT IS BEING PROVED
//   #2946 settled that "the runner could not READ BC's internals" raises
//   BcShapeGapException, and converted six readers. This file covers the next slice: every
//   BC-internals reflection guard behind the two permission virtual tables and the permission
//   metadata layer they share —
//
//     RecordPatches.AggregatePermissionSetVirtualTable.cs   (Aggregate Permission Set, 2000000167)
//     RecordPatches.MetadataPermissionSetVirtualTable.cs    (Metadata Permission Set, 2000000250)
//     RecordPatches.PermissionMetadataPopulator.cs          (the NavAppGroup inventory both drive)
//
//   The third file is in the slice because EnsurePermissionMetadataPopulated() is called from
//   the AL-entered populate path of BOTH tables and from nowhere else, so its refusals reach
//   AL exactly as the tables' own do. Leaving it out would have half-converted one path.
//
// WHY IT MATTERS THAT THE TYPE CHANGES
//   NavMethodScope_AssertError is an unfiltered catch(Exception). So today
//   `asserterror <read of Metadata Permission Set>` around a site whose BC member has moved
//   PASSES, where real BC reads the table fine and the asserterror fails. Catching the refusal
//   does not hide the gap, it INVERTS the result. BcShapeGapException tears through both AL
//   seams, which is the whole point of the conversion.
//
// THE FOUR END-TO-END ARMS
//   SetProperty, SetBackingField, SetEmptyListBackingField and BuildIncludeList each take the
//   reflected type or target as a PARAMETER, so they can be driven with a fake standing in for
//   a BC type whose member moved — real production code, no BC install required. Every one is
//   paired with a control arm that still succeeds, so a conversion that threw unconditionally
//   would fail here rather than pass.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

public sealed class PermissionMetadataShapeGapTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    // ══ 1. SetProperty — BC's MetaPermissionSet / MetaPermission property moved ═══════════

    [Fact]
    public void SetProperty_RaisesAShapeGapNamingTheMember_WhenBcsPropertyIsGone()
    {
        var ex = Assert.Throws<BcShapeGapException>(
            () => Invoke("SetProperty", new HasNoSuchProperty(), "Assignable", true));

        Assert.Contains("Assignable", ex.Member, StringComparison.Ordinal);
        Assert.Contains(nameof(HasNoSuchProperty), ex.Member, StringComparison.Ordinal);
        Assert.StartsWith("bc-shape-gap: ", ex.Message, StringComparison.Ordinal);
    }

    // CONTROL: it still writes a property that IS there.
    [Fact]
    public void SetProperty_StillWrites_WhenThePropertyIsPresent()
    {
        var target = new HasAutoProperty();
        Invoke("SetProperty", target, nameof(HasAutoProperty.Name), "SUPER");
        Assert.Equal("SUPER", target.Name);
    }

    // ══ 2. SetBackingField — the auto-property backing field BC generates ════════════════

    [Fact]
    public void SetBackingField_RaisesAShapeGapNamingTheMember_WhenTheBackingFieldIsGone()
    {
        var ex = Assert.Throws<BcShapeGapException>(
            () => Invoke("SetBackingField", typeof(HasNoSuchProperty), new HasNoSuchProperty(), "Permissions", null));

        Assert.Contains("Permissions", ex.Member, StringComparison.Ordinal);
        Assert.Contains("backing field", ex.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void SetBackingField_StillPokes_WhenTheBackingFieldIsPresent()
    {
        var target = new HasAutoProperty();
        Invoke("SetBackingField", typeof(HasAutoProperty), target, nameof(HasAutoProperty.Name), "SECURITY");
        Assert.Equal("SECURITY", target.Name);
    }

    // ══ 3. SetEmptyListBackingField — the property whose element type it reads ════════════

    [Fact]
    public void SetEmptyListBackingField_RaisesAShapeGapNamingTheProperty_WhenItIsGone()
    {
        var ex = Assert.Throws<BcShapeGapException>(
            () => Invoke("SetEmptyListBackingField", typeof(HasNoSuchProperty), new HasNoSuchProperty(), "IncludedPermissionSets"));

        Assert.Contains("IncludedPermissionSets", ex.Member, StringComparison.Ordinal);
    }

    [Fact]
    public void SetEmptyListBackingField_StillInstallsAnEmptyList_WhenThePropertyIsPresent()
    {
        var target = new HasListProperty();
        Invoke("SetEmptyListBackingField", typeof(HasListProperty), target, nameof(HasListProperty.Items));

        Assert.NotNull(target.Items);
        Assert.Empty(target.Items!);
    }

    // ══ 4. BuildIncludeList — BC's include/exclude element type is one it cannot fill ═════

    [Fact]
    public void BuildIncludeList_RaisesAShapeGapNamingTheElementType_WhenItCannotBeFilled()
    {
        var ex = Assert.Throws<BcShapeGapException>(
            () => Invoke("BuildIncludeList", typeof(List<NotFillableElement>), (IReadOnlyList<string>)new[] { "SUPER" }));

        Assert.Contains(nameof(NotFillableElement), ex.Member, StringComparison.Ordinal);
    }

    // CONTROL: the string element type — what BC actually declares — still fills.
    [Fact]
    public void BuildIncludeList_StillFills_AStringElementList()
    {
        var list = (IList)Invoke("BuildIncludeList", typeof(List<string>), (IReadOnlyList<string>)new[] { "SUPER", "SECURITY" })!;

        Assert.Equal(2, list.Count);
        Assert.Equal("SUPER", list[0]);
        Assert.Equal("SECURITY", list[1]);
    }

    // ══ 5. The sweep happened, and it stopped exactly where the judgement said ════════════
    //
    // Pins BOTH directions. Under-conversion fails (a BC-internals read still raising the
    // retired type is not in the allowlist); over-conversion fails too (the five deliberate
    // NON-conversions have to survive). Matching on message text rather than line number so
    // the guard does not rot on an unrelated edit.

    [Fact]
    public void EveryRemainingInvalidOperationExceptionInTheSlice_IsOneOfTheFiveDeliberateNonConversions()
    {
        // Each of these five raises after a read that SUCCEEDED, so BcShapeGapException.cs's
        // line says they stay as they are:
        //   * the field/assembly was resolved and BC (or the runner's own setup) answered null,
        //   * or the assembly is absent, which is the runner's load chain rather than BC's layout.
        string[] allowed =
        {
            "PermissionSetRecord.permissionSetKey was null",       // BC's record answered null
            "NavAppGroup.BaseGroup is null",                       // skeleton the RUNNER populates
            "Microsoft.Dynamics.Nav.Ncl is not loaded",            // runner load chain, not BC shape
            "Microsoft.Dynamics.Nav.Types is not loaded",          // ditto (x2 — two resolvers)
        };

        var offenders = new List<string>();
        var survivors = 0;

        foreach (var file in SliceFiles)
        {
            var path = Path.Combine(RepoRoot, "AlRunner", "Patches", file);
            Assert.True(File.Exists(path), $"{file} not found at {path} — it was renamed or moved.");

            var lines = File.ReadAllLines(path);
            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].Contains("throw new InvalidOperationException", StringComparison.Ordinal)) continue;

                // The whole throw expression, which may span lines.
                var end = i;
                while (end < lines.Length - 1 && !lines[end].TrimEnd().EndsWith(";", StringComparison.Ordinal)) end++;
                var text = string.Join(" ", lines.Skip(i).Take(end - i + 1).Select(l => l.Trim()));

                if (allowed.Any(a => text.Contains(a, StringComparison.Ordinal))) { survivors++; continue; }
                offenders.Add($"{file}:{i + 1}: {text}");
            }
        }

        Assert.True(offenders.Count == 0,
            "these BC-internals reads still raise the retired InvalidOperationException convention "
            + $"instead of BcShapeGapException (#2994):{Environment.NewLine}"
            + string.Join(Environment.NewLine, offenders));

        Assert.Equal(5, survivors);
    }

    private static readonly string[] SliceFiles =
    {
        "RecordPatches.AggregatePermissionSetVirtualTable.cs",
        "RecordPatches.MetadataPermissionSetVirtualTable.cs",
        "RecordPatches.PermissionMetadataPopulator.cs",
    };

    // ══ 6. The three factories, and what AL can do with what they raise: nothing ══════════

    public static TheoryData<string, string> Factories() => new()
    {
        { "AggregatePermissionSetBcShapeGap", "Aggregate Permission Set (virtual table 2000000167)" },
        { "MetadataPermissionSetBcShapeGap",  "Metadata Permission Set (virtual table 2000000250)" },
        { "PermissionMetadataBcShapeGap",     "Permission metadata (NavAppGroup permission-set inventory)" },
    };

    [Theory]
    [MemberData(nameof(Factories))]
    public void EachFactory_NamesItsSurfaceTheMemberAndTheShapeGapDoc(string factory, string surface)
    {
        var ex = Build(factory, "NavAppGroup.permissionSetLookup", "field not found — probe");

        Assert.Equal(surface, ex.Surface);
        Assert.Equal("NavAppGroup.permissionSetLookup", ex.Member);
        Assert.StartsWith("bc-shape-gap: ", ex.Message, StringComparison.Ordinal);
        Assert.Contains(surface, ex.Message, StringComparison.Ordinal);
        Assert.Contains("NavAppGroup.permissionSetLookup", ex.Message, StringComparison.Ordinal);
        // NOT docs/scope.md: a shape gap is not a scope claim.
        Assert.EndsWith(" — see docs/limitations.md#bc-shape-gaps", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("docs/scope.md", ex.Message, StringComparison.Ordinal);
    }

    // The absorption defence: none of these may be recovered as an out-of-scope signal, so no
    // expect-oos entry can ever declare a BC-layout regression on these tables as expected.
    [Theory]
    [MemberData(nameof(Factories))]
    public void NeitherTheReporterNorTheManifest_MistakesOneForAnOutOfScopeRefusal(string factory, string surface)
    {
        var ex = Build(factory, "MetaPermissionSet.Access", "property not found — probe");

        Assert.False(OutOfScopeMessage.TryParse(ex.Message, out _));
        Assert.Null(OutOfScopeMessage.FromException(ex));
        Assert.NotNull(BcShapeGapException.Find(ex));
        _ = surface;
    }

    // ── The AL seams. This is the behaviour change the conversion is FOR. ──
    // Before it these sites raised InvalidOperationException, which NavMethodScope_AssertError
    // catches — so `asserterror <read of one of these tables>` passed while real BC, which reads
    // the table fine, would have failed it.

    [Theory]
    [MemberData(nameof(Factories))]
    public void AssertError_TearsThrough_ForEachPermissionSurface(string factory, string surface)
    {
        var ex = Assert.Throws<BcShapeGapException>(() => BcRuntime.NavMethodScope_AssertError(
            null!, () => throw Build(factory, "NavAppGroup.BaseGroup", "static field not found — probe")));

        Assert.Equal(surface, ex.Surface);
    }

    [Theory]
    [MemberData(nameof(Factories))]
    public void TryFunction_TearsThrough_ForEachPermissionSurface(string factory, string surface)
    {
        var ex = Assert.Throws<BcShapeGapException>(() => BcRuntime.NavApplicationObjectBase_TryInvoke(
            null, () => throw Build(factory, "NavCode(int, string)", "constructor not found — probe")));

        Assert.Equal(surface, ex.Surface);
    }

    // ── CONTROL ARMS ──
    // Without these, "tears through" would be satisfied by seams that trapped nothing at all,
    // and by an assertion that merely discriminated on exception type.

    [Fact]
    public void BothSeams_StillTrapAPermanentRefusal_SoTearThroughIsNotVacuous()
    {
        Assert.False(BcRuntime.NavApplicationObjectBase_TryInvoke(
            null, () => throw new RunnerOutOfScopeException(
                "NavEmail.Send", "email-smtp — no SMTP transport in the runner", "email")));

        // asserterror returning normally IS its pass signal.
        BcRuntime.NavMethodScope_AssertError(null!, () => throw new RunnerOutOfScopeException(
            "NavEmail.Send", "email-smtp — no SMTP transport in the runner", "email"));
    }

    // The three refusals in this slice that were NOT converted keep the OLD contract, and that
    // is the point of leaving them: "data access has no in-memory provider" is an answer about
    // the RUNNER's own store wiring, so an expect-oos entry may still absorb it.
    [Fact]
    public void TheUnconvertedVirtualTableRefusals_AreStillAbsorbableOutOfScopeSignals()
    {
        var refusal = RecordPatches.MetadataPermissionSetShapeGap("data access has no in-memory provider");

        Assert.IsType<RunnerOutOfScopeException>(refusal);
        Assert.Null(BcShapeGapException.Find(refusal));
        Assert.NotNull(OutOfScopeMessage.FromException(refusal));
        Assert.StartsWith("not-yet-implemented", refusal.Reason, StringComparison.Ordinal);
    }

    private static BcShapeGapException Build(string factory, string member, string detail)
    {
        var m = typeof(RecordPatches).GetMethod(factory, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"test setup: RecordPatches.{factory} not found");
        return (BcShapeGapException)m.Invoke(null, new object?[] { member, detail })!;
    }

    // ══ Plumbing ═════════════════════════════════════════════════════════════════════════

    private static object? Invoke(string name, params object?[] args)
    {
        var m = typeof(RecordPatches).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
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

    private sealed class HasNoSuchProperty
    {
    }

    private sealed class HasAutoProperty
    {
        public string? Name { get; set; }
    }

    private sealed class HasListProperty
    {
        public List<string>? Items { get; set; }
    }

    private sealed class NotFillableElement
    {
        // Deliberately NOT the (int, string) shape BuildIncludeList knows how to fill.
        public NotFillableElement(long ignored) => Ignored = ignored;
        public long Ignored { get; }
    }
}

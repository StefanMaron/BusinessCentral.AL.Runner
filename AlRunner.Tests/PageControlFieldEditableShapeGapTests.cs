// PageControlFieldEditableShapeGapTests — issue #3669.
//
// WHAT IS BEING PROVED
//   GetMetaFieldEditable answers the bound field's Editable for the "Page Control Field"
//   (2000000192) virtual table. It reaches BC's own Types.Metadata.MetaField through five
//   chained `?.` reflection lookups, and every failure path answered `true`.
//
//   `true` there is not a neutral sentinel. It is a real, load-bearing answer about a field:
//   SolveDocumentControlEditable's rule 2 renders it as the AL-visible "True", so a field BC
//   reports NON-editable would be reported editable, silently, on the BC version where any of
//   metadataAppGroupMetaTable / Item / Fields / Id / Editable moves. That is worse than an
//   empty list — it is a plausible wrong value, the shape .claude/rules/loud-failures.md
//   forbids and BcShapeGapException.cs's own line puts on the refusing side.
//
//   LATENT, not live: every BC version this repository tests resolves all five members.
//   These arms are about the version where one of them moves.
//
// WHICH READS REFUSE, AND WHY THAT IS NOT A MECHANICAL ANSWER
//   The judgement is per read, and getting it wrong in the other direction would break an
//   ordinary page on EVERY BC version rather than only a future one. What decides it is
//   whether a null can be BC's own answer, and for three of the five that was settled by
//   measuring the member's TYPE off Microsoft.Dynamics.Nav.Types.dll (28.1.49838.54308):
//
//     MetaTable.Fields   ImmutableArray<MetaField> — a STRUCT. GetValue boxes it; it is
//                        never null. A null/non-enumerable read can therefore only be a
//                        failed lookup.                                        REFUSES
//     MetaField.Id       System.Int32  — never null.                           REFUSES
//     MetaField.Editable System.Boolean — never null.                          REFUSES
//
//   The two field/property LOOKUPS refuse for the ordinary reason: the member is absent.
//
//     metadataAppGroupMetaTable   field lookup                                 REFUSES
//     MetadataExtension`1.Item    property lookup                              REFUSES
//
//   Their VALUES do not, and that is the half a mechanical conversion gets wrong. Both are
//   reference-typed, and BC's own GetMetaTableOriginal() is literally
//   `return metadataAppGroupMetaTable?.Item;` (Ncl 28.1) — BC treats a null there as an
//   ANSWER and its callers take name-based fallbacks. The runner's own
//   AssignMetaTableOriginal is best-effort by design and documents that BC "keeps its
//   existing fallbacks rather than the table failing to build". Refusing on those values
//   would turn every table whose original MetaTable was never assigned into an error.
//
// THE NEGATIVE CONTROLS ARE THE POINT, NOT DECORATION
//   "A failed lookup refuses" is satisfiable by a change that refuses on everything. Three
//   exits must stay silent — an unassigned metadataAppGroupMetaTable, an Item reading null,
//   and the foreach completing without finding the field number (a table that genuinely has
//   no such field, which is BC's `field?.Editable ?? true`). Each has an arm here.
//
// WHY RUNNER-LOCAL AND NOT THE CORPUS
//   No AL statement can rename a private BC field, so a service tier has nothing to
//   adjudicate — the subject is the runner's own refusal contract. What BC ANSWERS for
//   Editable is already pinned upstream by corpus codeunit 60424 (PR #310, merged).
//   Same reasoning as BcShapeGapConventionTests and QueryDataItemFilterShapeGapTests.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

public sealed class PageControlFieldEditableShapeGapTests
{
    private const string Surface = "Page Control Field (system table 2000000192)";

    // ══ 1. THE FIVE LOOKUPS THAT MUST REFUSE ═════════════════════════════════════════════
    //
    // Each arm removes exactly ONE member and leaves the others in place, so an
    // implementation that threw unconditionally would still have to name the right member to
    // pass — and section 2's controls would fail it outright.

    [Fact]
    public void MetadataAppGroupMetaTableLookup_RaisesAShapeGapNamingTheMember_WhenBcRenamesIt()
    {
        var ex = Assert.Throws<BcShapeGapException>(
            () => Editable(new MetaTableWithoutTheAppGroupField(), fieldNo: 1));

        Assert.EndsWith(".metadataAppGroupMetaTable", ex.Member, StringComparison.Ordinal);
        Assert.Contains(nameof(MetaTableWithoutTheAppGroupField), ex.Member, StringComparison.Ordinal);
        Assert.StartsWith("bc-shape-gap: ", ex.Message, StringComparison.Ordinal);
        Assert.Equal(Surface, ex.Surface);
        Assert.EndsWith(" — see docs/limitations.md#bc-shape-gaps", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ItemLookup_RaisesAShapeGapNamingTheMember_WhenBcRenamesIt()
    {
        var ex = Assert.Throws<BcShapeGapException>(
            () => Editable(new FakeNclMetaTable(new ExtensionWithoutItem()), fieldNo: 1));

        Assert.EndsWith(".Item", ex.Member, StringComparison.Ordinal);
        Assert.Contains(nameof(ExtensionWithoutItem), ex.Member, StringComparison.Ordinal);
        Assert.StartsWith("bc-shape-gap: ", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FieldsLookup_RaisesAShapeGapNamingTheMember_WhenBcRenamesIt()
    {
        var ex = Assert.Throws<BcShapeGapException>(
            () => Editable(
                new FakeNclMetaTable(new FakeExtension(new MetaTableWithoutFields())),
                fieldNo: 1));

        Assert.EndsWith(".Fields", ex.Member, StringComparison.Ordinal);
        Assert.Contains(nameof(MetaTableWithoutFields), ex.Member, StringComparison.Ordinal);
        Assert.StartsWith("bc-shape-gap: ", ex.Message, StringComparison.Ordinal);
        // Condition 2 of #3669. The LOOKUP failing is a different fact from the member being
        // there and holding something unusable (the arm below). Asserting only "it throws"
        // passes when the OTHER branch throws, so each arm asserts its own distinguishing
        // substring PRESENT and the other one ABSENT.
        Assert.Contains("property not found", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("cannot be enumerated", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FieldsHoldingSomethingUnusable_RaisesASeparatelyWordedShapeGap_NotTheLookupOne()
    {
        // Present, and holding a shape the runner cannot walk. BcShapeGapException.cs's own
        // line puts this on the same refusing side as an absent member; folding the two into
        // one branch is how #2786's silent skip happened.
        var ex = Assert.Throws<BcShapeGapException>(
            () => Editable(
                new FakeNclMetaTable(new FakeExtension(new MetaTableWhoseFieldsIsAnInt())),
                fieldNo: 1));

        Assert.EndsWith(".Fields", ex.Member, StringComparison.Ordinal);
        Assert.Contains("Int32", ex.Message, StringComparison.Ordinal);
        Assert.Contains("cannot be enumerated", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("property not found", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void IdLookup_RaisesAShapeGapNamingTheMember_WhenBcRenamesIt()
    {
        // MetaField.Id is System.Int32 on BC, so GetValue can never answer null — an
        // unreadable Id is a moved member and nothing else.
        var ex = Assert.Throws<BcShapeGapException>(
            () => Editable(FakeTableWith(new FieldWithoutId()), fieldNo: 1));

        // The MEMBER half, not merely the string "Id" somewhere in Member: the declaring
        // type's own name contains "Id" for these fakes, so `Contains("Id")` alone is
        // satisfied by a refusal naming `.Editable` on a type called FieldWhoseIdIsAString.
        // Measured — a sabotage swapping the Id refusal's member for Editable passed the
        // weaker assertion. This is condition 2 of #3669 at its sharpest.
        Assert.EndsWith(".Id", ex.Member, StringComparison.Ordinal);
        Assert.Contains(nameof(FieldWithoutId), ex.Member, StringComparison.Ordinal);
        Assert.StartsWith("bc-shape-gap: ", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(".Editable", ex.Member, StringComparison.Ordinal);
    }

    [Fact]
    public void IdHoldingANonInteger_RaisesAShapeGapNamingWhatItActuallyHeld()
    {
        // The member kept its name and changed its type. Answering `true` here is the
        // original defect in its most deceptive form: every field silently misses the
        // fieldNo test and the method falls out of the loop reporting editable.
        var ex = Assert.Throws<BcShapeGapException>(
            () => Editable(FakeTableWith(new FieldWhoseIdIsAString()), fieldNo: 1));

        Assert.EndsWith(".Id", ex.Member, StringComparison.Ordinal);
        Assert.DoesNotContain(".Editable", ex.Member, StringComparison.Ordinal);
        Assert.Contains("String", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EditableLookup_RaisesAShapeGapNamingTheMember_WhenBcRenamesIt()
    {
        // The member #3669 is named for: MetaField.Editable is System.Boolean on BC, so a
        // null read is a moved member. Answering `true` reports a non-editable field as
        // editable — the wrong value this whole file exists to stop.
        var ex = Assert.Throws<BcShapeGapException>(
            () => Editable(FakeTableWith(new FieldWithoutEditable(1)), fieldNo: 1));

        Assert.EndsWith(".Editable", ex.Member, StringComparison.Ordinal);
        Assert.Contains(nameof(FieldWithoutEditable), ex.Member, StringComparison.Ordinal);
        Assert.StartsWith("bc-shape-gap: ", ex.Message, StringComparison.Ordinal);
        // The Id read on the same object SUCCEEDED, so this must not be reported as the Id
        // gap — the other half of condition 2 for this pair.
        Assert.DoesNotContain(".Id", ex.Member, StringComparison.Ordinal);
    }

    [Fact]
    public void EditableHoldingANonBoolean_RaisesAShapeGapNamingWhatItActuallyHeld()
    {
        var ex = Assert.Throws<BcShapeGapException>(
            () => Editable(FakeTableWith(new FieldWhoseEditableIsAString(1)), fieldNo: 1));

        Assert.EndsWith(".Editable", ex.Member, StringComparison.Ordinal);
        Assert.DoesNotContain(".Id", ex.Member, StringComparison.Ordinal);
        Assert.Contains("String", ex.Message, StringComparison.Ordinal);
    }

    // ══ 2. NEGATIVE CONTROLS — the exits that must STAY silent ═══════════════════════════
    //
    // Over-converting is worse than the defect: it breaks an ordinary page on every BC
    // version instead of only a future one. Each of these is a genuine answer, not a failed
    // read, and each would fail an implementation that refused on every null.

    [Fact]
    public void AFieldTheTableDoesNotCarry_AnswersTrue_WithoutThrowing()
    {
        // Condition 3 of #3669. The foreach completing without matching is BC's own
        // `field?.Editable ?? true` — a real "this table has no such field", not a failed
        // read. The five reads all SUCCEEDED here.
        Assert.True(Editable(FakeTableWith(new FakeMetaField(1, editable: false)), fieldNo: 99));
    }

    [Fact]
    public void AnUnassignedMetadataAppGroupMetaTable_AnswersTrue_WithoutThrowing()
    {
        // The field is THERE and holds null. AssignMetaTableOriginal is best-effort by
        // design, and BC's own GetMetaTableOriginal() is `metadataAppGroupMetaTable?.Item` —
        // so null is an answer BC itself produces and falls back from.
        Assert.True(Editable(new FakeNclMetaTable(null), fieldNo: 1));
    }

    [Fact]
    public void AnItemReadingNull_AnswersTrue_WithoutThrowing()
    {
        // Same reasoning one level down: MetadataExtension`1.Item is reference-typed and
        // BC's own `?.` chain treats a null Item as no original MetaTable.
        Assert.True(Editable(new FakeNclMetaTable(new FakeExtension(null)), fieldNo: 1));
    }

    [Fact]
    public void ANullEntryInTheFieldsSequence_IsSkippedRatherThanRead()
    {
        // Structural, not a failed read: a null element cannot be interrogated, and the real
        // field sitting after it must still be found.
        Assert.False(Editable(
            FakeTableWith(null, new FakeMetaField(1, editable: false)), fieldNo: 1));
    }

    // ══ 3. THE HAPPY PATH, so nothing above is vacuous ═══════════════════════════════════

    [Fact]
    public void ANonEditableField_AnswersFalse_AndAnEditableOneTrue()
    {
        // The value this method exists to carry, in both directions. Without this pair every
        // arm above is satisfiable by a method that always throws or always answers true.
        Assert.False(Editable(FakeTableWith(new FakeMetaField(7, editable: false)), fieldNo: 7));
        Assert.True(Editable(FakeTableWith(new FakeMetaField(7, editable: true)), fieldNo: 7));
    }

    [Fact]
    public void TheMatchingFieldDecides_NotTheFirstOne()
    {
        // Pins that the fieldNo test actually selects. A method ignoring it and reading the
        // first entry would answer true here.
        Assert.False(Editable(
            FakeTableWith(new FakeMetaField(1, editable: true), new FakeMetaField(2, editable: false)),
            fieldNo: 2));
    }

    [Fact]
    public void AnImmutableArrayOfFields_IsWalked_LikeTheStructBcActuallyHolds()
    {
        // BC's MetaTable.Fields is ImmutableArray<MetaField>, a struct that boxes to an
        // IEnumerable rather than ever being null — the measurement this file's header
        // records, and the reason the Fields read refuses on null at all. A fake handing back
        // a List would not exercise that.
        var fields = ImmutableArray.Create<object>(new FakeMetaField(3, editable: false));
        Assert.False(Editable(new FakeNclMetaTable(new FakeExtension(new MetaTableWithFields(fields))),
                              fieldNo: 3));
    }

    // ══ 4. WHAT AL CAN DO WITH THE REFUSAL: NOTHING ══════════════════════════════════════
    //
    // The point of BcShapeGapException over either RunnerOutOfScopeException flavour. Under
    // `asserterror` an absorbed refusal INVERTS the result: real BC reads Editable fine, so
    // the asserterror fails there and would have passed here.

    [Fact]
    public void TheRefusal_TearsThroughBothAlSeams_AndIsNotAnAbsorbableOutOfScopeSignal()
    {
        object Read() => Editable(FakeTableWith(new FieldWithoutEditable(1)), fieldNo: 1);

        var viaAssertError = Assert.Throws<BcShapeGapException>(
            () => BcRuntime.NavMethodScope_AssertError(null!, () => Read()));
        Assert.Contains("Editable", viaAssertError.Member, StringComparison.Ordinal);

        var viaTryFunction = Assert.Throws<BcShapeGapException>(
            () => BcRuntime.NavApplicationObjectBase_TryInvoke(null, () => Read()));
        Assert.Contains("Editable", viaTryFunction.Member, StringComparison.Ordinal);

        Assert.False(OutOfScopeMessage.TryParse(viaAssertError.Message, out _));
        Assert.Null(OutOfScopeMessage.FromException(viaAssertError));
    }

    // ══ 5. THE REFLECTION PIN: name, arity, uniqueness ═══════════════════════════════════
    //
    // ReflectionDrivenHelperLivenessTests measures LIVENESS from IL for every RecordPatches
    // member a test names as a literal, and this file names ReadMetaFieldEditable — so a
    // change leaving it declared but unreached by production fails there. Pinned HERE is the
    // half that guard cannot see: it exists, is unique by name, and takes what these arms pass.

    [Fact]
    public void TheHelperIsUniqueByName_AndTakesTheTwoParametersTheseArmsDriveItWith()
    {
        var candidates = typeof(RecordPatches)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => m.Name == "ReadMetaFieldEditable")
            .ToList();

        Assert.Single(candidates);
        Assert.Equal(
            new[] { typeof(object), typeof(int) },
            candidates[0].GetParameters().Select(p => p.ParameterType).ToArray());
        Assert.Equal(typeof(bool), candidates[0].ReturnType);
    }

    // ══ Plumbing ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Drives the real production helper. The NCLMetaTable is taken as <c>object</c> so a fake
    /// can stand in for a BC type whose member moved — the idiom
    /// QueryDataItemFilterShapeGapTests and PermissionMetadataShapeGapTests already use. The
    /// per-(table, field) memo is deliberately NOT in the path under test: it is keyed by
    /// table id, and these arms drive many different shapes for one nominal table.
    /// </summary>
    private static bool Editable(object meta, int fieldNo)
    {
        var m = typeof(RecordPatches).GetMethod(
                    "ReadMetaFieldEditable", BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "test setup: RecordPatches.ReadMetaFieldEditable not found");
        try
        {
            return (bool)m.Invoke(null, new object?[] { meta, fieldNo })!;
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            throw tie.InnerException;   // the reflection wrapper is not part of the contract
        }
    }

    private static object FakeTableWith(params object?[] fields)
        => new FakeNclMetaTable(new FakeExtension(new MetaTableWithFields(fields)));

    // ── Fakes standing in for BC's types. Each names the member it is missing. ──

    private sealed class MetaTableWithoutTheAppGroupField
    {
    }

    private sealed class FakeNclMetaTable
    {
        public FakeNclMetaTable(object? extension) => metadataAppGroupMetaTable = extension;

        // The name BC declares, private instance, exactly as NCLMetaTable holds it.
#pragma warning disable IDE0044, CS0169, IDE1006
        private readonly object? metadataAppGroupMetaTable;
#pragma warning restore IDE0044, CS0169, IDE1006
    }

    private sealed class ExtensionWithoutItem
    {
    }

    private sealed class FakeExtension
    {
        public FakeExtension(object? item) => Item = item;
        public object? Item { get; }
    }

    private sealed class MetaTableWithoutFields
    {
    }

    private sealed class MetaTableWhoseFieldsIsAnInt
    {
        public int Fields => 7;
    }

    private sealed class MetaTableWithFields
    {
        public MetaTableWithFields(IEnumerable fields) => Fields = fields;
        public IEnumerable Fields { get; }
    }

    private sealed class FakeMetaField
    {
        public FakeMetaField(int id, bool editable) { Id = id; Editable = editable; }
        public int Id { get; }
        public bool Editable { get; }
    }

    private sealed class FieldWithoutId
    {
        public bool Editable => true;
    }

    private sealed class FieldWhoseIdIsAString
    {
        public string Id => "1";
        public bool Editable => true;
    }

    private sealed class FieldWithoutEditable
    {
        public FieldWithoutEditable(int id) => Id = id;
        public int Id { get; }
    }

    private sealed class FieldWhoseEditableIsAString
    {
        public FieldWhoseEditableIsAString(int id) => Id = id;
        public int Id { get; }
        public string Editable => "yes";
    }
}

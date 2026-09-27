// Issue #4787: a PRECOMPILED page's Visible/Editable/Enabled arrive from SymbolReference.json in
// the raw AL spelling -- `Rec."Report Output Type" = Rec."Report Output Type"::Print` -- where a
// page the runner compiles carries the emitted one (`Report Output Type = 3`). These pin the
// runner's own reading of the raw spelling. What BC ANSWERS for such a property is the corpus's
// claim: codeunit 67403 "PXCM Tests" (Base Application page 682, three output-type arms).

using System.Reflection;
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Runtime;
using Xunit;

namespace AlRunner.Tests;

public sealed class PrecompiledRawClientExpressionTests
{
    // ---- the parser, with every resolver a fixed table -----------------------------------------

    // Globals and fields share one spelling in the EMITTED text, so the identifier resolver below
    // deliberately answers `Flag` differently from the field resolver: a `Rec.Flag` that reached
    // the identifier resolver would read false.
    private static readonly PageControlExpression.ResolveIdentifier Globals =
        (string name, bool quoted, out object? value) =>
        {
            (bool ok, object? v) = name switch
            {
                "Flag" => (true, (object?)false),
                "ShipToOptions" => (true, 2),
                _ => (false, null),
            };
            value = v;
            return ok;
        };

    private static readonly PageControlExpression.ResolveField Fields =
        (string name, out object? value) =>
        {
            (bool ok, object? v) = name switch
            {
                "Flag" => (true, (object?)true),
                "Report Output Type" => (true, 3),
                "Value" => (true, "x"),
                _ => (false, null),
            };
            value = v;
            return ok;
        };

    // Print is ordinal 3 and PDF 0, as in enum 482 "Job Queue Report Output Type".
    private static readonly PageControlExpression.ResolveMember Members =
        (PageControlExpression.MemberOwnerKind kind, string owner, string member, out object? ordinal) =>
        {
            ordinal = (kind, owner, member) switch
            {
                (PageControlExpression.MemberOwnerKind.Field, "Report Output Type", "Print") => 3,
                (PageControlExpression.MemberOwnerKind.Field, "Report Output Type", "PDF") => 0,
                (PageControlExpression.MemberOwnerKind.Name, "ShipToOptions", "Custom Address") => 2,
                (PageControlExpression.MemberOwnerKind.Name, "ShipToOptions", "Default") => 0,
                _ => null,
            };
            return ordinal != null;
        };

    private static bool Eval(string text)
    {
        var ok = PageControlExpression.TryEvaluateBoolean(text, Globals, Fields, Members, out var value, out var failure);
        Assert.True(ok, $"'{text}' should evaluate, but failed: {failure}");
        return value;
    }

    private static string Failure(string text, PageControlExpression.ResolveField? fields = null)
    {
        var ok = PageControlExpression.TryEvaluateBoolean(text, Globals, fields ?? Fields, Members, out _, out var failure);
        Assert.False(ok, $"'{text}' should NOT evaluate, but it returned a value");
        return failure!;
    }

    [Theory]
    // The corpus 67403 shape: the extension's modify() on page 682's "Printer Name".
    [InlineData("Rec.\"Report Output Type\" = Rec.\"Report Output Type\"::Print", true)]
    [InlineData("Rec.\"Report Output Type\" = Rec.\"Report Output Type\"::PDF", false)]
    [InlineData("Rec.\"Report Output Type\" <> Rec.\"Report Output Type\"::PDF", true)]
    // A page global's option literal (pages 41-51, 507, 5177: ShipToOptions).
    [InlineData("ShipToOptions = ShipToOptions::\"Custom Address\"", true)]
    [InlineData("ShipToOptions = ShipToOptions::Default", false)]
    // `Rec.` reads the FIELD, never a global of the same name.
    [InlineData("Rec.Flag", true)]
    [InlineData("not Rec.Flag", false)]
    [InlineData("Flag", false)]
    [InlineData("(Rec.Value <> '') and (ShipToOptions = ShipToOptions::\"Custom Address\")", true)]
    // The qualifier is matched as AL matches it: case-insensitively.
    [InlineData("rec.Flag", true)]
    public void RawSpelling_Evaluates(string text, bool expected)
        => Assert.Equal(expected, Eval(text));

    [Fact]
    public void UnknownMember_Refuses_NamingTheLiteral()
        => Assert.Contains("'Rec.Report Output Type::Word'",
            Failure("Rec.\"Report Output Type\" = Rec.\"Report Output Type\"::Word"));

    [Fact]
    public void UnknownField_Refuses_NamingTheReference()
        => Assert.Contains("'Rec.Nope'", Failure("Rec.Nope"));

    // A control's own Visible passes no field resolver: that is the #2596 rule, which must reach
    // the raw spelling too rather than a second rule for precompiled pages.
    [Fact]
    public void FieldReference_WithNoFieldResolver_Refuses_CitingTheOpenQuestion()
    {
        var ok = PageControlExpression.TryEvaluateBoolean("Rec.Flag", Globals, null, Members, out _, out var failure);
        Assert.False(ok);
        Assert.Contains("#2596", failure);
    }

    [Theory]
    // Only `Rec` qualifies a name; anything else is still outside the grammar.
    [InlineData("xRec.Flag", "'.'")]
    [InlineData("Other.Flag", "'.'")]
    [InlineData("Rec.", "field name")]
    [InlineData("ShipToOptions:: = 1", "member name")]
    [InlineData(":: Print", "'::'")]
    [InlineData("ShipToOptions : Default", "':'")]
    [InlineData("A::B::C = 1", "another '::'")]
    public void MalformedRawSpelling_Refuses(string text, string expected)
        => Assert.Contains(expected, Failure(text));

    // `Enum::"Type"::Member` routes to the EnumType owner, which the page resolver does not answer.
    [Fact]
    public void EnumTypeLiteral_IsRoutedAsAnEnumTypeOwner()
    {
        PageControlExpression.MemberOwnerKind? seen = null;
        string? seenOwner = null;
        PageControlExpression.ResolveMember spy =
            (PageControlExpression.MemberOwnerKind kind, string owner, string member, out object? ordinal) =>
            {
                seen = kind; seenOwner = owner; ordinal = 7; return true;
            };
        Assert.True(PageControlExpression.TryEvaluateBoolean(
            "Enum::\"Job Queue Report Output Type\"::Print = 7", Globals, Fields, spy, out var value, out _));
        Assert.True(value);
        Assert.Equal(PageControlExpression.MemberOwnerKind.EnumType, seen);
        Assert.Equal("Job Queue Report Output Type", seenOwner);
    }

    // ---- the ordinal, read off BC's own NavOption --------------------------------------------

    // Ordinals deliberately differ from positions: an index-for-ordinal mix-up would answer 1.
    private static AlEnumOptionMetadata SparseEnum() => new(
        name: "Raw Expression Kind",
        id: 4787001,
        options: new[] { "PDF", "Word", "Print" },
        indexes: new[] { 0, 1, 3 },
        implementations: null,
        captions: new[] { "PDF", "Word", "Print" });

    [Theory]
    [InlineData("Print", 3)]
    [InlineData("print", 3)]
    [InlineData("Word", 1)]
    [InlineData("PDF", 0)]
    public void TryOrdinalOfMember_AnswersTheOrdinal_NotThePosition(string member, int expected)
    {
        Assert.True(RunnerPageInstance.TryOrdinalOfMember(NavOption.Create(SparseEnum(), 0), member, out var ordinal));
        Assert.Equal(expected, ordinal);
    }

    [Fact]
    public void TryOrdinalOfMember_UnknownMember_IsFalse()
        => Assert.False(RunnerPageInstance.TryOrdinalOfMember(NavOption.Create(SparseEnum(), 0), "Excel", out _));

    [Fact]
    public void TryOrdinalOfMember_PlainOption_UsesItsPosition()
    {
        Assert.True(RunnerPageInstance.TryOrdinalOfMember(
            NavOption.Create(NCLOptionMetadata.Create("Welcome,ContactFilter,Finish"), 0), "Finish", out var ordinal));
        Assert.Equal(2, ordinal);
    }

    // ---- a page global's `::`, through a real RunnerPageInstance binding table -------------------

    private sealed class FakeExpression(string name, NavValue value)
    {
        public string Name { get; } = name;
        public NavValue Get() => value;
        public void Set(NavValue _) { }
    }

    private static bool ResolveMemberOnPage(string owner, string member, out object? ordinal)
    {
        var expressions = new Dictionary<string, object?>
        {
            // A precompiled page registers its global under the mangled key; the raw text names it.
            ["p4787p4787ShipToOptions"] = new FakeExpression("p4787p4787ShipToOptions", NavOption.Create(SparseEnum(), 1)),
        };
        var ctor = typeof(RunnerPageInstance).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic, binder: null,
            types: new[] { typeof(object), typeof(object), typeof(NavRecord), typeof(int), typeof(System.Collections.IDictionary) },
            modifiers: null) ?? throw new InvalidOperationException("RunnerPageInstance private ctor not found.");
        var page = (RunnerPageInstance)ctor.Invoke(new object?[] { new object(), new object(), null, 4787, expressions });

        var method = typeof(RunnerPageInstance).GetMethod("ResolveMemberOrdinal", BindingFlags.Instance | BindingFlags.NonPublic)
                     ?? throw new InvalidOperationException("ResolveMemberOrdinal not found.");
        var args = new object?[] { PageControlExpression.MemberOwnerKind.Name, owner, member, null };
        var ok = (bool)method.Invoke(page, args)!;
        ordinal = args[3];
        return ok;
    }

    [Fact]
    public void PageGlobalMember_ResolvesThroughTheGlobalsOwnBinding()
    {
        Assert.True(ResolveMemberOnPage("ShipToOptions", "Print", out var ordinal));
        Assert.Equal(3, ordinal);
    }

    [Fact]
    public void PageGlobalMember_UnknownOwnerOrMember_IsFalse()
    {
        Assert.False(ResolveMemberOnPage("NoSuchGlobal", "Print", out _));
        Assert.False(ResolveMemberOnPage("ShipToOptions", "Excel", out _));
    }
}

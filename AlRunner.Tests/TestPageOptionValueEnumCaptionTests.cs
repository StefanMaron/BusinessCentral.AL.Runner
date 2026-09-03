// TestPageOptionValueEnumCaptionTests — issue #1928 (TestPage.SetValue on an Enum-typed
// control never resolved by the enum's declared Caption).
//
// This is a RUNNER-MECHANISM test, not a claim about what real BC does: the BC-behaviour
// claim ("TestPage.SetValue on an Enum control resolves by Caption and refuses the member
// name") is proven upstream against a live BC service tier — see
// StefanMaron/BusinessCentral.AL.Language.Tests#50 (branch
// agent/impl-8/issue-1896-enum-var-control), which the runner's own
// tests/runner-extras/page-enum-control-modal suite mirrors as a stopgap per
// docs/rules/bc-behavior-tests-go-upstream.md. This file exists so a regression in OUR OWN
// resolution logic (RunnerPageInstance.TryGetOptionCaptions's Enum fallback,
// TestPageOptionValue.EnumCaptions, and TestPageOptionValue.Resolve's Enum-vs-Option branch)
// fails loudly here, in milliseconds, without needing the BC engine or a compiled page.
//
// Deliberately does NOT load the BC engine: AlEnumOptionMetadata and NCLOptionMetadata are
// ordinary (precompiled-DLL / our-own) types constructible directly, via InternalsVisibleTo
// for the internal AlEnumOptionMetadata ctor and NCLOptionMetadata's own public Create(string)
// factory for the plain-Option comparison case. Same technique as
// NavRecordGetCallerRecordTests / MediaSetPatchesTests' "contract test" shape.
//
// RED/GREEN: reverting TestPageOptionValue.Resolve's `isEnumBacked` gate (i.e. always running
// the member-name fallback loop) makes RejectsTheBareMemberName fail — the call that should
// throw returns a resolved NavOption instead. Deleting TestPageOptionValue.EnumCaptions (or
// reverting it to `=> null`) makes AcceptsTheDeclaredCaption fail — SetValue('Blocks') would
// then have no caption table to match against and throw instead of resolving.
using AlRunner;
using AlRunner.Infrastructure;
using Microsoft.Dynamics.Nav.Runtime;
using Xunit;

namespace AlRunner.Tests;

public sealed class TestPageOptionValueEnumCaptionTests
{
    private sealed class FakeExpression(string name, NavValue value)
    {
        private NavValue _value = value;
        public string Name { get; } = name;
        public NavValue Get() => _value;
        public void Set(NavValue value) => _value = value;
    }

    // Captions deliberately differ from member names — "Blocks" != "Block" is the whole
    // point; a caption equal to the member name would prove nothing about which table
    // Resolve actually consulted.
    private static AlEnumOptionMetadata BuildEnumMetadata()
        => new(
            name: "Test Page Enum Var Kind",
            id: 1928001,
            options: new[] { "Field", "Block", "Image" },
            indexes: new[] { 0, 1, 2 },
            implementations: null,
            captions: new[] { "Fields", "Blocks", "Images" });

    private static RunnerPageInstance BuildPage()
    {
        var ctor = typeof(RunnerPageInstance).GetConstructor(
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            binder: null,
            types: new[]
            {
                typeof(object),
                typeof(object),
                typeof(NavRecord),
                typeof(int),
                typeof(System.Collections.IDictionary)
            },
            modifiers: null)
            ?? throw new InvalidOperationException("RunnerPageInstance private ctor not found.");

        return (RunnerPageInstance)ctor.Invoke(new object?[]
        {
            new object(),
            new object(),
            null,
            1928001,
            new System.Collections.Generic.Dictionary<string, object?>()
        });
    }

    [Fact]
    public void EnumCaptions_EnumBackedMetadata_ReturnsDeclaredCaptionsInMemberOrder()
    {
        var option = NavOption.Create(BuildEnumMetadata(), 0);

        var captions = TestPageOptionValue.EnumCaptions(option);

        Assert.NotNull(captions);
        Assert.Equal(new[] { "Fields", "Blocks", "Images" }, captions);
    }

    // The Option primitive (as opposed to Enum) has its own AL-level OptionCaption property,
    // read elsewhere (RunnerPageInstance.TryGetOptionCaptions's ControlDefinition.OptionCaptionML
    // path) — EnumCaptions must answer null for it, or the two caption sources would collide.
    [Fact]
    public void EnumCaptions_PlainOptionMetadata_ReturnsNull()
    {
        var option = NavOption.Create(NCLOptionMetadata.Create("Field,Block,Image"), 0);

        Assert.Null(TestPageOptionValue.EnumCaptions(option));
    }

    [Fact]
    public void EnumCaptions_NullOption_ReturnsNull()
        => Assert.Null(TestPageOptionValue.EnumCaptions(null));

    // Positive direction of issue #1928: SetValue resolves an Enum control by its declared
    // Caption to the concrete, correct ordinal — not a default, not some other member.
    [Fact]
    public void Resolve_EnumControl_AcceptsTheDeclaredCaption_AndResolvesToTheRightOrdinal()
    {
        var metadata = BuildEnumMetadata();
        var current = NavOption.Create(metadata, 0);
        var captions = TestPageOptionValue.EnumCaptions(current);

        var resolved = Assert.IsType<NavOption>(
            TestPageOptionValue.Resolve(current, "Blocks", captions, "test"));

        Assert.Equal(1, resolved.Value);
    }

    // Negative direction of issue #1928, and the actual decision this issue made: real BC
    // refuses the bare member name for an Enum-typed control (verified against a real service
    // tier — see the file header), so the runner must refuse it too rather than silently
    // diverge. Both the exception type and that the message names the rejected spelling are
    // asserted — a generic catch-all failure would not prove the runner is refusing FOR THE
    // RIGHT REASON.
    [Fact]
    public void Resolve_EnumControl_RejectsTheBareMemberName()
    {
        var metadata = BuildEnumMetadata();
        var current = NavOption.Create(metadata, 0);
        var captions = TestPageOptionValue.EnumCaptions(current);

        var ex = Assert.Throws<RunnerOutOfScopeException>(() =>
        {
            TestPageOptionValue.Resolve(current, "Block", captions, "test");
        });

        Assert.Contains("Block", ex.Message);
        Assert.Contains("Caption", ex.Message);
    }

    // Control: the plain Option primitive's historical member-name fallback is UNCHANGED by
    // this fix — #1928's real-BC evidence is specific to Enum, so narrowing Option's behaviour
    // to match would be an assumption-based change, not something this issue's evidence
    // supports. If this regressed to Enum's stricter rule, this test — not a corpus test —
    // would be the one to catch it, since no real-BC evidence distinguishes the two yet for
    // Option.
    [Fact]
    public void Resolve_PlainOptionControl_StillAcceptsTheBareMemberName()
    {
        var metadata = NCLOptionMetadata.Create("Field,Block,Image");
        var current = NavOption.Create(metadata, 0);

        var resolved = Assert.IsType<NavOption>(
            TestPageOptionValue.Resolve(current, "Block", captions: null, context: "test"));

        Assert.Equal(1, resolved.Value);
    }

    // ── DisplayOrdinal — issue #2367 ────────────────────────────────────────────────
    //
    // The runner-mechanism half of #2367. The BC-behaviour claim ("AssertEquals on an
    // Option/Enum control compares the control's text on both sides, so passing the option
    // value the record holds is a match") is proven upstream against a live service tier —
    // StefanMaron/BusinessCentral.AL.Language.Tests#108, branch
    // agent/impl-3/testpage-option-assertequals, four tests in "Test Page Extended" (60126).
    //
    // What is pinned HERE is our own contract: BC's NavTestField.ALAssertEquals and
    // ALSetValue rebuild an AL option value against NavValueMetadata.DefaultMetadata(FieldType),
    // which throws the field's member table away, and then call ITestField.ValueToString on
    // the resulting bare ordinal. So ValueToString is the one place the control's own option
    // table can be put back, and DisplayOrdinal is what puts it back. Both of the runner's
    // real ITestField implementations (LiveNavTestField, PageVariableTestField) route through
    // it.
    //
    // RED/GREEN: reverting either ValueToString to `Convert.ToString(value, InvariantCulture)`
    // leaves these passing but breaks the upstream tests; deleting DisplayOrdinal's caption/
    // member lookup (returning null unconditionally) fails RendersTheOrdinalAsItsDeclaredCaption
    // and RendersTheOrdinalAsItsMemberName here, in milliseconds.

    [Fact]
    public void DisplayOrdinal_EnumControl_RendersTheOrdinalAsItsDeclaredCaption()
    {
        var current = NavOption.Create(BuildEnumMetadata(), 0);
        var captions = TestPageOptionValue.EnumCaptions(current);

        // 1, not 0: the ordinal must come from the ARGUMENT, not from the value the control
        // happens to be holding. Answering "Fields" here would still look like a caption.
        Assert.Equal("Blocks", TestPageOptionValue.DisplayOrdinal(current, 1, captions));
        Assert.Equal("Images", TestPageOptionValue.DisplayOrdinal(current, 2, captions));
    }

    // The plain Option primitive has no per-value Caption, so its members are what the
    // control shows — the same fallback Display already used for the read direction.
    [Fact]
    public void DisplayOrdinal_PlainOptionControl_RendersTheOrdinalAsItsMemberName()
    {
        var current = NavOption.Create(NCLOptionMetadata.Create("Field,Block,Image"), 0);

        Assert.Equal("Block", TestPageOptionValue.DisplayOrdinal(current, 1, captions: null));
        Assert.Equal("Image", TestPageOptionValue.DisplayOrdinal(current, 2, captions: null));
    }

    // The invariant #2367 was a violation of: the text the write path produces for an ordinal
    // and the text the read path (Display, behind ITestField.Value) produces for the same
    // ordinal must be one and the same. They disagreed — Value said "Blocks", ValueToString
    // said "1" — and AssertEquals compares one against the other.
    [Fact]
    public void DisplayOrdinal_AgreesWithDisplay_ForEveryMemberOfTheSet()
    {
        var metadata = BuildEnumMetadata();
        var current = NavOption.Create(metadata, 0);
        var captions = TestPageOptionValue.EnumCaptions(current);
        var expected = new[] { "Fields", "Blocks", "Images" };

        foreach (var ordinal in new[] { 0, 1, 2 })
        {
            var read = TestPageOptionValue.Display(NavOption.Create(metadata, ordinal), captions);
            var write = TestPageOptionValue.DisplayOrdinal(current, ordinal, captions);

            // Naming the concrete text matters: comparing the two paths to each other alone
            // would still hold if BOTH collapsed to null, which is exactly the no-op an
            // agreement-only assertion cannot distinguish from a working one.
            Assert.Equal(expected[ordinal], read);
            Assert.Equal(expected[ordinal], write);
        }
    }

    // A NavOption that still carries its own metadata is accepted too — ALSetValue's
    // CreateNavValueFromObject can hand back a NavOption rather than a boxed int.
    [Fact]
    public void DisplayOrdinal_NavOptionArgument_RendersThatOptionsOrdinal()
    {
        var metadata = BuildEnumMetadata();
        var current = NavOption.Create(metadata, 0);
        var captions = TestPageOptionValue.EnumCaptions(current);

        Assert.Equal("Blocks",
            TestPageOptionValue.DisplayOrdinal(current, NavOption.Create(metadata, 1), captions));
    }

    // Null means "not mine — use your own Convert.ToString", which is what keeps this fix
    // additive. Three ways to land there, each asserted rather than assumed:

    [Fact]
    public void DisplayOrdinal_ControlIsNotOptionBound_ReturnsNull()
        => Assert.Null(TestPageOptionValue.DisplayOrdinal(null, 1, captions: null));

    [Fact]
    public void DisplayOrdinal_OrdinalOutsideTheOptionSet_ReturnsNull()
    {
        var current = NavOption.Create(NCLOptionMetadata.Create("Field,Block,Image"), 0);

        Assert.Null(TestPageOptionValue.DisplayOrdinal(current, 7, captions: null));
    }

    // Deliberately narrow: a string is NOT reinterpreted as an option. ALSetValue's
    // `value is NavStringValue` fast path never reaches ValueToString, so a string arriving
    // would mean some other caller with an unexamined contract.
    [Fact]
    public void DisplayOrdinal_NonOrdinalArgument_ReturnsNull()
    {
        var current = NavOption.Create(NCLOptionMetadata.Create("Field,Block,Image"), 0);

        Assert.Null(TestPageOptionValue.DisplayOrdinal(current, "1", captions: null));
        Assert.Null(TestPageOptionValue.DisplayOrdinal(current, null, captions: null));
    }

    [Fact]
    public void PageVariableTestField_EnumControl_AnswersTheBoundOptionsCaptions()
    {
        var metadata = BuildEnumMetadata();
        var field = new PageVariableTestField(
            BuildPage(),
            new FakeExpression("Kind", NavOption.Create(metadata, 0)),
            controlId: 50100);

        Assert.Equal(3, field.OptionCount);
        Assert.Equal("Fields", field.GetOption(0));
        Assert.Equal("Blocks", field.GetOption(1));
        Assert.Equal("Images", field.GetOption(2));
    }

    [Fact]
    public void PageVariableTestField_EnumControl_OutOfRangeMemberLookupReturnsEmpty()
    {
        var metadata = BuildEnumMetadata();
        var field = new PageVariableTestField(
            BuildPage(),
            new FakeExpression("Kind", NavOption.Create(metadata, 0)),
            controlId: 50100);

        Assert.Equal(string.Empty, field.GetOption(-1));
        Assert.Equal(string.Empty, field.GetOption(7));
    }
}

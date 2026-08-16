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
}

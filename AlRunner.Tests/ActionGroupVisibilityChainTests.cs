// ActionGroupVisibilityChainTests — contract tests for
// AlRunner.Patches.RunnerPageInstance.ActionGroupsIn (issues #3689 / #3339).
//
// NOT a claim about what Business Central does. That claim is measured upstream, by corpus
// codeunit 60583 "TPAR Tests" (StefanMaron/BusinessCentral.AL.Language.Tests#313), on a real
// service tier: an action inside `group(G) { Visible = false; }` answers Visible() = false,
// answers Enabled() = true, and is still invokable.
//
// What this file pins is OUR OWN half of that fix: which elements of an ancestor path carry an
// action group's Visible. ActionVisible walks the path BC's own
// MasterPage.FindControlBaseDefinition hands back, and that path mixes element kinds — the
// content area, the command bar, action CONTAINERS (AL's `area(Processing)`), control groups
// and action groups. Only an action group is one an AL author can give a Visible, so only an
// action group may participate. The live route needs a NavForm over a real compiled page's
// metadata, which only the page-build pipeline produces, so this seam is where the decision is
// testable in milliseconds.
//
// RED/GREEN, both directions:
//   * reducing ActionGroupsIn to `Array.Empty<...>()` — the pre-fix behaviour, where an
//     action's own Visible was the whole answer — fails PicksActionGroups and
//     PicksEveryActionGroupOnTheWay;
//   * widening it to return the path unfiltered fails IgnoresElementsThatCarryNoActionGroupVisible,
//     which is the over-reach a fix chasing "anything above a hidden group" would produce.
using System.Collections.Generic;
using System.Linq;
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Types.Metadata;
using Xunit;

namespace AlRunner.Tests;

public sealed class ActionGroupVisibilityChainTests
{
    private static ActionGroupDefinition Group(int id, string? visible)
        => new() { ID = id, Visible = visible };

    // Positive: an action group in the path is picked up, and its declared Visible travels
    // with it — the caller reads .Visible off what this returns, so returning the element and
    // losing the property would be a silent no-op.
    [Fact]
    public void PicksActionGroups()
    {
        var group = Group(42, "false");
        var path = new List<ElementDefinition> { group, new ContentAreaDefinition() };

        var picked = RunnerPageInstance.ActionGroupsIn(path).ToList();

        Assert.Single(picked);
        Assert.Equal(42, picked[0].ID);
        Assert.Equal("false", picked[0].Visible);
    }

    // Positive: nesting. A group two levels deep must follow the OUTER group too, so every
    // action group on the path is returned rather than only the nearest one.
    [Fact]
    public void PicksEveryActionGroupOnTheWay()
    {
        var path = new List<ElementDefinition>
        {
            Group(7, null),          // the immediate group declares nothing
            Group(8, "false"),       // ...and the one enclosing it is the hidden one
            new ContentAreaDefinition(),
        };

        var picked = RunnerPageInstance.ActionGroupsIn(path).ToList();

        Assert.Equal(new[] { 7, 8 }, picked.Select(g => g.ID).ToArray());

        // "true" rather than null on the first: ActionGroupDefinition.Visible normalises an
        // unset property to the literal "true", which is the AL default and what
        // EvaluateProperty already reads as visible. Pinned because it is not obvious from the
        // constructor, and a walk that treated an unset Visible as "declared nothing, skip"
        // would behave the same only by accident.
        Assert.Equal(new string?[] { "true", "false" }, picked.Select(g => g.Visible).ToArray());
    }

    // Negative, and the one that bounds the fix: nothing else on the path participates. An
    // action CONTAINER is AL's `area(Processing)`, which carries no author-written Visible, and
    // neither does the content area or a layout control group. Folding them in would invent a
    // rule no measurement supports.
    [Fact]
    public void IgnoresElementsThatCarryNoActionGroupVisible()
    {
        var path = new List<ElementDefinition>
        {
            new ActionContainerDefinition { ID = 1 },
            new ContentAreaDefinition(),
            new ControlGroupDefinition { ID = 2, Visible = "false" },
        };

        Assert.Empty(RunnerPageInstance.ActionGroupsIn(path));
    }

    // Negative: an action that is not inside any group has an empty chain, so ActionVisible
    // falls through to the action's own declaration alone — the behaviour every arm of the
    // corpus suite outside a group depends on.
    [Fact]
    public void AnEmptyPathYieldsNoGroups()
    {
        Assert.Empty(RunnerPageInstance.ActionGroupsIn(new List<ElementDefinition>()));
    }

    // Negative: FindControlBaseDefinition answering null (an id that names no element) reaches
    // here as a null path. It must not throw — a page whose metadata the runner could not
    // resolve has to fall back to the action's own Visible, not fail the read.
    [Fact]
    public void ANullPathYieldsNoGroups()
    {
        Assert.Empty(RunnerPageInstance.ActionGroupsIn(null));
    }
}

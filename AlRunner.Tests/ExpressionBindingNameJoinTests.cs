// ExpressionBindingNameJoinTests — the runner-side mechanism behind issue #3825.
//
// THE DEFECT
// ----------
// A PRECOMPILED page states its control/action properties in SymbolReference.json, which carries
// the identifier the AL author wrote (`Enabled = PageEditable`). The binding table BC fills at
// runtime — NavForm.SourceExpressions, populated by the .app's own RegisterSourceExpression IL —
// is keyed by the COMPILER's spelling. RunnerPageInstance looked the declared identifier up as a
// key and nothing else, so every expression-bound property on a precompiled page missed and was
// refused, naming the expression (`'PageEditable' is not a name the page publishes a binding for`).
//
// A source-compiled page never hit this: its declarations and its binding table both come from
// the same emitted metadata, so the key lookup hits.
//
// WHAT THE .app ACTUALLY SHIPS, MEASURED
// --------------------------------------
// Reading Base Application 28.1.49838.53910's five R2R assemblies with Cecil — no compile —
// the (id, name) argument pairs passed to RegisterSourceExpression are all present:
//
//     pages 2,610   pages registering bindings 1,847   registered pairs 18,075
//       p<id>p<id>* 6,126     Control<hash>* 11,595     other 354
//
// and Page790.OnMetadataLoaded carries exactly:
//
//     RegisterSourceExpression("p790p790PageEditable" , "p790p790PageEditable")
//     RegisterSourceExpression("Control159866013"     , "GLAccTotaling")
//     RegisterSourceExpression("Control1520916075"    , "Rec.GetBalance()")
//
// That splits the join into the two arms this file pins, and the split is the whole finding:
//
//   * a VALUE-SOURCE binding carries the raw identifier in `Name`, so it is READ off the object;
//   * a PROPERTY binding registers the mangled spelling as BOTH arguments — the raw identifier is
//     nowhere on the object — so it is reached by composing the candidate key and asking the
//     table whether it EXISTS.
//
// All 6,126 of the p<id>p<id> registrations have id == name (0 counterexamples), and no page
// registers a duplicate key (0 of 2,610), so a hit is unambiguous.
//
// WHY THE SECOND ARM IS NOT "RE-IMPLEMENTING THE MANGLING"
// --------------------------------------------------------
// It never derives a value. It derives a CANDIDATE and requires the table to contain it, so a
// wrong guess resolves nothing and the caller refuses exactly as before. That is the difference
// between reading Microsoft's own registration and owning a naming rule across every BC version
// (precompiled-dll-respect.md § "Reuse before you re-implement"), and it is why the negative
// arms below matter more than the positive ones.
//
// WHAT IS DELIBERATELY NOT HERE
// -----------------------------
// Whether real BC answers true or false for a given page's property is a claim about BC and is
// not pinned here; the AL arms in tests/runner-extras/testpage-precompiled-member-names drive the
// resolution end to end against Base Application page 790.
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

public class ExpressionBindingNameJoinTests
{
    // A stand-in for NavFormSourceExpression carrying the two properties the join reads. The
    // runner resolves both by NAME through BcShape rather than against a BC type, so a shape with
    // the same members exercises the real lookup path.
    private sealed class FakeBinding
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
    }

    // Page 790's real registrations, exactly as the Cecil trace above reports them.
    private static System.Collections.IDictionary Page790Bindings() => new System.Collections.Hashtable
    {
        ["p790p790PageEditable"] = new FakeBinding { Id = "p790p790PageEditable", Name = "p790p790PageEditable" },
        ["Control159866013"] = new FakeBinding { Id = "Control159866013", Name = "GLAccTotaling" },
        ["Control1520916075"] = new FakeBinding { Id = "Control1520916075", Name = "Rec.GetBalance()" },
    };

    [Fact]
    public void TheIndexKeysEveryBindingByItsName_NotByItsRegisteredKey()
    {
        var byName = RunnerPageInstance.BuildBindingsByName(Page790Bindings());

        // The value-source bindings are reachable by the raw AL identifier, which is the half of
        // the join that needs no composed key at all.
        Assert.True(byName.ContainsKey("GLAccTotaling"));
        Assert.True(byName.ContainsKey("Rec.GetBalance()"));

        // And the property binding is indexed under the only Name it has — the mangled one. This
        // is the measured asymmetry: `PageEditable` is NOT a key here, which is exactly why the
        // second arm of the join exists.
        Assert.True(byName.ContainsKey("p790p790PageEditable"));
        Assert.False(byName.ContainsKey("PageEditable"));

        Assert.Equal(3, byName.Count);
    }

    [Fact]
    public void ABindingWhoseNameIsMissingOrBlankIsLeftOutRatherThanCostingTheWholeIndex()
    {
        var bindings = new System.Collections.Hashtable
        {
            ["Control1"] = new FakeBinding { Id = "Control1", Name = "Good" },
            ["Control2"] = new FakeBinding { Id = "Control2", Name = "" },
            ["Control3"] = new object(),   // no Name property at all
            ["Control4"] = null!,
        };

        var byName = RunnerPageInstance.BuildBindingsByName(bindings);

        // The readable one survives; the three unreadable ones are simply absent. An unreadable
        // binding must not cost the page every other binding — the caller's key lookup still
        // works for those, and an identifier that needed the join refuses loudly, naming itself.
        Assert.True(byName.ContainsKey("Good"));
        Assert.Single(byName);
    }

    [Fact]
    public void TheIndexIsCaseSensitive_SoTwoGlobalsDifferingOnlyInCaseDoNotResolveToEachOther()
    {
        var byName = RunnerPageInstance.BuildBindingsByName(new System.Collections.Hashtable
        {
            ["Control1"] = new FakeBinding { Id = "Control1", Name = "Flag" },
        });

        Assert.True(byName.ContainsKey("Flag"));
        Assert.False(byName.ContainsKey("flag"));
        Assert.False(byName.ContainsKey("FLAG"));
    }

    // The one-to-many shape, which is normal rather than exceptional: one global bound to two
    // controls registers twice under a single Name. The index must answer rather than throw.
    [Fact]
    public void TwoBindingsSharingOneNameResolveToOneEntryRatherThanThrowing()
    {
        var byName = RunnerPageInstance.BuildBindingsByName(new System.Collections.Hashtable
        {
            ["Control1"] = new FakeBinding { Id = "Control1", Name = "Shared" },
            ["Control2"] = new FakeBinding { Id = "Control2", Name = "Shared" },
        });

        Assert.True(byName.ContainsKey("Shared"));
        Assert.Single(byName);
    }

    [Fact]
    public void AnEmptyBindingTableProducesAnEmptyIndexRatherThanFailing()
    {
        Assert.Empty(RunnerPageInstance.BuildBindingsByName(new System.Collections.Hashtable()));
    }
}

// TableRelationUnrepresentableShapeRefusalTests — issue #3326.
//
// WHAT IS PINNED
// --------------
// The PARSER's two TableRelation refusal sites used to answer `null` — dropping the whole
// relation — and carry on. #3306 fixed the same end state one layer down in the BUILDER; these
// two sites were left, and they are strictly worse placed, because `RelationArms = null` never
// reaches the builder at all, so #3306's guard at `EvaluateRelation` cannot see it.
//
// A dropped relation leaves `FieldRef.Relation` answering 0 and `Validate` accepting a value
// with no row in the related table — both silent wrong answers in the direction that looks like
// success, which `.claude/rules/loud-failures.md` forbids.
//
// WHY A THROW AND NOT A NOTE (the design question #3326 asked to be settled by measurement)
// ----------------------------------------------------------------------------------------
// #3326 offered two routes and asked which the evidence supports. Measured on this tree:
//
//   * The condition-shape site is reachable ONLY from AL that the AL compiler rejects, and
//     the runner refuses to run that AL. Both reaching shapes are proof of this:
//       - `SimpleFieldExpressionSyntax` in an if() — `error AL0489: The property expression is
//         not valid. A CONST or FILTER expression is expected.`
//       - `InvalidPropertyExpressionSyntax` — the AL parser's ERROR-RECOVERY node, which by
//         construction exists only where the parse already failed.
//     A bundle carrying either fails to compile and runs zero tests; the same shape in a
//     DEPENDENCY is refused with "source dependency does not compile".
//   * The whole al-language corpus (2978 tests) and all of tests/runner-extras (361 tests)
//     produced ZERO hits at either site with both instrumented to print unconditionally.
//
// So there is no AL-observable state to record a note FOR: no AL that reaches these sites ever
// executes. A note keyed on (tableId, fieldId) would be bookkeeping for a state that cannot be
// observed — which is what #3326 warned against building. What IS worth having is that the
// runner stops treating an unrepresentable shape as a silent success, because reaching either
// site means the runner's own model of AL has diverged from the compiler's.
//
// That makes this the `NclCecilRewrite` case: a shape the runner believes cannot occur, which
// throws loudly if it ever does, rather than degrading into a wrong answer.
//
// WHY THE RELATED-TABLE-NAME SITE IS GONE ENTIRELY RATHER THAN CONVERTED
// ---------------------------------------------------------------------
// `ParseRelationArms`' `parts.Count is not (1 or 2)` guard cannot fire, and that is structural
// rather than a matter of what AL anyone has written:
//
//   * `RelationTargetNameParts` returns `parts.Count <= 2 ? parts : parts.GetRange(...2)`, so
//     its result is 0, 1 or 2 — never more. The comment's "3-part name" has been impossible
//     since #2851 clamped it.
//   * 0 is unproducible. `NameSyntax` has exactly three subclasses in
//     Microsoft.Dynamics.Nav.CodeAnalysis 28.1 — `SimpleNameSyntax` (abstract),
//     `IdentifierNameSyntax` and `QualifiedNameSyntax` — and `NameParts` walks both concrete
//     ones, appending at least one part for each. AL's error recovery synthesises a MISSING
//     `IdentifierNameSyntax` rather than nothing, so even unparseable input yields 1 part with
//     empty text.
//
// Fifteen probes covering three-, four- and five-part names, a quoted name containing a dot, an
// empty quoted name, a leading/trailing/bare dot, an integer literal, a parenthesized name, a
// keyword, a string literal and an absent value all returned 1 or 2 parts. `EmptyRelationTarget`
// below pins the one thing that actually matters downstream — an empty target NAME is carried
// as 1 part and refused by the builder, which is #3306's business, not the parser's.
//
// BC-BEHAVIOUR ANSWER
// -------------------
// None of this asserts anything about BC. Real BC never sees these shapes: its own compiler
// rejects them, exactly as the runner's does, because both ARE Microsoft's AL compiler. The
// claim is purely "the runner must not silently drop a relation whose shape it cannot carry",
// which is a runner-local claim about a runner-side surface. There is nothing for a service
// tier to adjudicate, so no corpus test is owed. The BC-behaviour half — that `Validate`
// enforces a TableRelation at all — is already pinned upstream (corpus codeunit 60482
// TestFieldRefRelation and corpus PRs 207/222), and this file deliberately does not duplicate it.
using System.Reflection;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

public sealed class TableRelationUnrepresentableShapeRefusalTests
{
    /// <summary>
    /// Drive <c>RelationConditionList</c> — the site that used to print and return null — over a
    /// real AL syntax tree, so the shapes under test are the ones Microsoft's parser actually
    /// produces rather than ones this test asserts into existence.
    /// </summary>
    private static object? InvokeRelationConditionList(
        Microsoft.Dynamics.Nav.CodeAnalysis.Syntax.TableFilterExpressionSyntax? filter,
        string fieldName, bool allowFieldLinks, bool fromCompiledSource = true)
    {
        var m = typeof(RecordPatches).GetMethod(
                    "RelationConditionList",
                    BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "RecordPatches.RelationConditionList not found — this test drives the "
                    + "parser's condition-shape refusal site directly.");
        try
        {
            return m.Invoke(null,
                new object?[] { filter, fieldName, allowFieldLinks, fromCompiledSource });
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo
                .Capture(tie.InnerException).Throw();
            throw; // unreachable
        }
    }

    /// <summary>
    /// The <c>TableRelation</c> property value of the single field of a one-field table, parsed
    /// out of real AL text by the AL compiler's own parser.
    /// </summary>
    private static Microsoft.Dynamics.Nav.CodeAnalysis.Syntax.TableRelationPropertyValueSyntax
        ParseRelation(string relationText)
    {
        var src = $$"""
            table 50100 "URS Probe"
            {
                fields
                {
                    field(1; "Code"; Code[20]) { }
                    field(2; "Kind"; Code[20]) { }
                    field(3; "Link"; Code[20]) { TableRelation = {{relationText}}; }
                }
            }
            """;
        var m = typeof(RecordPatches).GetMethod(
                    "ParseAlObjects", BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException("RecordPatches.ParseAlObjects not found.");
        var objects = (System.Collections.IEnumerable)m.Invoke(null, new object?[] { src })!;

        foreach (var obj in objects)
        {
            if (obj is not Microsoft.Dynamics.Nav.CodeAnalysis.Syntax.TableSyntax t) continue;
            if (t.Fields == null) continue;
            foreach (var f in t.Fields.Fields)
            {
                if (f is not Microsoft.Dynamics.Nav.CodeAnalysis.Syntax.FieldSyntax fs) continue;
                if (fs.PropertyList == null) continue;
                foreach (var p in fs.PropertyList.Properties)
                    if (p is Microsoft.Dynamics.Nav.CodeAnalysis.Syntax.PropertySyntax ps
                        && string.Equals(ps.Name?.ToString()?.Trim(), "TableRelation",
                            StringComparison.OrdinalIgnoreCase)
                        && ps.Value is Microsoft.Dynamics.Nav.CodeAnalysis.Syntax
                            .TableRelationPropertyValueSyntax tr)
                        return tr;
            }
        }
        throw new InvalidOperationException(
            $"no TableRelation property parsed out of '{relationText}'");
    }

    [Fact]
    public void IfConditionCarryingAFieldLink_ThrowsNamingTheShapeAndTheField()
    {
        // The one shape #3326 could construct, and the reason the site exists at all: BC models
        // an if() condition as MetaCondition, whose NCLMetaFilter.CreateFromMetaCondition has
        // CONST and FILTER cases only. Microsoft's own compiler rejects it first with AL0489 —
        // which is exactly why a throw is right and a note would be unobservable bookkeeping.
        var tr = ParseRelation("""if ("Kind" = field("Code")) "URS Parent" """);
        var cond = tr.IfExpression?.IfTableRelationCondition;
        Assert.NotNull(cond);
        // Pin the PRECONDITION rather than assuming it: this really is the field-link node, so
        // the test is exercising the refusal and not some unrelated parse failure.
        Assert.Contains(cond!.Conditions, c =>
            c is Microsoft.Dynamics.Nav.CodeAnalysis.Syntax.SimpleFieldExpressionSyntax);

        var ex = Assert.Throws<InvalidOperationException>(
            () => InvokeRelationConditionList(cond, "Link", allowFieldLinks: false));

        // The message must name WHICH field, WHICH position and WHICH syntax shape — without all
        // three it is no more actionable than the silent null it replaces.
        Assert.Contains("Link", ex.Message);
        Assert.Contains("if()", ex.Message);
        Assert.Contains("SimpleFieldExpressionSyntax", ex.Message);
        // ...and it must say the runner believes this cannot happen, so whoever sees it knows
        // this is a runner-model divergence rather than bad AL to be fixed in the test.
        Assert.Contains("AL0489", ex.Message);
        Assert.Contains("#3326", ex.Message);
    }

    [Fact]
    public void ErrorRecoveryConditionNode_ThrowsNamingTheShape()
    {
        // The OTHER reaching shape, and the more general one: InvalidPropertyExpressionSyntax is
        // the AL parser's error-recovery node. It appears only where the parse already failed, so
        // a bundle carrying it never compiles — but if the runner ever meets one it must say so
        // rather than drop the relation and answer 0.
        var tr = ParseRelation("""Customer where("No." = bogus("X"))""");
        var filt = tr.TableFilter?.Filter;
        Assert.NotNull(filt);
        Assert.Contains(filt!.Conditions, c =>
            c is Microsoft.Dynamics.Nav.CodeAnalysis.Syntax.InvalidPropertyExpressionSyntax);

        var ex = Assert.Throws<InvalidOperationException>(
            () => InvokeRelationConditionList(filt, "Link", allowFieldLinks: true));

        Assert.Contains("Link", ex.Message);
        Assert.Contains("where()", ex.Message);
        Assert.Contains("InvalidPropertyExpressionSyntax", ex.Message);
    }

    [Fact]
    public void EveryShapeTheCompilerAccepts_IsCarriedAndNotRefused()
    {
        // The scoping control, and the half that must pass in BOTH states. These six are every
        // condition shape Microsoft's binder accepts — verified by compiling them through the
        // runner, which drives Microsoft's own compiler: 1P/0F/0E, no refusal. If the throw above
        // were keyed on anything coarser than the unrepresentable node types, this fails.
        //
        // const/filter are legal in both positions; the four field() spellings only in where().
        foreach (var (relation, inWhere) in new (string, bool)[]
        {
            ("""if ("Kind" = const('A')) Customer""", false),
            ("""if ("Kind" = filter('A'|'B')) Customer""", false),
            ("""Customer where("No." = const('A'))""", true),
            ("""Customer where("No." = filter('A'|'B'))""", true),
            ("""Customer where("No." = field("Code"))""", true),
            ("""Customer where("No." = field(filter("Code")))""", true),
            ("""Customer where("No." = field(upperlimit("Code")))""", true),
            ("""Customer where("No." = field(upperlimit(filter("Code"))))""", true),
        })
        {
            var tr = ParseRelation(relation);
            var filt = inWhere ? tr.TableFilter?.Filter : tr.IfExpression?.IfTableRelationCondition;
            Assert.True(filt != null, $"no condition list parsed out of '{relation}'");

            var carried = InvokeRelationConditionList(filt, "Link", allowFieldLinks: inWhere);

            // Not merely "did not throw": the list must actually carry the condition. A method
            // that returned an empty list for everything would pass a no-throw assertion.
            Assert.NotNull(carried);
            var count = (int)carried!.GetType().GetProperty("Count")!.GetValue(carried)!;
            Assert.Equal(1, count);
        }
    }

    [Fact]
    public void SameShapeFromAPrecompiledAppSymbol_RefusesQuietlyInsteadOfThrowing()
    {
        // The direction that decides the whole design, and the one an unconditional throw got
        // wrong. `TryParseRelationArmsText` feeds a STRING out of a precompiled .app's
        // SymbolReference.json through the same parser. Nothing vetted that string — Microsoft's
        // compiler never saw it — and an if() field() link really is unrepresentable there,
        // because BC's own NCLMetaFilter.CreateFromMetaCondition has CONST and FILTER cases only
        // and throws NotSupportedException on FIELD. So refusing the PROPERTY is BC's constraint
        // rather than a runner bug, and null is the correct permanent answer.
        //
        // Measured on Base Application 28.1.49838.53910: 1,155 of 7,787 relation-bearing fields
        // carry a where(... = field(...)) link, so this path is live, not hypothetical.
        var m = typeof(RecordPatches).GetMethod(
                    "TryParseRelationArmsText", BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "RecordPatches.TryParseRelationArmsText not found — this test drives the "
                    + "precompiled-.app symbol path.");

        var arms = m.Invoke(null, new object?[]
        {
            """if ("Kind" = field("Kind")) "URS Parent"."Code" """, "Link",
        });

        // Null, not an exception: the caller's documented contract is to read this as
        // "no relation". A throw here aborts reading the whole .app.
        Assert.Null(arms);
    }

    [Fact]
    public void RepresentableShapeFromAPrecompiledAppSymbol_IsStillCarried()
    {
        // The scoping half of the pair. If the symbol path had been made to refuse everything —
        // or if the provenance flag were wired backwards — this fails. A `where()` field() link
        // IS representable (BC models it as MetaFilter/FilterType.FIELD), so it must come back
        // carried, with the arm naming its target concretely.
        var m = typeof(RecordPatches).GetMethod(
                    "TryParseRelationArmsText", BindingFlags.NonPublic | BindingFlags.Static)!;

        var arms = m.Invoke(null, new object?[]
        {
            "\"URS Parent\".\"Code\" where(\"Group\" = field(\"Kind\"))", "Link",
        });

        Assert.NotNull(arms);
        var list = (System.Collections.IList)arms!;
        Assert.Single(list);

        // Assert the CONTENT, not just that something came back: a method returning one empty
        // arm for everything would pass a count-only check.
        var arm = list[0]!;
        var target = arm.GetType().GetProperty("TableName")!.GetValue(arm);
        Assert.Equal("URS Parent", target);
        var filters = (System.Collections.ICollection)arm.GetType()
            .GetProperty("Filters")!.GetValue(arm)!;
        Assert.Single(filters);
    }

    [Fact]
    public void EmptyRelationTargetName_IsCarriedAsOnePartNotRefusedByTheParser()
    {
        // Why the related-table-name site is deleted rather than converted to a throw: its guard
        // cannot fire. `""` is the closest thing to an empty target AL admits, and it arrives as
        // ONE part whose text is empty — not zero parts. Refusing an empty NAME is the builder's
        // job (#3306), which is where it is already done, and pinning that here keeps the
        // deletion honest: the parser is not silently dropping this case, it never had it.
        var m = typeof(RecordPatches).GetMethod(
                    "RelationTargetNameParts", BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "RecordPatches.RelationTargetNameParts not found.");

        foreach (var (relation, expected) in new (string, int)[]
        {
            ("""Customer""", 1),                                   // plain
            ("""Customer."No." """, 2),                            // table + field
            ("""Microsoft.Sales.Customer."No." """, 2),            // namespace-qualified, clamped
            ("""A.B.C.D.E""", 2),                                  // five parts, clamped
            ("\"\"", 1),                                          // empty quoted name -> 1 part
        })
        {
            var tr = ParseRelation(relation);
            var parts = (System.Collections.ICollection)m.Invoke(
                null, new object?[] { tr.RelatedTableField })!;
            Assert.Equal(expected, parts.Count);
            // The property the deleted guard depended on, asserted directly: never 0, never >2.
            Assert.InRange(parts.Count, 1, 2);
        }
    }
}

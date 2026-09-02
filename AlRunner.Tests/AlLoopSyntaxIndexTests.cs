// Issue #2056: `iterationTracking`, the STATIC half. AlLoopSyntaxIndex reads loop
// statements out of BC's own AL syntax tree (kind, loop variable, header and body
// ranges, nesting), and AlLoopScopeTable.Build resolves them against a compiled scope's
// [SourceSpans] table into the header/body/marker id sets AlIterationSegmenter consumes.
//
// The span tables used below are NOT invented: each `Measured*` fixture is the
// statement table al-runner itself reports (`coverage:true`) for the AL source embedded
// next to it, transcribed to 0-based positions. That is what makes these tests prove
// the classification against BC's real instrumentation rather than against a guess.
//
// Parsing needs Microsoft.Dynamics.Nav.CodeAnalysis.dll: the BcEngineCollection
// fixture makes it resolvable in-process and reports Skipped (never Passed) when the
// BC artifacts are not provisioned. The pure Build tests at the end need no engine.
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class AlLoopSyntaxIndexTests
{
    private readonly BcEngineFixture _engine;

    public AlLoopSyntaxIndexTests(BcEngineFixture engine) => _engine = engine;

    private void RequireEngine() =>
        TestArtifacts.SkipIf(!_engine.Ready, _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

    // ---------------------------------------------------------------------------------
    // Fixture 1: one procedure per loop shape. Line numbers matter, see Measured*.
    // ---------------------------------------------------------------------------------
    internal const string LoopShapesSource = """
codeunit 60300 LoopShapes
{
    trigger OnRun()
    begin
        ForTo();
        ForDownto();
        WhileDo();
        RepeatUntil();
        Nested();
        WithBreak();
        WithExit();
        ZeroIter();
        Message('DONE');
    end;

    local procedure ForTo()
    var
        i: Integer;
        t: Integer;
    begin
        t := 0;
        for i := 1 to 3 do
            t := t + i;
        t := t * 10;
    end;

    local procedure ForDownto()
    var
        i: Integer;
        t: Integer;
    begin
        for i := 3 downto 1 do begin
            t := t + i;
        end;
    end;

    local procedure WhileDo()
    var
        n: Integer;
    begin
        n := 3;
        while n > 0 do begin
            n := n - 1;
        end;
        n := 99;
    end;

    local procedure RepeatUntil()
    var
        n: Integer;
    begin
        n := 0;
        repeat
            n := n + 1;
        until n >= 3;
        n := 99;
    end;

    local procedure Nested()
    var
        i: Integer;
        j: Integer;
        s: Integer;
    begin
        for i := 1 to 2 do begin
            s := s + 100;
            for j := 1 to 2 do
                s := s + j;
            s := s + 1000;
        end;
    end;

    local procedure WithBreak()
    var
        i: Integer;
        s: Integer;
    begin
        for i := 1 to 10 do begin
            s := s + i;
            if i = 2 then
                break;
        end;
        s := -1;
    end;

    local procedure WithExit()
    var
        i: Integer;
        s: Integer;
    begin
        for i := 1 to 10 do begin
            s := s + i;
            if i = 2 then
                exit;
        end;
        s := -1;
    end;

    local procedure ZeroIter()
    var
        i: Integer;
        s: Integer;
    begin
        for i := 1 to 0 do
            s := s + 1;
        s := 7;
    end;
}
""";

    // ---------------------------------------------------------------------------------
    // Fixture 2: nested-first-statement shapes and foreach. See Measured*.
    // ---------------------------------------------------------------------------------
    internal const string LoopShapes2Source = """
codeunit 60301 LoopShapes2
{
    trigger OnRun()
    begin
        ForEachList();
        IfFirst();
        WhileFirst();
        CallsLooper();
        ForBlockLastAssign();
        CaseInBody();
    end;

    local procedure ForEachList()
    var
        l: List of [Integer];
        v: Integer;
        s: Integer;
    begin
        l.Add(5);
        l.Add(6);
        foreach v in l do
            s := s + v;
        s := 99;
    end;

    local procedure IfFirst()
    var
        i: Integer;
        s: Integer;
    begin
        for i := 1 to 3 do begin
            if i = 2 then
                s := s + 10;
            s := s + 1;
        end;
    end;

    local procedure WhileFirst()
    var
        i: Integer;
        n: Integer;
        s: Integer;
    begin
        for i := 1 to 2 do begin
            while n < 2 do
                n := n + 1;
            n := 0;
            s := s + 1;
        end;
    end;

    local procedure Inner(): Integer
    var
        k: Integer;
        s: Integer;
    begin
        for k := 1 to 2 do
            s := s + k;
        exit(s);
    end;

    local procedure CallsLooper()
    var
        i: Integer;
        s: Integer;
    begin
        for i := 1 to 2 do
            s := s + Inner();
    end;

    local procedure ForBlockLastAssign()
    var
        i: Integer;
        s: Integer;
    begin
        for i := 1 to 2 do begin
            s := s + i;
        end;
        s := s * 2;
    end;

    local procedure CaseInBody()
    var
        i: Integer;
        s: Integer;
    begin
        for i := 1 to 2 do
            case i of
                1:
                    s := 10;
                else
                    s := 20;
            end;
    end;
}
""";

    /// <summary>Builds a [SourceSpans]-shaped table from 1-based (line, col, endLine, endCol)
    /// entries as al-runner's statement table prints them; index = statement id.</summary>
    private static long[] Spans(params (int L1, int C1, int L2, int C2)[] s) =>
        s.Select(x => AlSourceSpanCodec.Encode(x.L1 - 1, x.C1 - 1, x.L2 - 1, x.C2 - 1)).ToArray();

    // Measured with `coverage:true` against LoopShapesSource (al-runner 2.10.0, BC 28.1).
    private static readonly long[] MeasuredForTo       = Spans((21, 9, 21, 16), (22, 9, 23, 24), (23, 13, 23, 24), (24, 9, 24, 21));
    private static readonly long[] MeasuredWhileDo     = Spans((41, 9, 41, 16), (42, 15, 42, 20), (43, 13, 43, 24), (44, 9, 44, 12), (45, 9, 45, 17));
    private static readonly long[] MeasuredRepeatUntil = Spans((52, 9, 52, 16), (54, 13, 54, 24), (55, 15, 55, 21), (56, 9, 56, 17));
    private static readonly long[] MeasuredNested      = Spans((65, 9, 70, 13), (66, 13, 66, 26), (67, 13, 68, 28), (68, 17, 68, 28), (69, 13, 69, 27), (70, 9, 70, 12));
    private static readonly long[] MeasuredZeroIter    = Spans((104, 9, 105, 24), (105, 13, 105, 24), (106, 9, 106, 16));
    // Measured against LoopShapes2Source.
    private static readonly long[] MeasuredForEachList = Spans((19, 9, 19, 18), (20, 9, 20, 18), (21, 9, 22, 24), (22, 13, 22, 24), (23, 9, 23, 17));
    private static readonly long[] MeasuredIfFirst     = Spans((31, 9, 35, 13), (32, 16, 32, 21), (33, 17, 33, 29), (34, 13, 34, 24), (35, 9, 35, 12));
    private static readonly long[] MeasuredWhileFirst  = Spans((44, 9, 49, 13), (45, 19, 45, 24), (46, 17, 46, 28), (47, 13, 47, 20), (48, 13, 48, 24), (49, 9, 49, 12));

    private static AlLoopMember Member(IReadOnlyList<AlLoopMember> members, string name) =>
        Assert.Single(members, m => m.Name == name);

    // --- parsing: what the syntax walk reports ---------------------------------------

    [SkippableFact]
    public void Parse_ForWithSingleStatementBody_KindVariableHeaderAndBody()
    {
        RequireEngine();
        var members = AlLoopSyntaxIndex.Parse(LoopShapesSource, "LoopShapes.al");
        var m = Member(members, "ForTo");

        var site = Assert.Single(m.Sites);
        Assert.Equal(AlLoopKind.For, site.Kind);
        Assert.Equal("i", site.LoopVariable);
        Assert.Equal(new AlTextPosition(21, 8), site.Range.Start);      // `for` keyword, 0-based
        Assert.Equal(22, site.Range.End.Line);                            // ends on the body's line
        var header = Assert.Single(site.HeaderRanges);
        Assert.Equal(new AlTextPosition(21, 8), header.Start);
        Assert.True(header.End < new AlTextPosition(22, 12), $"header must end before the body starts: {header}");
        var body = Assert.Single(site.Body);
        Assert.Equal(new AlTextPosition(22, 12), body.Range.Start);
        Assert.Null(body.NestedSiteIndex);
        Assert.Null(site.ParentIndex);
    }

    [SkippableFact]
    public void Parse_EveryLoopKind_IsRecognised_WithForEachVariable()
    {
        RequireEngine();
        var m1 = AlLoopSyntaxIndex.Parse(LoopShapesSource, "LoopShapes.al");
        var m2 = AlLoopSyntaxIndex.Parse(LoopShapes2Source, "LoopShapes2.al");

        Assert.Equal(AlLoopKind.While, Assert.Single(Member(m1, "WhileDo").Sites).Kind);
        Assert.Equal(AlLoopKind.Repeat, Assert.Single(Member(m1, "RepeatUntil").Sites).Kind);
        var fe = Assert.Single(Member(m2, "ForEachList").Sites);
        Assert.Equal(AlLoopKind.ForEach, fe.Kind);
        Assert.Equal("v", fe.LoopVariable);
        Assert.Null(Assert.Single(Member(m1, "WhileDo").Sites).LoopVariable);
        // A member with no loop at all is still listed, with no sites, so a scope
        // lookup can tell "member found, no loops" from "member not found".
        Assert.Empty(Member(m1, "OnRun").Sites);
    }

    [SkippableFact]
    public void Parse_RepeatHeaderIsTheUntilCondition_AfterTheBody()
    {
        RequireEngine();
        var site = Assert.Single(Member(AlLoopSyntaxIndex.Parse(LoopShapesSource, "LoopShapes.al"), "RepeatUntil").Sites);
        var header = Assert.Single(site.HeaderRanges);
        var body = Assert.Single(site.Body);
        Assert.True(header.Start > body.Range.End, $"until-condition {header} must follow the body {body.Range}");
        Assert.Equal(54, header.Start.Line); // `until n >= 3;` is line 55 (1-based)
    }

    [SkippableFact]
    public void Parse_BlockBody_IsFlattened_ToItsStatements()
    {
        RequireEngine();
        var site = Assert.Single(Member(AlLoopSyntaxIndex.Parse(LoopShapesSource, "LoopShapes.al"), "WithBreak").Sites);
        // begin s := s + i; if i = 2 then break; end  -> two statements, not one block
        Assert.Equal(2, site.Body.Count);
        Assert.Equal(78, site.Body[0].Range.Start.Line);
        Assert.Equal(79, site.Body[1].Range.Start.Line);
    }

    [SkippableFact]
    public void Parse_NestedLoops_ParentIndex_AndNestedSiteIndexOnlyWhenTheStatementIsTheLoop()
    {
        RequireEngine();
        var m = Member(AlLoopSyntaxIndex.Parse(LoopShapes2Source, "LoopShapes2.al"), "WhileFirst");
        Assert.Equal(2, m.Sites.Count);
        var outer = m.Sites[0];
        var inner = m.Sites[1];
        Assert.Equal(AlLoopKind.For, outer.Kind);
        Assert.Equal(AlLoopKind.While, inner.Kind);
        Assert.Null(outer.ParentIndex);
        Assert.Equal(0, inner.ParentIndex);
        Assert.Equal(3, outer.Body.Count);                 // while; n := 0; s := s + 1
        Assert.Equal(1, outer.Body[0].NestedSiteIndex);     // the while IS the first statement
        Assert.Null(outer.Body[1].NestedSiteIndex);
        Assert.Null(outer.Body[2].NestedSiteIndex);
    }

    [SkippableFact]
    public void Parse_FieldTriggersSharingAName_AreSeparateMembers_ResolvedByStatementPosition()
    {
        RequireEngine();
        const string table = """
table 60302 "Loop Trigger Fixture"
{
    fields
    {
        field(1; A; Integer)
        {
            trigger OnValidate()
            var
                i: Integer;
            begin
                for i := 1 to 2 do
                    Rec.A := i;
            end;
        }
        field(2; B; Integer)
        {
            trigger OnValidate()
            var
                n: Integer;
            begin
                while n < 2 do
                    n := n + 1;
            end;
        }
    }
}
""";
        var members = AlLoopSyntaxIndex.Parse(table, "Fixture.Table.al");
        var validates = members.Where(m => m.Name == "OnValidate").ToList();
        Assert.Equal(2, validates.Count);
        Assert.Equal(AlLoopKind.For, Assert.Single(validates[0].Sites).Kind);
        Assert.Equal(AlLoopKind.While, Assert.Single(validates[1].Sites).Kind);

        var index = AlLoopSyntaxIndex.FromMembers(members);
        // A statement on line 21 (0-based 20: `while n < 2 do`) belongs to field B's trigger.
        var sites = index.FindSites("Fixture.Table.al", "OnValidate", new AlTextPosition(20, 16));
        Assert.NotNull(sites);
        Assert.Equal(AlLoopKind.While, Assert.Single(sites!).Kind);
        // Unknown member -> null (distinct from "found, no loops" -> empty).
        Assert.Null(index.FindSites("Fixture.Table.al", "OnInsert", null));
    }

    // --- Build: measured tables -> header / body / marker ------------------------------

    [SkippableFact]
    public void Build_ForTo_HeaderIsTheForStatement_BodyPerIteration_TrailingStatementUnowned()
    {
        RequireEngine();
        var sites = Member(AlLoopSyntaxIndex.Parse(LoopShapesSource, "LoopShapes.al"), "ForTo").Sites;
        var t = Assert.Single(AlLoopScopeTable.Build(sites, MeasuredForTo).Sites);
        Assert.Equal(new[] { 1 }, t.HeaderIds.Order().ToArray());
        Assert.Equal(new[] { 2 }, t.BodyIds.Order().ToArray());
        Assert.Equal(2, t.MarkerStatementId);
        Assert.Null(t.MarkerNestedSiteIndex);
        Assert.Null(t.Unsegmentable);
        Assert.False(t.Owns(0));
        Assert.False(t.Owns(3));
        Assert.Equal(22, t.StartLine);
        Assert.Equal(23, t.EndLine);
    }

    [SkippableFact]
    public void Build_WhileDo_HeaderIsTheCondition_BlockEndIsOutside()
    {
        RequireEngine();
        var sites = Member(AlLoopSyntaxIndex.Parse(LoopShapesSource, "LoopShapes.al"), "WhileDo").Sites;
        var t = Assert.Single(AlLoopScopeTable.Build(sites, MeasuredWhileDo).Sites);
        Assert.Equal(new[] { 1 }, t.HeaderIds.Order().ToArray());
        Assert.Equal(new[] { 2 }, t.BodyIds.Order().ToArray());
        Assert.Equal(2, t.MarkerStatementId);
        Assert.False(t.Owns(3)); // the `end` of `do begin ... end`, hit once AFTER the loop
        Assert.False(t.Owns(4));
    }

    [SkippableFact]
    public void Build_RepeatUntil_HeaderIsTheUntilCondition()
    {
        RequireEngine();
        var sites = Member(AlLoopSyntaxIndex.Parse(LoopShapesSource, "LoopShapes.al"), "RepeatUntil").Sites;
        var t = Assert.Single(AlLoopScopeTable.Build(sites, MeasuredRepeatUntil).Sites);
        Assert.Equal(new[] { 2 }, t.HeaderIds.Order().ToArray());
        Assert.Equal(new[] { 1 }, t.BodyIds.Order().ToArray());
        Assert.Equal(1, t.MarkerStatementId);
    }

    [SkippableFact]
    public void Build_Nested_InnerIdsBelongToBothBodies_ChildWiredToParent()
    {
        RequireEngine();
        var sites = Member(AlLoopSyntaxIndex.Parse(LoopShapesSource, "LoopShapes.al"), "Nested").Sites;
        var table = AlLoopScopeTable.Build(sites, MeasuredNested);
        Assert.Equal(2, table.Sites.Count);
        var outer = table.Sites[0];
        var inner = table.Sites[1];
        Assert.Equal(new[] { 0 }, outer.HeaderIds.Order().ToArray());
        Assert.Equal(new[] { 1, 2, 3, 4 }, outer.BodyIds.Order().ToArray());
        Assert.Equal(1, outer.MarkerStatementId);
        Assert.Equal(new[] { 2 }, inner.HeaderIds.Order().ToArray());
        Assert.Equal(new[] { 3 }, inner.BodyIds.Order().ToArray());
        Assert.Equal(3, inner.MarkerStatementId);
        Assert.Same(inner, Assert.Single(outer.Children));
        Assert.Same(outer, Assert.Single(table.Roots));
        Assert.False(outer.Owns(5)); // block end
        Assert.Same(outer, table.RootSiteOwning(3));
    }

    [SkippableFact]
    public void Build_WhileAsFirstBodyStatement_MarkerIsTheNestedLoopsEntry()
    {
        RequireEngine();
        var sites = Member(AlLoopSyntaxIndex.Parse(LoopShapes2Source, "LoopShapes2.al"), "WhileFirst").Sites;
        var table = AlLoopScopeTable.Build(sites, MeasuredWhileFirst);
        var outer = table.Sites[0];
        Assert.Null(outer.MarkerStatementId);
        Assert.Equal(1, outer.MarkerNestedSiteIndex);
        Assert.Equal(new[] { 1, 2, 3, 4 }, outer.BodyIds.Order().ToArray());
        Assert.Equal(new[] { 1 }, table.Sites[1].HeaderIds.Order().ToArray());
        Assert.Equal(new[] { 2 }, table.Sites[1].BodyIds.Order().ToArray());
        Assert.False(outer.Owns(5));
    }

    [SkippableFact]
    public void Build_IfAsFirstBodyStatement_MarkerIsItsConditionId()
    {
        RequireEngine();
        var sites = Member(AlLoopSyntaxIndex.Parse(LoopShapes2Source, "LoopShapes2.al"), "IfFirst").Sites;
        var t = Assert.Single(AlLoopScopeTable.Build(sites, MeasuredIfFirst).Sites);
        Assert.Equal(new[] { 0 }, t.HeaderIds.Order().ToArray());
        Assert.Equal(new[] { 1, 2, 3 }, t.BodyIds.Order().ToArray());
        Assert.Equal(1, t.MarkerStatementId); // the `if` condition: fires once per iteration
        Assert.False(t.Owns(4));
    }

    [SkippableFact]
    public void Build_ForEach_SameShapeAsFor()
    {
        RequireEngine();
        var sites = Member(AlLoopSyntaxIndex.Parse(LoopShapes2Source, "LoopShapes2.al"), "ForEachList").Sites;
        var t = Assert.Single(AlLoopScopeTable.Build(sites, MeasuredForEachList).Sites);
        Assert.Equal(new[] { 2 }, t.HeaderIds.Order().ToArray());
        Assert.Equal(new[] { 3 }, t.BodyIds.Order().ToArray());
        Assert.Equal(3, t.MarkerStatementId);
        Assert.Equal("v", t.LoopVariable);
    }

    [SkippableFact]
    public void Build_ZeroIterationLoop_StillFullyClassified()
    {
        RequireEngine();
        var sites = Member(AlLoopSyntaxIndex.Parse(LoopShapesSource, "LoopShapes.al"), "ZeroIter").Sites;
        var t = Assert.Single(AlLoopScopeTable.Build(sites, MeasuredZeroIter).Sites);
        Assert.Equal(new[] { 0 }, t.HeaderIds.Order().ToArray());
        Assert.Equal(new[] { 1 }, t.BodyIds.Order().ToArray());
        Assert.Equal(1, t.MarkerStatementId);
    }

    // --- Build: negative direction, no engine needed ----------------------------------

    [Fact]
    public void Build_EmptyBody_IsUnsegmentable_WithAReason_NotSilentlyZero()
    {
        // for i := 1 to 3 do ;    <- one id for the `for`, none in the body
        var site = new AlLoopSite(0, AlLoopKind.For, "i",
            new AlTextRange(new AlTextPosition(8, 8), new AlTextPosition(8, 27)),
            new[] { new AlTextRange(new AlTextPosition(8, 8), new AlTextPosition(8, 26)) },
            Array.Empty<AlLoopBodyStatement>(), null);
        var spans = new[] { AlSourceSpanCodec.Encode(8, 8, 8, 27), AlSourceSpanCodec.Encode(9, 8, 9, 14) };

        var t = Assert.Single(AlLoopScopeTable.Build(new[] { site }, spans).Sites);
        Assert.Equal(new[] { 0 }, t.HeaderIds.Order().ToArray());
        Assert.Empty(t.BodyIds);
        Assert.Null(t.MarkerStatementId);
        Assert.NotNull(t.Unsegmentable);
        Assert.Contains("no instrumented statement", t.Unsegmentable);
    }

    [Fact]
    public void Build_RestrictedToInstrumentedIds_IgnoresTheTrailingSentinel()
    {
        // BC emits one SourceSpans entry beyond the last StmtHit (the method's closing
        // `end;`). If it were considered, a loop ending at the method's end could absorb it.
        var site = new AlLoopSite(0, AlLoopKind.While, null,
            new AlTextRange(new AlTextPosition(5, 8), new AlTextPosition(6, 20)),
            new[] { new AlTextRange(new AlTextPosition(5, 8), new AlTextPosition(5, 22)) },
            new[] { new AlLoopBodyStatement(new AlTextRange(new AlTextPosition(6, 12), new AlTextPosition(6, 20)), null) },
            null);
        var spans = new[]
        {
            AlSourceSpanCodec.Encode(5, 14, 5, 19), // 0: condition
            AlSourceSpanCodec.Encode(6, 12, 6, 20), // 1: body
            AlSourceSpanCodec.Encode(6, 12, 6, 20), // 2: a sentinel that happens to share the body's span
        };
        var t = Assert.Single(AlLoopScopeTable.Build(new[] { site }, spans, instrumented: new[] { 0, 1 }).Sites);
        Assert.Equal(new[] { 1 }, t.BodyIds.Order().ToArray());
        Assert.False(t.Owns(2));
    }

    [Fact]
    public void LineOf_OutsideTheSpanTable_IsNullNotAFakeLine()
    {
        var table = new AlLoopScopeTable(Array.Empty<AlLoopSiteTable>(), new[] { AlSourceSpanCodec.Encode(3, 0, 3, 5) });
        Assert.Equal(4, table.LineOf(0));
        Assert.Null(table.LineOf(1));
        Assert.Null(table.LineOf(-1));
    }
}

codeunit 63701 "Pbtoos Test"
{
    // Runner-specific contract only -- see app.json's brief/description and
    // .claude/rules/bc-behavior-tests-go-upstream.md. Not a claim about BC's own
    // page-background-task behaviour.
    Subtype = Test;
    TestPermissions = Disabled;

    var
        Assert: Codeunit "Pbtoos Assert";

    local procedure Seed()
    var
        Row: Record "Pbtoos Row";
    begin
        Row.DeleteAll();
        Row.Init();
        Row."No." := 'PBT-1';
        Row.Insert();
    end;

    [Test]
    procedure EnqueueBackgroundTask_FromTrigger_ThrowsOutOfScope()
    var
        Row: Record "Pbtoos Row";
        Card: TestPage "Pbtoos Card";
    begin
        // [GIVEN] a page whose OnAfterGetCurrRecord calls CurrPage.EnqueueBackgroundTask
        Seed();
        Row.Get('PBT-1');

        // [WHEN] the page is positioned, which fires OnAfterGetCurrRecord
        Card.OpenView();
        asserterror Card.GoToRecord(Row);

        // [THEN] the runner refuses loudly, naming the exact AL API -- not an internal
        // NavSession/NavTenant NRE or ArgumentNullException a caller could mistake for an
        // unrelated bug.
        Assert.ExpectedError('out-of-scope: Page.EnqueueBackgroundTask');
        Assert.ExpectedError('not-yet-implemented');
    end;

    [Test]
    procedure RunPageBackgroundTask_NoCompletionTriggers_ThrowsOutOfScope()
    var
        Row: Record "Pbtoos Row";
        Card: TestPage "Pbtoos NoTrigger Card";
        Params: Dictionary of [Text, Text];
        Results: Dictionary of [Text, Text];
    begin
        // [GIVEN] a TestPage over a page with no PBT-triggering AL of its own
        Seed();
        Row.Get('PBT-1');
        Card.OpenEdit();
        Card.GoToRecord(Row);

        // [WHEN] the test calls TestPage.RunPageBackgroundTask directly
        asserterror Results := Card.RunPageBackgroundTask(Codeunit::"Pbtoos Worker", Params, false);

        // [THEN] the runner refuses loudly, naming the exact AL API
        Assert.ExpectedError('out-of-scope: TestPage.RunPageBackgroundTask');
        Assert.ExpectedError('not-yet-implemented');
        Card.Close();
    end;

    [Test]
    procedure RunPageBackgroundTask_WithCompletionTriggers_ThrowsOutOfScope()
    var
        Row: Record "Pbtoos Row";
        Card: TestPage "Pbtoos NoTrigger Card";
        Params: Dictionary of [Text, Text];
        Results: Dictionary of [Text, Text];
    begin
        // [GIVEN] a TestPage over a page with no PBT-triggering AL of its own
        Seed();
        Row.Get('PBT-1');
        Card.OpenEdit();
        Card.GoToRecord(Row);

        // [WHEN] the test calls TestPage.RunPageBackgroundTask with RunCompletionTriggers = true
        asserterror Results := Card.RunPageBackgroundTask(Codeunit::"Pbtoos Worker", Params, true);

        // [THEN] the runner refuses loudly, naming the exact AL API -- same refusal regardless
        // of RunCompletionTriggers, because both funnel through the same synchronous
        // child-session bootstrap.
        Assert.ExpectedError('out-of-scope: TestPage.RunPageBackgroundTask');
        Assert.ExpectedError('not-yet-implemented');
        Card.Close();
    end;
}

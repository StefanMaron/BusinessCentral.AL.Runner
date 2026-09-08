// TCM -- the runner reaches the END of BC's close path when a [MessageHandler] consumes the
// close-time error, instead of refusing partway through it. Issue #3179.
//
// WHAT IS ASSERTED HERE, AND WHAT IS NOT
//   That real BC shows an OnQueryClosePage error as a MESSAGE, refuses the close, hands
//   control back to the caller, and leaves the page open is a plain BC-behaviour claim. It is
//   asserted upstream in the al-language corpus by codeunit 60602 "QCM Query Close Msg Tests"
//   (StefanMaron/BusinessCentral.AL.Language.Tests#272, merged bd168356), green on all eight
//   cloud legs and confirmed by the Windows nightly reference tier. None of it is re-asserted
//   here, and none of these assertions may be read as evidence about BC.
//
//   What this suite pins is a property only a runner test can observe: the runner does not
//   REFUSE. Until #3179 it raised RunnerOutOfScopeException the instant a [MessageHandler]
//   consumed the text, so every line after the close was unreachable -- a test that passes on a
//   service tier could not run here at all. A corpus leg cannot see that, because the refusal
//   exists only in the runner.
//
//   Each [Test] below therefore has the same structure: DRIVE the close, then read state back
//   AFTER it. Reaching the read at all is half the claim; the value read is the other half, and
//   it is a concrete value rather than a liveness check, so a runner that returned control but
//   discarded the page's work would fail rather than pass quietly.
codeunit 65863 "Tcm Close Message Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        Assert: Codeunit "Tcm Assert";
        CloseRefusedTxt: Label 'TCM close refused by OnQueryClosePage';

    // CLAIM 1: control comes back. This is the whole of the #3179 defect -- the runner used to
    // raise here, so the line after Close() was unreachable.
    //
    // Asserting the witness row rather than merely reaching the end is what makes the test
    // prove something: a runner that swallowed the close-time error without ever dispatching
    // the [MessageHandler] would also reach this line, and it fails the first assertion.
    [Test]
    [HandlerFunctions('TcmMessageHandler')]
    procedure CloseAfterQueryCloseError_MessageConsumed_ReturnsToTheCaller()
    var
        Card: TestPage "Tcm Error Card";
        Row: Record "Tcm Row";
    begin
        Initialize();

        Card.OpenEdit();
        Card.Close();

        // Reaching this line is the claim. Before #3179 the runner raised
        // "not-yet-implemented -- testpage-close-refused-after-message" inside Close().
        Assert.IsTrue(Row.Get('SEEN'),
            'The [MessageHandler] must have been dispatched -- reaching this line without it would mean the close-time error was swallowed rather than shown.');
        Assert.AreEqual(1, Row."Set ID",
            'The TestPage route must deliver the close-time message exactly once. Two would mean the runner attempted the close twice.');
        Assert.IsTrue(StrPos(Row."Last Text", CloseRefusedTxt) > 0,
            StrSubstNo('The handler must receive the trigger''s own error text, not a runner-invented one; got "%1".', Row."Last Text"));
    end;

    // CLAIM 2: the page is still usable afterwards, which is what "BC left it open" means for a
    // test that still holds the variable. A runner that returned control but tore the page down
    // would fail here with BC's own "The TestPage is not open."
    //
    // Reading a FIELD, not a status flag: a torn-down page raises on any field read, so this
    // cannot pass against a page that only claims to be open.
    [Test]
    [HandlerFunctions('TcmMessageHandler')]
    procedure CloseAfterQueryCloseError_MessageConsumed_LeavesThePageDrivable()
    var
        Card: TestPage "Tcm Error Card";
    begin
        Initialize();

        Card.OpenEdit();
        Card."Set ID".SetValue(99);
        Card.Close();

        Assert.AreEqual('99', Format(Card."Set ID".Value()),
            'The page must still be drivable after a close BC refused -- and must still hold the value set before the close, not a value re-read from a page that was torn down and rebuilt.');
    end;

    // CLAIM 3: the write the page made survives the refused close.
    //
    // Corpus 60602 measured this on a real tier and it was the fact that contradicted the test
    // author's prediction: the neighbouring codeunit 60677 asserts the write IS rolled back and
    // also passes, because it measures behind asserterror, where what unwinds the write is a
    // PROPAGATED error. A consumed message propagates nothing, so nothing unwinds. Pinned here
    // because the runner's own close path has a flush step, and skipping the tear-down (which is
    // how the refusal is implemented) must not be allowed to drop the row either.
    //
    // The VALUE is asserted, not just the row's presence, so a row resurrected empty by anything
    // else cannot satisfy it.
    [Test]
    [HandlerFunctions('TcmMessageHandler')]
    procedure CloseAfterQueryCloseError_MessageConsumed_KeepsThePagesWrite()
    var
        Card: TestPage "Tcm Error Card";
        Row: Record "Tcm Row";
    begin
        Initialize();

        Card.OpenEdit();
        Card.Close();

        Assert.IsTrue(Row.Get('OPENED'),
            'The row OnOpenPage inserted must survive a close the page refused -- nothing propagated, so nothing unwinds.');
        Assert.AreEqual(42, Row."Set ID",
            'The surviving row must carry the value OnOpenPage wrote, not a default.');
    end;

    // NEGATIVE CONTROL, and the reason the three arms above are not vacuous. Without a
    // [MessageHandler] the text has nowhere to go, so showing it is itself what fails: the
    // framework raises its own "Unhandled UI: Message ..." from inside the show call and the
    // close-time error DOES reach the test.
    //
    // This is what stops "the runner now swallows every close-time error" from passing the
    // suite. A change that made Close() unconditionally succeed would turn all three arms above
    // green and fail this one.
    [Test]
    procedure CloseAfterQueryCloseError_NoMessageHandler_StillReachesTheTest()
    var
        Card: TestPage "Tcm Error Card";
    begin
        Initialize();

        Card.OpenEdit();
        asserterror Card.Close();

        // The trigger's own text, carried inside whatever envelope the unhandled-message
        // refusal puts it in. Asserting the text rather than the envelope: which wording the
        // framework wraps it in is BC's decision and is pinned upstream, not here.
        Assert.IsTrue(StrPos(GetLastErrorText(), CloseRefusedTxt) > 0,
            StrSubstNo('With no [MessageHandler] declared the close-time error must still reach the test; got "%1".', GetLastErrorText()));
    end;

    local procedure Initialize()
    var
        Row: Record "Tcm Row";
    begin
        Row.DeleteAll();
    end;

    // Records rather than asserts -- see the "Tcm Row" header. Counting deliveries as well as
    // capturing the text, so an arm can tell one close attempt from two.
    [MessageHandler]
    procedure TcmMessageHandler(Msg: Text[1024])
    var
        Row: Record "Tcm Row";
    begin
        if not Row.Get('SEEN') then begin
            Row.Init();
            Row."No." := 'SEEN';
            Row."Set ID" := 0;
            Row.Insert();
        end;
        Row."Set ID" := Row."Set ID" + 1;
        Row."Last Text" := CopyStr(Msg, 1, MaxStrLen(Row."Last Text"));
        Row.Modify();
    end;
}

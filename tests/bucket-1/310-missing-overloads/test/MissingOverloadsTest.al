/// Tests for missing overloads (issue #1380):
///   Dialog.Error (Text, Joker)
///   ErrorInfo.AddAction (Text, Integer, Text, Text)
///   ErrorInfo.AddNavigationAction (Text, Text)
///   FilterPageBuilder.AddField (Text, Joker, Text)
///   TestField.Lookup (RecordRef)
codeunit 310202 "MOv Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "MOv Src";

    // ── Dialog.Error (Text, Joker) ───────────────────────────────────────────

    [Test]
    procedure DialogError_TextInt_ThrowsFormattedMessage()
    begin
        // Positive (negative test): Error('Value is %1', 42) must throw with the
        // interpolated message "Value is 42" — not just any error.
        asserterror Src.ErrorWithIntArg(42);
        Assert.ExpectedError('Value is 42');
    end;

    [Test]
    procedure DialogError_TextInt_DifferentValue_Distinct()
    begin
        // Proves a different value produces a different message (catches no-op mocks
        // that always return the same string regardless of argument).
        asserterror Src.ErrorWithIntArg(99);
        Assert.ExpectedError('Value is 99');
    end;

    [Test]
    procedure DialogError_TextText_ThrowsFormattedMessage()
    begin
        // Positive: Error('Name is %1', 'Alice') throws with "Name is Alice".
        asserterror Src.ErrorWithTextArg('Alice');
        Assert.ExpectedError('Name is Alice');
    end;

    // ── ErrorInfo.AddAction (Text, Integer, Text, Text) ──────────────────────

    [Test]
    procedure EI_AddAction_4Arg_NoThrow()
    begin
        // Positive: the 4-arg AddAction(caption, codeunitId, method, params) must
        // complete without throwing (no-op in standalone mode).
        Assert.IsTrue(
            Src.EI_AddAction_4Arg('Fix it', '{"param1":"val"}'),
            'AddAction 4-arg must complete without throwing');
    end;

    [Test]
    procedure EI_AddAction_4Arg_EmptyParams_NoThrow()
    begin
        // Edge: empty params string must also complete.
        Assert.IsTrue(
            Src.EI_AddAction_4Arg('Fix it', ''),
            'AddAction 4-arg with empty params must not throw');
    end;

    [Test]
    procedure EI_AddAction_4Arg_EmptyCaption_NoThrow()
    begin
        // Edge: empty caption must not crash.
        Assert.IsTrue(
            Src.EI_AddAction_4Arg('', 'data'),
            'AddAction 4-arg with empty caption must not throw');
    end;

    // ── ErrorInfo.AddNavigationAction (Text, Text) ───────────────────────────

    [Test]
    procedure EI_AddNavigationAction_2Arg_NoThrow()
    begin
        // Positive: 2-arg AddNavigationAction(caption, description) must complete.
        Assert.IsTrue(
            Src.EI_AddNavigationAction_2Arg('Open list', 'Navigate to records'),
            'AddNavigationAction 2-arg must complete without throwing');
    end;

    [Test]
    procedure EI_AddNavigationAction_2Arg_EmptyDesc_NoThrow()
    begin
        // Edge: empty description must also complete.
        Assert.IsTrue(
            Src.EI_AddNavigationAction_2Arg('Open list', ''),
            'AddNavigationAction 2-arg with empty description must not throw');
    end;

    [Test]
    procedure EI_AddNavigationAction_PreservesMessage()
    begin
        // Positive: ErrorInfo.Message must be unchanged after AddNavigationAction.
        Assert.AreEqual(
            'Test error',
            Src.EI_AddNavigationAction_MessagePreserved(),
            'ErrorInfo.Message must be preserved after AddNavigationAction');
    end;

    // ── FilterPageBuilder.AddField (Text, Joker, Text) ───────────────────────

    [Test]
    procedure FPB_AddField_3Arg_NoThrow()
    begin
        // Positive: AddField(caption, field, defaultFilter) must not throw.
        Assert.IsTrue(
            Src.FPB_AddField_3Arg('Code', 'A*'),
            'FilterPageBuilder.AddField 3-arg must complete without throwing');
    end;

    [Test]
    procedure FPB_AddField_3Arg_EmptyFilter_NoThrow()
    begin
        // Edge: empty default filter must also complete.
        Assert.IsTrue(
            Src.FPB_AddField_3Arg('Code', ''),
            'FilterPageBuilder.AddField 3-arg with empty filter must not throw');
    end;

    [Test]
    procedure FPB_AddField_3Arg_IncreasesCount()
    begin
        // Positive: Count must reflect how many fields were registered.
        // Returns 2 when two fields are added.
        Assert.AreEqual(
            2,
            Src.FPB_AddField_3Arg_Count(),
            'FilterPageBuilder.Count must be 2 after adding 2 fields with AddField 3-arg');
    end;

    // ── TestField.Lookup (RecordRef) ─────────────────────────────────────────

    [Test]
    procedure TF_Lookup_RecordRef_NoThrow()
    var
        TP: TestPage "MOv Card";
        RecRef: RecordRef;
        Rec: Record "MOv Rec";
    begin
        // Positive: TestField.Lookup(RecordRef) must complete without throwing.
        // (No-op in standalone mode — no real UI to open.)
        TP.OpenNew();
        RecRef.Open(Database::"MOv Rec");
        TP.CodeField.Lookup(RecRef);
        RecRef.Close();
        TP.Close();
        Assert.IsTrue(true, 'TestField.Lookup(RecordRef) must not throw in standalone mode');
    end;

    [Test]
    procedure TF_Lookup_RecordRef_FieldValueUnchanged()
    var
        TP: TestPage "MOv Card";
        RecRef: RecordRef;
        Rec: Record "MOv Rec";
    begin
        // Positive: after Lookup(RecordRef), the field value must be unchanged
        // (no-op mock does not alter it).
        TP.OpenNew();
        TP.CodeField.SetValue('BEFORE');
        RecRef.Open(Database::"MOv Rec");
        TP.CodeField.Lookup(RecRef);
        RecRef.Close();
        Assert.AreEqual('BEFORE', TP.CodeField.Value(), 'Field value must be unchanged after no-op Lookup');
        TP.Close();
    end;
}

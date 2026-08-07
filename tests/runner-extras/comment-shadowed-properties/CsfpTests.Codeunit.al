// Issue #1690: a comment mentioning 'InitValue =' was parsed as the field's InitValue,
// so Insert threw NavNCLEvaluateException with the comment prose as the value. These
// pin the concrete parsed values, not just "it did not crash".
codeunit 62402 "CSFP Comment Shadow Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "CSFP Assert";

    [Test]
    procedure InsertAppliesTheDeclaredInitValueNotTheCommentText()
    var
        Flag: Record "CSFP Flag Table";
    begin
        // [WHEN] A row is inserted without touching Accept.
        Flag.Init();
        Flag."Code" := 'A';
        Flag.Insert(false);

        // [THEN] It carries the DECLARED InitValue. Before the fix this line was never
        // reached: Insert threw NavNCLEvaluateException because the field's InitValue was
        // the comment prose ('true: new rows default to accepted. ...').
        Flag.Get('A');
        Assert.IsTrue(Flag.Accept, 'Accept must default to true via the declared InitValue');
    end;

    [Test]
    procedure CommentMentioningCaptionDoesNotOverrideTheDeclaredCaption()
    var
        Flag: Record "CSFP Flag Table";
    begin
        // [THEN] The block comment naming a different Caption is prose, not a property.
        Assert.AreEqual('Ratio // Net', Flag.FieldCaption(Ratio),
            'the declared Caption must win over the one named in the comment');
    end;

    [Test]
    procedure SlashesInsideAStringLiteralAreNotTreatedAsAComment()
    var
        Flag: Record "CSFP Flag Table";
    begin
        // [THEN] Negative direction: the comment pass must not over-reach. Truncating at
        // the '//' would leave 'Ratio ' here.
        Assert.AreEqual('Ratio // Net', Flag.FieldCaption(Ratio),
            'a // inside a string literal is literal text, not a comment');
        Assert.AreEqual('Accept', Flag.FieldCaption(Accept),
            'an ordinary caption on a commented field is still parsed');
    end;
}

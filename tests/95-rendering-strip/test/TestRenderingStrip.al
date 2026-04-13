codeunit 95002 "RS Rendering Strip Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure ReportWithRenderingBlockCompiles()
    var
        Helper: Codeunit "RS Logic Helper";
        Result: Text;
    begin
        // [GIVEN] A report with a rendering { ... } block in AL source
        // [WHEN] The source is compiled through the standalone pipeline
        //        (rendering block is stripped before transpilation)
        Result := Helper.GetRecordName(1);

        // [THEN] Compilation succeeds and logic runs
        Assert.AreEqual('Generated', Result, 'Logic alongside report with rendering block should work');
    end;

    [Test]
    procedure ReportWithRenderingNegative()
    var
        Helper: Codeunit "RS Logic Helper";
        Result: Text;
    begin
        // [NEGATIVE] The returned value should not be empty
        Result := Helper.GetRecordName(2);
        Assert.AreNotEqual('', Result, 'Logic should return non-empty value');
    end;
}

/// <summary>
/// Proves that a tableextension compiled from a DEPENDENCY app's AL source
/// is correctly registered on its base-table records, and that calling
/// the extension's procedure reaches its body (InvokeAsync(extId=60701) fires).
///
/// This is the runner-extras regression test for the class of gaps where
/// dependency-app tableextension code was NOT wired up and would throw
/// NavNCLCompilationException "table extension object with ID N was not found
/// for the table object with ID M".
///
/// RED (without fix): the extension procedure would be unreachable → error.
/// GREEN (with fix): the concrete value 42 + Score*Mult is returned.
/// </summary>
codeunit 60751 "DEX TableExt Invoke Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "DEX Assert";

    /// <summary>
    /// Positive: calls the dep tableextension procedure with Score=5, Mult=3.
    /// Expects: 5*3+42 = 57. Proves InvokeAsync(60701) fires the real body.
    /// </summary>
    [Test]
    procedure DepExtProc_WithScoreAndMultiplier_ReturnsCorrectValue()
    var
        Rec: Record "DEX Base Table";
        Result: Integer;
    begin
        Rec.Init();
        Rec."No." := 'T1';
        Rec."Extension Score" := 5;

        Result := Rec.ComputeScore(3);

        Assert.AreEqual(57, Result,
            'ComputeScore(3) on DEX Base Table with ExtensionScore=5 must return 5*3+42=57');
    end;

    /// <summary>
    /// Positive: calls the dep tableextension procedure with Score=0, Mult=10.
    /// Expects: 0*10+42 = 42. The constant 42 must always be present regardless of Score.
    /// </summary>
    [Test]
    procedure DepExtProc_WithZeroScore_ReturnsFortyTwo()
    var
        Rec: Record "DEX Base Table";
        Result: Integer;
    begin
        Rec.Init();
        Rec."No." := 'T2';
        Rec."Extension Score" := 0;

        Result := Rec.ComputeScore(10);

        Assert.AreEqual(42, Result,
            'ComputeScore(10) with ExtensionScore=0 must return 0*10+42=42');
    end;

    /// <summary>
    /// Positive: the dep tableextension field "Extension Score" is readable and
    /// writable on a record — proves field registration is correct.
    /// </summary>
    [Test]
    procedure DepExtField_ReadWrite_ReturnsAssignedValue()
    var
        Rec: Record "DEX Base Table";
    begin
        Rec.Init();
        Rec."Extension Score" := 99;

        Assert.AreEqual(99, Rec."Extension Score",
            'Extension Score field round-trip must return assigned value 99');
    end;

    /// <summary>
    /// Negative: wrong multiplier 0 still returns 42 (the constant), not 0.
    /// Guards against a stub that returns default int (0).
    /// </summary>
    [Test]
    procedure DepExtProc_WithMultiplierZero_StillReturnsFortyTwo()
    var
        Rec: Record "DEX Base Table";
        Result: Integer;
    begin
        Rec.Init();
        Rec."Extension Score" := 100;

        Result := Rec.ComputeScore(0);

        Assert.AreEqual(42, Result,
            'ComputeScore(0) must return 100*0+42=42, not 0 (a stub returning default int would fail this)');
    end;
}

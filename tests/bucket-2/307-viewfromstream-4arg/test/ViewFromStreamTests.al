codeunit 307501 "ViewFromStream Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    /// <summary>
    /// Positive: ViewFromStream(InStream, FileName, IsEditable) is a no-op in standalone mode
    /// — it must not throw. This is the 3-arg AL form which BC transpiles to the 4-arg C# call
    /// ALViewFromStream(parent, inStream, fileName, isEditable).
    /// </summary>
    [Test]
    procedure ViewFromStream_4Arg_NoThrow()
    var
        Helper: Codeunit "ViewFromStream Helper";
    begin
        // Verifies that ALViewFromStream with 4 C# args compiles and runs without error.
        // The entire claim is "this does not crash" — the overload is a no-op in standalone mode.
        Helper.CallViewFromStream4Arg();
    end;

    /// <summary>
    /// Positive: ViewFromStream(InStream, FileName) is a no-op in standalone mode
    /// — it must not throw. This is the 2-arg AL form which BC transpiles to the 3-arg C# call
    /// ALViewFromStream(parent, inStream, fileName).
    /// </summary>
    [Test]
    procedure ViewFromStream_3Arg_NoThrow()
    var
        Helper: Codeunit "ViewFromStream Helper";
    begin
        // Verifies that ALViewFromStream with 3 C# args compiles and runs without error.
        Helper.CallViewFromStream3Arg();
    end;
}

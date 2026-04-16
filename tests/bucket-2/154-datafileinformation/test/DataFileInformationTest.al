codeunit 61401 "DFI Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    // ------------------------------------------------------------------
    // Positive: DataFileInformation returns the expected stub value.
    // ------------------------------------------------------------------

    [Test]
    procedure DataFileInformation_ReturnsNonEmpty()
    var
        H: Codeunit "DFI Helper";
        Result: Text;
    begin
        Result := H.GetDataFileInformation();
        Assert.AreNotEqual('', Result, 'DataFileInformation must return a non-empty string');
    end;

    [Test]
    procedure DataFileInformation_ReturnsStandaloneStub()
    var
        H: Codeunit "DFI Helper";
    begin
        Assert.AreEqual('STANDALONE', H.GetDataFileInformation(), 'DataFileInformation must return the STANDALONE stub value');
    end;

    // ------------------------------------------------------------------
    // Negative: return value must not be an empty string (proves stub is not a no-op).
    // ------------------------------------------------------------------

    [Test]
    procedure DataFileInformation_IsIdempotent()
    var
        H: Codeunit "DFI Helper";
        First: Text;
        Second: Text;
    begin
        First := H.GetDataFileInformation();
        Second := H.GetDataFileInformation();
        Assert.AreEqual(First, Second, 'DataFileInformation must return the same value on repeated calls');
    end;
}

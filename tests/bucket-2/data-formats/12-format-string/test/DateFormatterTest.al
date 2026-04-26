codeunit 50912 "Date Formatter Tests"
{
    Subtype = Test;

    var
        DateFmt: Codeunit "Date Formatter";
        Assert: Codeunit Assert;

    [Test]
    procedure TestFormatDateISO()
    var
        Result: Text;
    begin
        // [GIVEN] A date of January 15, 2025
        // [WHEN] Formatting with ISO format string
        Result := DateFmt.FormatDateISO(20250115D);

        // [THEN] The result should be '2025-01-15'
        Assert.AreEqual('2025-01-15', Result, 'Expected ISO date format 2025-01-15');
    end;
}

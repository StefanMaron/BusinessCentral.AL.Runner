/// <summary>"Zulu Nowhere Ledger" is declared by nobody: generated, and it takes the FIRST free table id although the
/// refused name sorts before it.</summary>
codeunit 65361 "Missing Table Package Control"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure MissingEverywhere_IsGenerated()
    var
        Ledger: Record "Zulu Nowhere Ledger";
    begin
        Ledger.Init();
        Ledger.Insert();
    end;
}

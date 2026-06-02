/// <summary>
/// TableExtension defined in the dependency app on the dep base table.
/// Adds a field and a procedure. The main app calls the procedure through the
/// extension to prove InvokeAsync(extId=60701) reaches this body.
/// </summary>
tableextension 60701 "DEX Base Table Ext" extends "DEX Base Table"
{
    fields
    {
        field(10; "Extension Score"; Integer) { DataClassification = CustomerContent; }
    }

    procedure ComputeScore(Multiplier: Integer): Integer
    begin
        exit("Extension Score" * Multiplier + 42);
    end;
}

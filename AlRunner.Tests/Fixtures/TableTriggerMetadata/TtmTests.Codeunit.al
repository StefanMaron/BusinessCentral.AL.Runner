// Touches each table once so the runner builds its NCLMetaTable and the audit records a line.
codeunit 70743 "TTM Tests"
{
    Subtype = Test;

    [Test]
    procedure TouchTheTablesSoTheirMetadataIsBuilt()
    var
        Plain: Record "TTM Plain";
        Own: Record "TTM Own";
    begin
        Plain.Init();
        Plain."No." := 'A1';
        Plain.Insert(true);

        Own.Init();
        Own."No." := 'B1';
        Own.Insert(true);
    end;
}

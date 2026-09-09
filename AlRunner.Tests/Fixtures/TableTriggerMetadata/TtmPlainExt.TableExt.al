// Declares three of the four write triggers a tableextension can contribute, and NOT the
// rename pair - so an implementation answering "all five bits" wholesale fails.
tableextension 70742 "TTM Plain Ext" extends "TTM Plain"
{
    trigger OnBeforeInsert()
    begin
        Payload := Payload + 1;
    end;

    trigger OnAfterModify()
    begin
        Payload := Payload + 1;
    end;

    trigger OnBeforeDelete()
    begin
        Payload := Payload + 1;
    end;
}

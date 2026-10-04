codeunit 73090 "TRE B Target"
{
    procedure Placeholder()
    begin
    end;
}

reportextension 73091 "TRE B Report Ext" extends "TRE A Report"
{
    trigger OnPreReport() var T: Codeunit "TRE B Target"; R: Integer; begin R := T.MBExtPre(1); end;
}

codeunit 73060 "TOT B Target"
{
    procedure Placeholder()
    begin
    end;
}

table 73061 "TOT B Rec"
{
    fields { field(1; PK; Code[20]) { } }
    keys { key(PK; PK) { Clustered = true; } }
}

report 73062 "TOT B Report"
{
    ProcessingOnly = true;
    UseRequestPage = false;
    trigger OnPreReport() var T: Codeunit "TOT B Target"; R: Integer; begin R := T.MBReportPre(1); end;
}

query 73063 "TOT B Query"
{
    elements { dataitem(Row; "TOT B Rec") { column(PK; PK) { } } }
    trigger OnBeforeOpen() var T: Codeunit "TOT B Target"; R: Integer; begin R := T.MBQueryOpen(1); end;
}

xmlport 73064 "TOT B XmlPort"
{
    Direction = Both;
    Format = Xml;
    UseRequestPage = false;
    schema { textelement(Root) { tableelement(Row; "TOT B Rec") { fieldelement(PK; Row.PK) { } } } }
    trigger OnPreXmlPort() var T: Codeunit "TOT B Target"; R: Integer; begin R := T.MBXmlPre(1); end;
}

reportextension 73065 "TOT B Report Ext" extends "TOT A Report"
{
    trigger OnPreReport() var T: Codeunit "TOT B Target"; R: Integer; begin R := T.MBExtPre(1); end;
}

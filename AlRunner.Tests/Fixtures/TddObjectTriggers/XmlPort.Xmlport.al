xmlport 73005 "OT XmlPort"
{
    Direction = Both;
    Format = Xml;
    UseRequestPage = false;
    schema
    {
        textelement(Root)
        {
            tableelement(Row; "OT Rec")
            {
                fieldelement(PK; Row.PK)
                {
                    trigger OnBeforePassField() var T: Codeunit "OT Target"; R: Integer; begin R := T.MXmlBeforePassField(1); end;
                    trigger OnAfterAssignField() var T: Codeunit "OT Target"; R: Integer; begin R := T.MXmlAfterAssignField(1); end;
                }
                fieldelement(Qty; Row.Qty) { }
                trigger OnPreXmlItem() var T: Codeunit "OT Target"; R: Integer; begin R := T.MXmlRowPre(1); end;
                trigger OnAfterGetRecord() var T: Codeunit "OT Target"; R: Integer; begin R := T.MXmlRowGet(1); end;
                trigger OnBeforeInsertRecord() var T: Codeunit "OT Target"; R: Integer; begin R := T.MXmlBeforeInsert(1); end;
                trigger OnAfterInsertRecord() var T: Codeunit "OT Target"; R: Integer; begin R := T.MXmlAfterInsert(1); end;
            }
        }
    }
    trigger OnInitXmlPort() var T: Codeunit "OT Target"; R: Integer; begin R := T.MXmlInit(1); end;
    trigger OnPreXmlPort() var T: Codeunit "OT Target"; R: Integer; begin R := T.MXmlPre(1); end;
    trigger OnPostXmlPort() var T: Codeunit "OT Target"; R: Integer; begin R := T.MXmlPost(1); end;
}

// A second xmlport, whose table element is another table: an import of the first writes none of its.
xmlport 73008 "OT Other XmlPort"
{
    Direction = Both;
    Format = Xml;
    UseRequestPage = false;
    schema { textelement(Root) { tableelement(Row; "OT Store") { fieldelement(PK; Row.PK) { } } } }
    trigger OnPreXmlPort() var T: Codeunit "OT Target"; R: Integer; begin R := T.MOtherXmlPre(1); end;
}

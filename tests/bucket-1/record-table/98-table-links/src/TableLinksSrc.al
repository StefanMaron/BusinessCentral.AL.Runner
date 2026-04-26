table 112000 "Links Test Table"
{
    DataClassification = CustomerContent;
    fields
    {
        field(1; Id; Integer) { }
        field(2; Name; Text[50]) { }
    }
    keys
    {
        key(PK; Id) { Clustered = true; }
    }
}

codeunit 112001 TableLinksSrc
{
    procedure HasLinks(var Rec: Record "Links Test Table"): Boolean
    begin
        exit(Rec.HasLinks());
    end;

    procedure AddLink(var Rec: Record "Links Test Table"; Url: Text; Description: Text): Integer
    begin
        exit(Rec.AddLink(Url, Description));
    end;

    procedure DeleteLinks(var Rec: Record "Links Test Table")
    begin
        Rec.DeleteLinks();
    end;

    procedure DeleteLink(var Rec: Record "Links Test Table"; LinkId: Integer)
    begin
        Rec.DeleteLink(LinkId);
    end;

    procedure CopyLinks(var Target: Record "Links Test Table"; Source: Record "Links Test Table")
    begin
        Target.CopyLinks(Source);
    end;
}

table 307800 "RRC Table"
{
    DataClassification = ToBeClassified;

    fields
    {
        field(1; "No."; Code[20])
        {
            DataClassification = ToBeClassified;
        }
        field(2; "Name"; Text[50])
        {
            DataClassification = ToBeClassified;
        }
        field(3; "Amount"; Decimal)
        {
            DataClassification = ToBeClassified;
        }
    }

    keys
    {
        key(PK; "No.")
        {
            Clustered = true;
        }
    }

    /// Called from within the table — exercises ALRecordId on the Record class itself.
    /// Uses the implicit Rec.RecordId() form.
    procedure GetOwnRecordId(): RecordId
    begin
        exit(Rec.RecordId());
    end;

    /// Called from within the table — exercises ALRecordId as a bare built-in call.
    /// BC emits ALRecordId directly on the Record class for bare RecordId() calls inside
    /// table procedures (issue #1330: CS1061 on Record<N>: missing ALRecordId).
    procedure GetOwnRecordIdBuiltin(): RecordId
    var
        RecId: RecordId;
    begin
        RecId := RecordId;
        exit(RecId);
    end;

    /// Called from within the table — exercises ALCurrentCompany as a bare built-in call.
    /// BC emits ALCurrentCompany directly on the Record class for bare CurrentCompany calls
    /// inside table procedures (issue #1330: CS1061 on Record<N>: missing ALCurrentCompany).
    procedure GetOwnCurrentCompanyBuiltin(): Text
    var
        Company: Text;
    begin
        Company := CurrentCompany;
        exit(Company);
    end;

    /// Called from within the table — exercises ALCurrentCompany on the Record class.
    procedure GetOwnCurrentCompany(): Text
    begin
        exit(Rec.CurrentCompany());
    end;
}

table 306000 "RRC Table"
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
    /// BC emits ALRecordId directly on the Record class for bare RecordId() calls.
    procedure GetOwnRecordIdBuiltin(): RecordId
    begin
        exit(RecordId());
    end;

    /// Called from within the table — exercises ALTestFieldNavValueSafe on the Record class.
    procedure ValidateNameField()
    begin
        Rec.TestField("Name");
    end;

    /// Called from within the table — exercises ALTestFieldNavValueSafe with a value arg.
    procedure ValidateNameFieldWithValue(ExpectedName: Text[50])
    begin
        Rec.TestField("Name", ExpectedName);
    end;

    /// Called from within the table — exercises ALCurrentCompany as a bare built-in call.
    procedure GetOwnCurrentCompanyBuiltin(): Text
    begin
        exit(CurrentCompany());
    end;

    /// Called from within the table — exercises ALCurrentCompany on the Record class.
    procedure GetOwnCurrentCompany(): Text
    begin
        exit(Rec.CurrentCompany());
    end;
}

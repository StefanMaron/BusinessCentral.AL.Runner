table 50102 "Audit Log Entry"
{
    DataClassification = ToBeClassified;

    fields
    {
        field(1; "Entry No."; Integer)
        {
            DataClassification = ToBeClassified;
        }
        field(2; "Table ID"; Integer)
        {
            DataClassification = ToBeClassified;
        }
        field(3; "Action"; Text[50])
        {
            DataClassification = ToBeClassified;
        }
    }

    keys
    {
        key(PK; "Entry No.")
        {
            Clustered = true;
        }
    }
}

table 50103 "Customer Balance"
{
    DataClassification = ToBeClassified;

    fields
    {
        field(1; "Customer No."; Code[20])
        {
            DataClassification = ToBeClassified;
        }
        field(2; "Balance"; Decimal)
        {
            DataClassification = ToBeClassified;
        }
    }

    keys
    {
        key(PK; "Customer No.")
        {
            Clustered = true;
        }
    }

    // In a real BC app, this trigger would fire after every Modify:
    //
    // trigger OnAfterModify()
    // var
    //     AuditEntry: Record "Audit Log Entry";
    // begin
    //     AuditEntry.Init();
    //     AuditEntry."Entry No." := GetNextEntryNo();
    //     AuditEntry."Table ID" := Database::"Customer Balance";
    //     AuditEntry."Action" := 'Modified';
    //     AuditEntry.Insert(true);
    // end;
    //
    // This subscriber is NOT fired by AL Runner because implicit DB event
    // subscribers are not supported. See the test codeunit for details.
}

codeunit 50105 "Balance Manager"
{
    procedure UpdateBalance(CustomerNo: Code[20]; NewBalance: Decimal)
    var
        CustBalance: Record "Customer Balance";
    begin
        if not CustBalance.Get(CustomerNo) then begin
            CustBalance.Init();
            CustBalance."Customer No." := CustomerNo;
            CustBalance."Balance" := NewBalance;
            CustBalance.Insert(true);
        end else begin
            CustBalance."Balance" := NewBalance;
            CustBalance.Modify(true);
        end;
    end;
}

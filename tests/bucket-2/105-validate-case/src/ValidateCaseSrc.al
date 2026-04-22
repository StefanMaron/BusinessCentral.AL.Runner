table 100010 "Case Validate Table"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; PK; Code[20]) { }
        field(2; "Rate Code"; Text[10])
        {
            trigger OnValidate()
            begin
                "Position" := GetPositionFromCode("Rate Code");
            end;
        }
        field(3; "Position"; Integer) { }
    }

    keys
    {
        key(PK; PK) { Clustered = true; }
    }

    local procedure GetPositionFromCode(RateCode: Text): Integer
    begin
        case RateCode of
            '23', '22':
                exit(1);
            '8', '7':
                exit(2);
            '5':
                exit(3);
            else
                exit(0);
        end;
    end;
}

/// Helper codeunit to test the case statement without going through trigger dispatch
codeunit 100012 "Case Helper"
{
    procedure GetPosition(RateCode: Text): Integer
    begin
        case RateCode of
            '23', '22':
                exit(1);
            '8', '7':
                exit(2);
            '5':
                exit(3);
            else
                exit(0);
        end;
    end;
}

table 56450 "SAV Product"
{
    fields
    {
        field(1; "No."; Code[20]) { }
        field(2; Name; Text[100]) { }
        field(3; "Validated Count"; Integer) { }

        field(4; Price; Decimal)
        {
            trigger OnValidate()
            begin
                // Running the OnValidate trigger as a side-effect lets the test
                // observe that Validate() actually ran (not just a no-op).
                Rec."Validated Count" += 1;
                if Price < 0 then
                    Error('Price must not be negative');
            end;
        }

        field(5; "Next Run Date Formula"; DateFormula)
        {
            // Matches the reporter's exact shape: DateFormula field populated
            // via Evaluate then handed to single-arg Validate.
            trigger OnValidate()
            begin
                Rec."Validated Count" += 1;
            end;
        }
    }
    keys
    {
        key(PK; "No.") { Clustered = true; }
    }
}

codeunit 56450 "SAV Configurator"
{
    procedure ApplyPriceFromText(var Prod: Record "SAV Product"; Text: Text)
    var
        Value: Decimal;
    begin
        Evaluate(Value, Text);
        Prod.Price := Value;
        // Single-arg Validate — validates the value already assigned to Price.
        Prod.Validate(Price);
    end;

    procedure ProbeNegativePrice(var Prod: Record "SAV Product")
    begin
        Prod.Price := -1;
        Prod.Validate(Price);
    end;

    procedure ApplyDateFormulaFromText(var Prod: Record "SAV Product"; Text: Text)
    var
        Formula: DateFormula;
    begin
        // Parallel to the reporter's Job Queue Entry shape, but via a local
        // because Evaluate directly into a record field hits a separate type
        // issue (NavText vs NavDateFormula) that's tracked elsewhere.
        Evaluate(Formula, Text);
        Prod."Next Run Date Formula" := Formula;
        Prod.Validate("Next Run Date Formula");
    end;
}

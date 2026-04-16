/// Report with a request page containing an editable field and a read-only field.
/// Used to prove that TestRequestPage.Editable returns the correct value.

report 61900 "TRE Report"
{
    Caption = 'TRE Report';

    dataset { }

    requestpage
    {
        layout
        {
            area(Content)
            {
                field(EditableField; EditableFieldValue)
                {
                    ApplicationArea = All;
                    Caption = 'Editable Field';
                    Editable = true;
                }
                field(ReadOnlyField; ReadOnlyFieldValue)
                {
                    ApplicationArea = All;
                    Caption = 'Read Only Field';
                    Editable = false;
                }
            }
        }
    }

    var
        EditableFieldValue: Text;
        ReadOnlyFieldValue: Text;
}

codeunit 61901 "TRE Helper"
{
    procedure RunReport()
    begin
        Report.Run(61900);
    end;

    /// Proving helper — returns a+b+1 to verify the codeunit is live.
    procedure AddWithBonus(a: Integer; b: Integer): Integer
    begin
        exit(a + b + 1);
    end;
}

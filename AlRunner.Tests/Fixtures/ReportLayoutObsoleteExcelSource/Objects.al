table 71860 "RLO Row"
{
    DataClassification = ToBeClassified;
    fields
    {
        field(1; "No."; Integer) { }
    }
    keys { key(PK; "No.") { Clustered = true; } }
}

// Four Excel layouts, one per state of the two columns under test:
//   Plain     — declares neither property: not obsolete, sheet configuration Default.
//   Retiring  — ObsoleteState = Pending: obsolete.
//   Sheets    — ExcelLayoutMultipleDataSheets = true: Multiple data sheets.
//   OneSheet  — ExcelLayoutMultipleDataSheets = false: Single Data sheet.
report 71861 "RLO Layouts"
{
    UseRequestPage = false;
    DefaultRenderingLayout = Plain;

    dataset
    {
        dataitem(Rows; "RLO Row")
        {
            column(No; "No.") { }
        }
    }

    rendering
    {
        layout(Plain)
        {
            Type = Excel;
            LayoutFile = 'RloLayout.xlsx';
        }
        layout(Retiring)
        {
            Type = Excel;
            LayoutFile = 'RloLayout.xlsx';
            ObsoleteState = Pending;
            ObsoleteReason = 'Replaced by Plain.';
            ObsoleteTag = '1.0';
        }
        layout(Sheets)
        {
            Type = Excel;
            LayoutFile = 'RloLayout.xlsx';
            ExcelLayoutMultipleDataSheets = true;
        }
        layout(OneSheet)
        {
            Type = Excel;
            LayoutFile = 'RloLayout.xlsx';
            ExcelLayoutMultipleDataSheets = false;
        }
    }
}

// Standalone Assert — this fixture imports nothing.
codeunit 71862 "RLO Assert"
{
    procedure AreEqual(Expected: Variant; Actual: Variant; Msg: Text)
    begin
        if Format(Expected) <> Format(Actual) then
            Error('Expected %1 but got %2: %3', Format(Expected), Format(Actual), Msg);
    end;
}

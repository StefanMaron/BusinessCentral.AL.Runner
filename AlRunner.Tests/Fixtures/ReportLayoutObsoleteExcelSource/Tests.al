codeunit 71863 "RLO Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "RLO Assert";

    local procedure Row(Name: Text; var Layouts: Record "Report Layout List")
    begin
        Layouts.SetRange("Report ID", Report::"RLO Layouts");
        Layouts.SetRange(Name, Name);
        Assert.AreEqual(1, Layouts.Count(), 'report 71861 declares exactly one layout named ' + Name);
        Layouts.FindFirst();
    end;

    [Test]
    procedure PendingObsoleteLayoutIsObsolete()
    var
        Layouts: Record "Report Layout List";
    begin
        Row('Retiring', Layouts);
        Assert.AreEqual(true, Layouts.IsObsolete, 'Retiring declares ObsoleteState = Pending');
    end;

    [Test]
    procedure LayoutWithoutObsoleteStateIsNotObsolete()
    var
        Layouts: Record "Report Layout List";
    begin
        Row('Plain', Layouts);
        Assert.AreEqual(false, Layouts.IsObsolete, 'Plain declares no ObsoleteState');
    end;

    [Test]
    procedure MultipleDataSheetsTrueIsMultiple()
    var
        Layouts: Record "Report Layout List";
    begin
        Row('Sheets', Layouts);
        Assert.AreEqual(Layouts.ExcelLayoutMultipleDataSheets::"Multiple data sheets", Layouts.ExcelLayoutMultipleDataSheets, 'Sheets declares ExcelLayoutMultipleDataSheets = true');
    end;

    [Test]
    procedure MultipleDataSheetsFalseIsSingle()
    var
        Layouts: Record "Report Layout List";
    begin
        Row('OneSheet', Layouts);
        Assert.AreEqual(Layouts.ExcelLayoutMultipleDataSheets::"Single Data sheet", Layouts.ExcelLayoutMultipleDataSheets, 'OneSheet declares ExcelLayoutMultipleDataSheets = false');
    end;

    [Test]
    procedure UndeclaredSheetPropertyIsDefault()
    var
        Layouts: Record "Report Layout List";
    begin
        Row('Plain', Layouts);
        Assert.AreEqual(Layouts.ExcelLayoutMultipleDataSheets::Default, Layouts.ExcelLayoutMultipleDataSheets, 'Plain declares no ExcelLayoutMultipleDataSheets');
    end;
}

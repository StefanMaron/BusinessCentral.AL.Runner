/// <summary>#5301: where the test bundle's members are generated, into its own compile.</summary>
codeunit 72710 "TPLT Target"
{
    procedure Placeholder()
    begin
    end;
}

/// <summary>An OnInsert trigger the test bundle adds to the library's table: it runs for every insert of it,
/// whichever bundle starts one.</summary>
tableextension 72711 "TPLT Rec Ext" extends "TPL Rec"
{
    trigger OnInsert()
    var T: Codeunit "TPLT Target"; R: Integer;
    begin
        R := T.MTestExtInsert(1);
    end;
}

codeunit 72712 "TPLT Tests"
{
    Subtype = Test;

    [Test]
    procedure OpenNew_StartsTheInsertOfTheLibrarysTable()
    var P: TestPage "TPL Card";
    begin
        P.OpenNew();
        P.Close();
    end;

    [Test]
    procedure SetValue_StartsTheValidateAndInsertOfTheLibrarysTable()
    var R: Record "TPL Rec"; P: TestPage "TPL Card";
    begin
        R.PK := 'S';
        R.Insert(false);
        P.OpenEdit();
        P.GoToKey('S');
        P.Qty.SetValue(2);
        P.Close();
    end;

    [Test]
    procedure SetValue_OnAControlOfALibrarysPageExtension_StartsTheBaseTablesTriggersOnly()
    var R: Record "TPL Rec"; P: TestPage "TPL Card";
    begin
        R.PK := 'X';
        R.Insert(false);
        P.OpenEdit();
        P.GoToKey('X');
        P.ExtQty.SetValue(4);
    end;

    [Test]
    procedure LibraryHelperOpeningANewRecord_StartsTheInsertOfTheTableExtensionInThisBundle()
    var Helper: Codeunit "TPL Helper";
    begin
        Helper.CreateThroughThePage();
    end;

    [Test]
    procedure OpenEditAndRead_IsNotAnnotated()
    var R: Record "TPL Rec"; P: TestPage "TPL Card"; Seen: Text;
    begin
        R.PK := 'R';
        R.Insert(false);
        P.OpenEdit();
        P.GoToKey('R');
        Seen := P.PK.Value;
        P.Close();
    end;
}

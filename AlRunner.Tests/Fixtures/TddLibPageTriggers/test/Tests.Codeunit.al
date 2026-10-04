/// <summary>#5309: where the test bundle's members are generated, into its own compile.</summary>
codeunit 72830 "TPGT Target"
{
    procedure Placeholder()
    begin
    end;
}

/// <summary>Triggers the test bundle adds to the library's page: they run for every open of it, whichever bundle
/// opens it.</summary>
pageextension 72831 "TPGT Card Ext" extends "TPG Card"
{
    layout
    {
        addlast(Content)
        {
            field(ExtQty; Rec.Qty)
            {
                trigger OnValidate() var T: Codeunit "TPGT Target"; R: Integer; begin R := T.MTestExtValidate(1); end;
            }
        }
    }
    trigger OnAfterGetRecord() var T: Codeunit "TPGT Target"; R: Integer; begin R := T.MTestExtAfterGet(1); end;
}

codeunit 72832 "TPGT Subs"
{
    [EventSubscriber(ObjectType::Page, Page::"TPG Card", 'OnClosePageEvent', '', false, false)]
    local procedure OnClose(var Rec: Record "TPG Rec") var T: Codeunit "TPGT Target"; R: Integer; begin R := T.MTestEvtClose(1); end;
}

codeunit 72833 "TPGT Tests"
{
    Subtype = Test;

    [Test]
    procedure OpenView_StartsTheLibrarysOnOpenPageAndTheExtensionsRowTrigger()
    var R: Record "TPG Rec"; P: TestPage "TPG Card";
    begin
        R.PK := 'A';
        R.Insert();
        P.OpenView();
    end;

    [Test]
    procedure Invoke_OnALibraryAction_StartsThatActionsOnActionOnly()
    var R: Record "TPG Rec"; P: TestPage "TPG Card";
    begin
        R.PK := 'B';
        R.Insert();
        P.OpenView();
        P.Go.Invoke();
    end;

    [Test]
    procedure SetValue_OnAControlOfThePageExtension_StartsTheExtensionsFieldTrigger()
    var R: Record "TPG Rec"; P: TestPage "TPG Card";
    begin
        R.PK := 'C';
        R.Insert();
        P.OpenEdit();
        P.ExtQty.SetValue(3);
    end;

    [Test]
    procedure Close_StartsTheSubscriberOfThePagesCloseEventInThisBundle()
    var R: Record "TPG Rec"; P: TestPage "TPG Card";
    begin
        R.PK := 'D';
        R.Insert();
        P.OpenView();
        P.Close();
    end;

    [Test]
    procedure LibraryHelperOpeningThePage_StartsWhatThisBundleAddsToIt()
    var Helper: Codeunit "TPG Helper";
    begin
        Helper.OpenThePage();
    end;

    [Test]
    procedure NoPage_IsNotAnnotated()
    var R: Record "TPG Rec";
    begin
        R.PK := 'E';
        R.Insert();
    end;
}

/// Tests MockCurrPage.PromptMode and MockFormHandle.PromptMode stubs.
/// CurrPage.PromptMode is only valid in PromptDialog page/pageextension context.
/// Compilation of the pageextension proves the C# mock has the property.

page 113000 "PM Test Page"
{
    PageType = PromptDialog;
    ApplicationArea = All;
    UsageCategory = None;

    procedure SetPromptModeToContent()
    begin
        CurrPage.PromptMode := PromptMode::Content;
    end;
}

pageextension 113001 "PM Page Ext" extends "PM Test Page"
{
    trigger OnOpenPage()
    begin
        // Round-trip: read then write PromptMode — proves getter and setter both exist.
        CurrPage.PromptMode := CurrPage.PromptMode;
    end;
}

codeunit 113002 "PM Helper"
{
    procedure ExpectedDefaultOrdinal(): Integer
    begin
        exit(0); // Enum "Prompt Mode"::Prompt = ordinal 0
    end;

    procedure ExpectedEditOrdinal(): Integer
    begin
        exit(1); // Enum "Prompt Mode"::Edit = ordinal 1
    end;

    procedure OrdinalsDiffer(): Boolean
    begin
        exit(0 <> 1);
    end;
}

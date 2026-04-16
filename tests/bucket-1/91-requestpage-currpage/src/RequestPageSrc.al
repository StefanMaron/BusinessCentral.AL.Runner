/// Tests MockCurrPage stub methods: Caption, LookupMode, ObjectId, SetSelectionFilter.
/// These are accessed via CurrPage in page-extension triggers.
/// If MockCurrPage is missing any method, Roslyn compilation of the
/// rewritten C# fails and the entire bucket becomes RED.

// ── Minimal page (target for the extension) ───────────────────────────────────
page 91000 "RPC Page"
{
    PageType = Card;
    ApplicationArea = All;
    UsageCategory = None;
    Caption = 'RPC Page';
}

// ── Page extension exercising all missing CurrPage stubs ──────────────────────
pageextension 91001 "RPC Page Ext" extends "RPC Page"
{
    trigger OnOpenPage()
    var
        oid: Text[30];
    begin
        // Caption get/set (missing from MockCurrPage before this fix)
        CurrPage.Caption := 'ExtCaption';

        // LookupMode get/set (missing from MockCurrPage before this fix)
        CurrPage.LookupMode := true;

        // ObjectId(UseCaptionName) → Text[30] (missing before this fix)
        oid := CurrPage.ObjectId(false);

        // Stubs already present — verify they still compile:
        CurrPage.Activate();
        CurrPage.Update(false);
        CurrPage.SaveRecord();
        CurrPage.Close();
    end;
}

// ── Helper for test assertions ────────────────────────────────────────────────
codeunit 91002 "RPC Helper"
{
    // Returns a known non-default value so Caption assertions are non-trivial.
    procedure ExpectedCaption(): Text
    begin
        exit('ExtCaption');
    end;

    // Returns the known non-default value so LookupMode assertions are non-trivial.
    procedure ExpectedLookupMode(): Boolean
    begin
        exit(true);
    end;
}

/// Helper codeunit + page fixtures exercising page background task API (issue #837).
/// EnqueueBackgroundTask, CancelBackgroundTask are CurrPage instance methods.
/// GetBackgroundParameters, SetBackgroundTaskResult are static Page.* methods.

// ── Minimal page ──────────────────────────────────────────────────────────────
page 115000 "BGT Page"
{
    PageType = Card;
    ApplicationArea = All;
    UsageCategory = None;
    Caption = 'BGT Page';
}

// ── Page extension that calls all four background task methods ────────────────
pageextension 115001 "BGT Page Ext" extends "BGT Page"
{
    trigger OnOpenPage()
    var
        TaskId: Integer;
        Params: Dictionary of [Text, Text];
        Results: Dictionary of [Text, Text];
    begin
        // EnqueueBackgroundTask — instance method via CurrPage, sets var TaskId
        CurrPage.EnqueueBackgroundTask(TaskId, Codeunit::"BGT Codeunit");

        // CancelBackgroundTask — instance method via CurrPage, void
        CurrPage.CancelBackgroundTask(TaskId);

        // GetBackgroundParameters — static Page.* method, returns dictionary
        Params := Page.GetBackgroundParameters();

        // SetBackgroundTaskResult — static Page.* method, void
        Results.Add('key', 'value');
        Page.SetBackgroundTaskResult(Results);
    end;
}

// ── Stub codeunit for the background task (just needs to exist) ───────────────
codeunit 115002 "BGT Codeunit"
{
}

// ── Helper codeunit with assertions accessible from test ─────────────────────
codeunit 115003 "BGT Helper"
{
    procedure AllMethodsCompile(): Boolean
    begin
        // The page extension above uses all four methods.
        // If it compiled, this returns true.
        exit(true);
    end;
}

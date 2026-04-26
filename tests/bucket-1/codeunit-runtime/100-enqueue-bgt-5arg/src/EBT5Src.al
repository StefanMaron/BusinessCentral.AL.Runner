// Fixtures for CurrPage.EnqueueBackgroundTask with 5 AL args
// (taskId, codeunitId, parameters, timeout, errorLevel) — issue #1327.
// BC compiler emits this as a 6-arg C# call:
// (DataError, ByRef taskId, int codeunitId, NavDictionary params, int timeout, NavOption errorLevel).

// ── Minimal page ─────────────────────────────────────────────────────────────
page 307300 "EBT5 Page"
{
    PageType = Card;
    ApplicationArea = All;
    UsageCategory = None;
    Caption = 'EBT5 Page';
}

// ── Page extension exercising all overloads of EnqueueBackgroundTask ──────────
pageextension 307301 "EBT5 Page Ext" extends "EBT5 Page"
{
    var
        TaskId1: Integer;
        TaskId2: Integer;
        TaskId3: Integer;
        TaskId4: Integer;
        Params: Dictionary of [Text, Text];

    trigger OnOpenPage()
    begin
        Params.Add('key', 'val');

        // 2-arg: taskId, codeunitId
        CurrPage.EnqueueBackgroundTask(TaskId1, Codeunit::"EBT5 Worker");

        // 3-arg: taskId, codeunitId, params
        CurrPage.EnqueueBackgroundTask(TaskId2, Codeunit::"EBT5 Worker", Params);

        // 4-arg: taskId, codeunitId, params, timeout
        CurrPage.EnqueueBackgroundTask(TaskId3, Codeunit::"EBT5 Worker", Params, 60000);

        // 5-arg: taskId, codeunitId, params, timeout, errorLevel — THE NEW OVERLOAD (issue #1327)
        CurrPage.EnqueueBackgroundTask(TaskId4, Codeunit::"EBT5 Worker", Params, 600000, PageBackgroundTaskErrorLevel::Error);
    end;
}

// ── Worker codeunit (just needs to exist) ────────────────────────────────────
codeunit 307302 "EBT5 Worker"
{
}

// ── Helper accessible from the test codeunit ─────────────────────────────────
codeunit 307303 "EBT5 Helper"
{
    procedure AllOverloadsCompile(): Boolean
    begin
        // The page extension above calls all overloads including the 5-arg form.
        // If it compiled, this returns true.
        exit(true);
    end;
}

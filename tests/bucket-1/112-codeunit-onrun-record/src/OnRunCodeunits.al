/// <summary>
/// Codeunit with TableNo — processes the record passed via Codeunit.Run.
/// OnRun receives the calling record via the implicit Rec parameter.
/// </summary>
codeunit 80101 "OnRun Processor"
{
    TableNo = "OnRun Test Table";
    trigger OnRun()
    begin
        Rec.Description := 'processed';
        Rec."Processed By" := 'RUNNER';
        Rec.Modify();
    end;
}

/// <summary>
/// Codeunit with TableNo that increments a counter on each run.
/// Used to verify record modifications are visible to the caller (var semantics).
/// </summary>
codeunit 80102 "OnRun Counter"
{
    TableNo = "OnRun Test Table";
    trigger OnRun()
    begin
        Rec.Counter := Rec.Counter + 10;
        Rec.Modify();
    end;
}

/// <summary>
/// Parameterless OnRun — verifies that codeunits without TableNo still dispatch
/// correctly via Codeunit.Run after the record-parameter reflection changes.
/// </summary>
codeunit 80103 "OnRun No Params"
{
    trigger OnRun()
    var
        Rec: Record "OnRun Test Table";
    begin
        Rec.Init();
        Rec."No." := 'NOPARAMS';
        Rec.Description := 'no-params-ran';
        Rec.Insert();
    end;
}

/// <summary>
/// Codeunit that uses StartSession with a record parameter.
/// </summary>
codeunit 80104 "OnRun Session Processor"
{
    TableNo = "OnRun Test Table";
    trigger OnRun()
    begin
        Rec.Description := 'session-processed';
        Rec.Modify();
    end;
}

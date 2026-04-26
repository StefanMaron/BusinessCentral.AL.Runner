/// Helper codeunit exercising ErrorInfo.AddAction() with 3-arg and 4-arg forms.
/// Avoid ErrorInfo.Create(msg) — it hits a separate DLL-loading gap (loads
/// Microsoft.Dynamics.Nav.CodeAnalysis 16.4.x which is absent standalone).
/// Default-initialised ErrorInfo values work fine for AddAction tests.
codeunit 59890 "EIA Src"
{
    procedure AddSingleAction(caption: Text): Boolean
    var
        ei: ErrorInfo;
    begin
        ei.AddAction(caption, Codeunit::"EIA Src", 'DoFix');
        exit(true);
    end;

    procedure AddSingleActionWithDescription(caption: Text; desc: Text): Boolean
    var
        ei: ErrorInfo;
    begin
        ei.AddAction(caption, Codeunit::"EIA Src", 'DoFix', desc);
        exit(true);
    end;

    procedure AddMultipleActions(): Boolean
    var
        ei: ErrorInfo;
    begin
        ei.AddAction('Action 1', Codeunit::"EIA Src", 'DoFix');
        ei.AddAction('Action 2', Codeunit::"EIA Src", 'DoFix');
        ei.AddAction('Action 3', Codeunit::"EIA Src", 'DoFix');
        exit(true);
    end;

    /// Referenced by AddAction as the target method. Runner doesn't fire
    /// interactive actions; this is here for AL compilability.
    procedure DoFix(ei: ErrorInfo)
    begin
    end;
}

/// Helper codeunit exercising ErrorInfo.AddNavigationAction() with 3-arg and 4-arg forms.
/// Uses default-initialised ErrorInfo values — ErrorInfo.Create() hits a separate
/// DLL-loading gap and is deliberately avoided here.
/// Page IDs are passed as integer literals to avoid Page:: references that require
/// base-app objects not present in the test compilation unit.
codeunit 81250 "EINA Src"
{
    procedure AddNavigationAction_3Arg(caption: Text): Boolean
    var
        ei: ErrorInfo;
    begin
        ei.AddNavigationAction(caption, 22, '');
        exit(true);
    end;

    procedure AddNavigationAction_4Arg(caption: Text; desc: Text): Boolean
    var
        ei: ErrorInfo;
    begin
        ei.AddNavigationAction(caption, 22, '', desc);
        exit(true);
    end;

    procedure AddNavigationAction_WithMessage(): Text
    var
        ei: ErrorInfo;
    begin
        ei.Message('Record not found');
        ei.AddNavigationAction('Open List', 22, '');
        exit(ei.Message());
    end;

    procedure AddMultipleNavigationActions(): Boolean
    var
        ei: ErrorInfo;
    begin
        ei.AddNavigationAction('Action 1', 22, '');
        ei.AddNavigationAction('Action 2', 22, '');
        exit(true);
    end;
}

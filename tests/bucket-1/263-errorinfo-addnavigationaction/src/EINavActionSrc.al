/// Helper codeunit exercising ErrorInfo.AddNavigationAction() with 1-arg and 2-arg forms.
/// The BC AL compiler exposes:
///   AddNavigationAction(Caption: Text)
///   AddNavigationAction(Caption: Text; Description: Text)
/// Uses default-initialised ErrorInfo values — ErrorInfo.Create() hits a separate
/// DLL-loading gap and is deliberately avoided here.
codeunit 81250 "EINA Src"
{
    procedure AddNavigationAction_1Arg(caption: Text): Boolean
    var
        ei: ErrorInfo;
    begin
        ei.AddNavigationAction(caption);
        exit(true);
    end;

    procedure AddNavigationAction_2Arg(caption: Text; desc: Text): Boolean
    var
        ei: ErrorInfo;
    begin
        ei.AddNavigationAction(caption, desc);
        exit(true);
    end;

    procedure AddNavigationAction_WithMessage(): Text
    var
        ei: ErrorInfo;
    begin
        ei.Message('Record not found');
        ei.AddNavigationAction('Open List');
        exit(ei.Message());
    end;

    procedure AddMultipleNavigationActions(): Boolean
    var
        ei: ErrorInfo;
    begin
        ei.AddNavigationAction('Action 1');
        ei.AddNavigationAction('Action 2');
        exit(true);
    end;
}

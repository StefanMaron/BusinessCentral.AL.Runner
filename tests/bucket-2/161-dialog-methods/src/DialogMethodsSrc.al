/// Helper codeunit exercising the additional Dialog methods per issue #542:
/// - Dialog.HideSubsequentDialogs (instance method, no-op stub standalone)
/// - Dialog.LogInternalError (STATIC method, no-op stub standalone)
///
/// Dialog.Error and Dialog.Message are already covered by existing tests
/// (MessageHandler/Assert.ExpectedError suites); this suite is specifically
/// for the two less-common methods that were flagged as gaps.
codeunit 59740 "DLGM Src"
{
    procedure CallHideSubsequentDialogs(hide: Boolean)
    var
        dlg: Dialog;
    begin
        dlg.HideSubsequentDialogs(hide);
    end;

    procedure CallHideAndReturnFlag(hide: Boolean): Boolean
    var
        dlg: Dialog;
    begin
        dlg.HideSubsequentDialogs(hide);
        exit(true);
    end;

    procedure CallLogInternalError(msg: Text)
    begin
        Dialog.LogInternalError(msg, DataClassification::SystemMetadata, Verbosity::Normal);
    end;

    procedure CallBothAndReturnFlag(msg: Text): Boolean
    var
        dlg: Dialog;
    begin
        dlg.HideSubsequentDialogs(true);
        Dialog.LogInternalError(msg, DataClassification::SystemMetadata, Verbosity::Normal);
        exit(true);
    end;
}

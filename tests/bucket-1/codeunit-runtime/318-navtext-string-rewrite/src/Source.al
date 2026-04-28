// Source codeunit for issue #1528 — CS0029 string→NavText rewriter fix.
//
// Root cause: the RoslynRewriter intercepts all receiver.ToText(arg) calls
// (where arg is any argument, guarded by Count >= 1) and replaces them with
// AlCompat.Format(receiver) which returns C# string. For BC runtime types
// this is fine because the call is always inside new NavText(…), but for
// user-defined AL procedures named ToText(), BC scope classes generate
// _parent.ToText(this) (passing the scope as the first arg). After rewriting
// that becomes AlCompat.Format(_parent) which returns string, but the
// assignment target is NavText — CS0029.
//
// The fix: exclude calls where the first argument is bare `this`, which
// signals a user-defined scope→parent call, not a BC runtime session call.
codeunit 1318001 "NavText Rewrite Source"
{
    // User-defined ToText() — no parameters. When called from within the
    // same codeunit via a scope class, BC generates _parent.ToText(this).
    // The rewriter must NOT replace that with AlCompat.Format(_parent).
    procedure ToText(): Text
    begin
        exit('hello-from-totext');
    end;

    // Caller: calls self's ToText() and assigns to a Text variable.
    // The assignment target is NavText in BC C#; if the rewriter wrongly
    // replaces _parent.ToText(this) with AlCompat.Format(_parent) (string),
    // Roslyn rejects it with CS0029.
    procedure GetTextViaToText(): Text
    var
        Result: Text;
    begin
        Result := ToText();
        exit(Result);
    end;

    // Database.TenantId() — rewriter replaces with "STANDALONE"
    procedure GetTenantId(): Text
    begin
        exit(Database.TenantId());
    end;

    // Database.SerialNumber() — rewriter replaces with "STANDALONE"
    procedure GetSerialNumber(): Text
    begin
        exit(Database.SerialNumber());
    end;

    // SessionInformation.Callstack() — rewriter replaces with ""
    procedure GetCallStack(): Text
    begin
        exit(SessionInformation.Callstack());
    end;

    // Session.ApplicationIdentifier() — rewriter replaces with ""
    procedure GetApplicationIdentifier(): Text
    begin
        exit(Session.ApplicationIdentifier());
    end;
}

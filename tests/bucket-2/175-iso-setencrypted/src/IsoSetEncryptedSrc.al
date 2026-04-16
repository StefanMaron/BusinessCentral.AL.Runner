/// Helper codeunit exercising IsolatedStorage.SetEncrypted.
codeunit 59995 "ISE Src"
{
    procedure StoreEncrypted2Arg(keyName: Text; value: Text)
    begin
        IsolatedStorage.SetEncrypted(keyName, value);
    end;

    procedure StoreEncrypted3Arg(keyName: Text; value: Text; scope: DataScope)
    begin
        IsolatedStorage.SetEncrypted(keyName, value, scope);
    end;

    procedure StoreAndRetrieve_2Arg(keyName: Text; value: Text): Text
    var
        outValue: Text;
    begin
        IsolatedStorage.SetEncrypted(keyName, value);
        IsolatedStorage.Get(keyName, outValue);
        exit(outValue);
    end;

    procedure StoreAndContains(keyName: Text; value: Text): Boolean
    begin
        IsolatedStorage.SetEncrypted(keyName, value);
        exit(IsolatedStorage.Contains(keyName));
    end;
}

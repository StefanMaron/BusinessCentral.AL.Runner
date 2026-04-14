codeunit 50116 "Iso Storage Wrapper"
{
    procedure SetValue(StorageKey: Text; StorageValue: Text)
    begin
        IsolatedStorage.Set(StorageKey, StorageValue);
    end;

    procedure GetValue(StorageKey: Text; var Result: Text): Boolean
    begin
        exit(IsolatedStorage.Get(StorageKey, Result));
    end;

    procedure HasKey(StorageKey: Text): Boolean
    begin
        exit(IsolatedStorage.Contains(StorageKey));
    end;

    procedure RemoveKey(StorageKey: Text): Boolean
    begin
        exit(IsolatedStorage.Delete(StorageKey));
    end;
}

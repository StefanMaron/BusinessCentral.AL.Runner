codeunit 50224 SecretStoreHelper
{
    /// <summary>
    /// Stores a secret text value in IsolatedStorage.
    /// </summary>
    procedure StoreSecret(StorageKey: Text; Value: SecretText)
    begin
        IsolatedStorage.Set(StorageKey, Value, DataScope::Module);
    end;

    /// <summary>
    /// Retrieves a secret text value from IsolatedStorage.
    /// Returns true if the key exists; SecretValue is set via ByRef.
    /// </summary>
    procedure GetSecret(StorageKey: Text; var SecretValue: SecretText): Boolean
    begin
        exit(IsolatedStorage.Get(StorageKey, DataScope::Module, SecretValue));
    end;

    /// <summary>
    /// Checks whether a key exists in IsolatedStorage.
    /// </summary>
    procedure HasSecret(StorageKey: Text): Boolean
    begin
        exit(IsolatedStorage.Contains(StorageKey, DataScope::Module));
    end;

    /// <summary>
    /// Deletes a key from IsolatedStorage.
    /// </summary>
    procedure RemoveSecret(StorageKey: Text)
    begin
        if IsolatedStorage.Contains(StorageKey, DataScope::Module) then
            IsolatedStorage.Delete(StorageKey, DataScope::Module);
    end;

    /// <summary>
    /// Converts a Text to SecretText and stores it.
    /// This exercises ALCompiler.ToSecretText in the transpiled code.
    /// </summary>
    procedure StoreFromText(StorageKey: Text; PlainValue: Text)
    var
        SecretVal: SecretText;
    begin
        SecretVal := PlainValue;
        IsolatedStorage.Set(StorageKey, SecretVal, DataScope::Module);
    end;
}

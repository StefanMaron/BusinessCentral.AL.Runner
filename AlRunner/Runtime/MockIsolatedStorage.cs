namespace AlRunner.Runtime;

using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;

/// <summary>
/// In-memory replacement for ALIsolatedStorage.
/// IsolatedStorage in BC is a key-value store backed by the database.
/// We mock it the same way we mock record access — pure in-memory.
/// </summary>
public static class MockIsolatedStorage
{
    private static readonly Dictionary<string, string> _store = new();

    public static void ResetAll() => _store.Clear();

    // ALSet overloads (with DataScope parameter — original BC signature)
    public static void ALSet(DataError errorLevel, string key, string value, object dataScope)
    {
        _store[key] = value;
    }

    public static void ALSet(DataError errorLevel, string key, NavSecretText value, object dataScope)
    {
        _store[key] = value.ToString() ?? "";
    }

    public static void ALSet(DataError errorLevel, string key, string value, object dataScope, object encryption)
    {
        _store[key] = value;
    }

    public static void ALSet(DataError errorLevel, string key, NavSecretText value, object dataScope, object encryption)
    {
        _store[key] = value.ToString() ?? "";
    }

    // ALSet overloads (without DataScope — transpiler strips it for simple IsolatedStorage.Set calls)
    public static void ALSet(DataError errorLevel, string key, string value)
    {
        _store[key] = value;
    }

    public static void ALSet(DataError errorLevel, string key, NavSecretText value)
    {
        _store[key] = value.ToString() ?? "";
    }

    // ALGet overloads (with DataScope)
    public static bool ALGet(DataError errorLevel, string key, object dataScope, out NavText value)
    {
        if (_store.TryGetValue(key, out var v))
        {
            value = new NavText(v);
            return true;
        }
        value = new NavText("");
        return false;
    }

    public static bool ALGet(DataError errorLevel, string key, object dataScope, out NavSecretText value)
    {
        if (_store.TryGetValue(key, out var v))
        {
            value = NavSecretText.Create(v);
            return true;
        }
        value = NavSecretText.Create("");
        return false;
    }

    // ALGet with DataScope + ByRef (actual transpiler output)
    public static bool ALGet(DataError errorLevel, string key, object dataScope, ByRef<NavSecretText> value)
    {
        if (_store.TryGetValue(key, out var v))
        {
            value.Value = NavSecretText.Create(v);
            return true;
        }
        value.Value = NavSecretText.Create("");
        return false;
    }

    public static bool ALGet(DataError errorLevel, string key, object dataScope, ByRef<NavText> value)
    {
        if (_store.TryGetValue(key, out var v))
        {
            value.Value = new NavText(v);
            return true;
        }
        value.Value = new NavText("");
        return false;
    }

    // ALGet overloads (without DataScope — transpiler output for simple IsolatedStorage.Get)
    // Transpiler uses ByRef<NavText> instead of out parameter
    public static bool ALGet(DataError errorLevel, string key, ByRef<NavText> value)
    {
        if (_store.TryGetValue(key, out var v))
        {
            value.Value = new NavText(v);
            return true;
        }
        value.Value = new NavText("");
        return false;
    }

    public static bool ALGet(DataError errorLevel, string key, ByRef<NavSecretText> value)
    {
        if (_store.TryGetValue(key, out var v))
        {
            value.Value = NavSecretText.Create(v);
            return true;
        }
        value.Value = NavSecretText.Create("");
        return false;
    }

    // ALContains (with DataScope)
    public static bool ALContains(DataError errorLevel, string key, object dataScope)
    {
        return _store.ContainsKey(key);
    }

    // ALContains (without DataError — transpiler sometimes omits it)
    public static bool ALContains(string key, object dataScope)
    {
        return _store.ContainsKey(key);
    }

    public static bool ALContains(string key)
    {
        return _store.ContainsKey(key);
    }

    // ALDelete (with DataScope)
    public static bool ALDelete(DataError errorLevel, string key, object dataScope)
    {
        return _store.Remove(key);
    }

    // ALDelete (without DataScope)
    public static bool ALDelete(DataError errorLevel, string key)
    {
        return _store.Remove(key);
    }
}

namespace AlRunner.Runtime;

using System.Reflection;
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

    /// <summary>Deep-clones the store for the --init-events baseline.</summary>
    public static Dictionary<string, string> Snapshot() => new(_store);

    /// <summary>Replaces the store with a clone of the given snapshot.</summary>
    public static void RestoreSnapshot(Dictionary<string, string> snapshot)
    {
        _store.Clear();
        foreach (var kv in snapshot) _store[kv.Key] = kv.Value;
    }

    // ALSet overloads (with DataScope parameter — original BC signature)
    // BC AL: IsolatedStorage.Set returns Boolean (true on success) — issue #1432.
    public static bool ALSet(DataError errorLevel, string key, string value, object dataScope)
    {
        _store[key] = value;
        return true;
    }

    public static bool ALSet(DataError errorLevel, string key, NavSecretText value, object dataScope)
    {
        _store[key] = ExtractSecretValue(value);
        return true;
    }

    public static bool ALSet(DataError errorLevel, string key, string value, object dataScope, object encryption)
    {
        _store[key] = value;
        return true;
    }

    public static bool ALSet(DataError errorLevel, string key, NavSecretText value, object dataScope, object encryption)
    {
        _store[key] = ExtractSecretValue(value);
        return true;
    }

    // ALSet overloads (without DataScope — transpiler strips it for simple IsolatedStorage.Set calls)
    public static bool ALSet(DataError errorLevel, string key, string value)
    {
        _store[key] = value;
        return true;
    }

    public static bool ALSet(DataError errorLevel, string key, NavSecretText value)
    {
        _store[key] = ExtractSecretValue(value);
        return true;
    }

    // ALSetEncrypted — encrypted variant. In the mock, encryption is transparent
    // (no crypto); the value round-trips through Get/Contains like a plain ALSet.
    // Overloads cover the same arg shapes as ALSet: with/without DataScope, and
    // Text vs NavSecretText value.
    // BC AL: IsolatedStorage.SetEncrypted returns Boolean (true on success) — issue #1432.
    public static bool ALSetEncrypted(DataError errorLevel, string key, string value)
    {
        _store[key] = value;
        return true;
    }

    public static bool ALSetEncrypted(DataError errorLevel, string key, NavSecretText value)
    {
        _store[key] = ExtractSecretValue(value);
        return true;
    }

    public static bool ALSetEncrypted(DataError errorLevel, string key, string value, object dataScope)
    {
        _store[key] = value;
        return true;
    }

    public static bool ALSetEncrypted(DataError errorLevel, string key, NavSecretText value, object dataScope)
    {
        _store[key] = ExtractSecretValue(value);
        return true;
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

    /// <summary>
    /// Extracts the plain text value from a NavSecretText.
    /// NavSecretText.ToString() returns masked text ("***"), so we use reflection
    /// to access the internal value field. Falls back to ToString() if reflection fails.
    /// </summary>
    private static string ExtractSecretValue(NavSecretText secret)
    {
        if (secret.ALIsEmpty()) return "";

        // Try to find the internal field that holds the actual value.
        // NavSecretText is a struct with an internal string field.
        var flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        foreach (var field in typeof(NavSecretText).GetFields(flags))
        {
            if (field.FieldType == typeof(string))
            {
                var val = field.GetValue(secret);
                if (val is string s && !string.IsNullOrEmpty(s))
                    return s;
            }
        }

        // Fallback: try properties
        foreach (var prop in typeof(NavSecretText).GetProperties(flags))
        {
            if (prop.PropertyType == typeof(string) && prop.CanRead)
            {
                try
                {
                    var val = prop.GetValue(secret);
                    if (val is string s && !string.IsNullOrEmpty(s))
                        return s;
                }
                catch { }
            }
        }

        // Last resort
        return secret.ToString() ?? "";
    }
}

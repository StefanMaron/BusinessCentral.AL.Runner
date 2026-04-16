namespace AlRunner.Runtime;

using System.Globalization;
using System.Reflection;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

/// <summary>
/// Helper methods for JSON operations that bypass TrappableOperationExecutor
/// and NavCurrentThread.Session, which crash in standalone mode.
///
/// The real NavJsonToken.ALWriteTo/ALReadFrom/ALSelectToken/ALSelectTokens
/// all go through TrappableOperationExecutor → NavEnvironment → crash.
/// These helpers access the internal BackingToken via reflection and
/// perform the JSON operations directly using Newtonsoft.Json.
/// </summary>
public static class MockJsonHelper
{
    private static readonly PropertyInfo BackingTokenProp =
        typeof(NavJsonToken).GetProperty("BackingToken",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static readonly MethodInfo SetBackingTokenMethod =
        typeof(NavJsonToken).GetMethod("SetBackingToken",
            BindingFlags.Instance | BindingFlags.NonPublic,
            null, new[] { typeof(JToken), typeof(bool) }, null)!;

    private static JToken GetBackingToken(NavJsonToken token)
        => (JToken)BackingTokenProp.GetValue(token)!;

    private static void SetBackingToken(NavJsonToken token, JToken value, bool verifyType = false)
        => SetBackingTokenMethod.Invoke(token, new object[] { value, verifyType });

    /// <summary>
    /// Replacement for NavJsonToken.ALWriteTo(DataError, OutStream).
    /// Serializes the JSON token to a MockOutStream.
    /// AL: JsonObject.WriteTo(OutStream)  →  MockJsonHelper.WriteTo(token, error, stream)
    /// </summary>
    public static bool WriteTo(NavJsonToken token, DataError errorLevel, MockOutStream stream)
    {
        try
        {
            var backingToken = GetBackingToken(token);
            using var stringWriter = new StringWriter(CultureInfo.InvariantCulture);
            using var jsonWriter = new JsonTextWriter(stringWriter)
            {
                Formatting = Formatting.None
            };
            backingToken.WriteTo(jsonWriter);
            stream.WriteText(stringWriter.ToString());
            return true;
        }
        catch (Exception)
        {
            if (errorLevel == DataError.ThrowError)
                throw;
            return false;
        }
    }

    /// <summary>
    /// Replacement for NavJsonToken.ALWriteTo(DataError, ByRef&lt;NavText&gt;).
    /// Serializes the JSON token to a Text variable without going through
    /// TrappableOperationExecutor or NavCurrentThread.Session.
    /// </summary>
    public static bool WriteTo(NavJsonToken token, DataError errorLevel, ByRef<NavText> data)
    {
        try
        {
            var backingToken = GetBackingToken(token);
            using var stringWriter = new StringWriter(CultureInfo.InvariantCulture);
            using var jsonWriter = new JsonTextWriter(stringWriter)
            {
                Formatting = Formatting.None
            };
            backingToken.WriteTo(jsonWriter);
            data.Value = new NavText(data.Value.MaxLength, stringWriter.ToString());
            return true;
        }
        catch (Exception)
        {
            if (errorLevel == DataError.ThrowError)
                throw;
            return false;
        }
    }

    /// <summary>
    /// Replacement for NavJsonToken.ALReadFrom(DataError, string).
    /// Parses a JSON string into the token without going through
    /// TrappableOperationExecutor or NavCurrentThread.Session.
    /// </summary>
    public static bool ReadFrom(NavJsonToken token, DataError errorLevel, string data)
    {
        try
        {
            using var reader = new StringReader(data);
            using var jsonReader = new JsonTextReader(reader)
            {
                DateParseHandling = DateParseHandling.None
            };
            var jToken = JToken.ReadFrom(jsonReader);
            SetBackingToken(token, jToken, false);
            return true;
        }
        catch (Exception)
        {
            if (errorLevel == DataError.ThrowError)
                throw;
            return false;
        }
    }

    /// <summary>
    /// Replacement for NavJsonToken.ALReadFrom(DataError, InStream).
    /// Reads the JSON string from a MockInStream and parses it.
    /// AL: JsonObject.ReadFrom(InStream)  →  MockJsonHelper.ReadFrom(token, error, stream)
    /// </summary>
    public static bool ReadFrom(NavJsonToken token, DataError errorLevel, MockInStream stream)
    {
        var text = stream.ReadAll();
        return ReadFrom(token, errorLevel, text);
    }

    /// <summary>
    /// Replacement for NavJsonToken.ALSelectToken(DataError, string, ByRef&lt;NavJsonToken&gt;).
    /// Selects a single token by JSONPath without going through TrappableOperationExecutor.
    /// </summary>
    public static bool SelectToken(NavJsonToken token, DataError errorLevel, string path, ByRef<NavJsonToken> result)
    {
        try
        {
            var backingToken = GetBackingToken(token);
            var selected = backingToken.SelectTokens(path, errorWhenNoMatch: false)
                .Where(IsSupportedTokenType)
                .ToList();

            if (selected.Count == 0)
            {
                if (errorLevel == DataError.ThrowError)
                    throw new Exception($"A JSON path query did not produce any results: '{path}'");
                return false;
            }

            result.Value = NavJsonToken.Create(selected[0]);
            return true;
        }
        catch (Exception)
        {
            if (errorLevel == DataError.ThrowError)
                throw;
            return false;
        }
    }

    /// <summary>
    /// Replacement for NavJsonToken.ALSelectTokens(DataError, string, ByRef&lt;NavList&lt;NavJsonToken&gt;&gt;).
    /// Passes through to the real implementation — SelectTokens is less commonly used
    /// and may require NavList construction that varies across BC versions.
    /// Falls back gracefully if the real method crashes.
    /// </summary>
    public static bool SelectTokens(NavJsonToken token, DataError errorLevel, string path, object result)
    {
        try
        {
            // Attempt the real call via reflection
            var method = token.GetType().GetMethod("ALSelectTokens",
                BindingFlags.Instance | BindingFlags.Public,
                null, new[] { typeof(DataError), typeof(string), result.GetType() }, null);
            if (method != null)
                return (bool)method.Invoke(token, new object[] { errorLevel, path, result })!;
            return false;
        }
        catch (Exception)
        {
            if (errorLevel == DataError.ThrowError)
                throw;
            return false;
        }
    }

    /// <summary>
    /// Replacement for NavJsonToken.ALGetBoolean(key).
    /// Returns the boolean value of the named property without going through
    /// TrappableOperationExecutor or NavCurrentThread.Session.
    /// AL: JsonObject.GetBoolean('key')  →  MockJsonHelper.GetBoolean(token, key)
    /// Note: BC does not pass a DataError arg for typed JSON getters.
    /// </summary>
    public static bool GetBoolean(NavJsonToken token, string key)
    {
        var backingToken = GetBackingToken(token);
        if (backingToken is not JObject obj)
            throw new Exception("The JSON token is not an object.");

        if (!obj.TryGetValue(key, out var val))
            throw new Exception($"The JSON object does not contain a property with the name '{key}'.");

        if (val.Type != JTokenType.Boolean)
            throw new Exception(
                $"The value of JSON property '{key}' cannot be converted to a Boolean value.");

        return val.Value<bool>();
    }

    private static bool IsSupportedTokenType(JToken token)
    {
        return token.Type switch
        {
            JTokenType.Object or JTokenType.Array or JTokenType.Integer or
            JTokenType.Float or JTokenType.String or JTokenType.Boolean or
            JTokenType.Null or JTokenType.Undefined or JTokenType.Date or
            JTokenType.Bytes or JTokenType.Guid or JTokenType.Uri or
            JTokenType.TimeSpan => true,
            _ => false,
        };
    }
}

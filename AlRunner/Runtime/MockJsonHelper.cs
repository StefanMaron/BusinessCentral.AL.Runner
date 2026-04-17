namespace AlRunner.Runtime;

using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
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

    private static T CreateJsonToken<T>(JToken backing) where T : NavJsonToken
    {
        // NavJsonToken subclasses have no parameterless constructor.
        // GetUninitializedObject creates a zero-initialized instance bypassing constructors;
        // SetBackingToken then injects the Newtonsoft.Json token directly.
        var instance = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
        SetBackingToken(instance, backing);
        return instance;
    }

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
    /// Replacement for NavJsonObject.ALWriteWithSecretsTo(DataError, NavDictionary, ByRef&lt;NavSecretText&gt;).
    /// In the runner, secrets are treated as plain text — serializes the JSON to a string
    /// and wraps it in a NavSecretText. The secrets dictionary is intentionally ignored.
    /// AL: JsonObject.WriteWithSecretsTo(Secrets, var Result) → MockJsonHelper.WriteWithSecretsTo(token, error, secrets, result)
    /// </summary>
    public static bool WriteWithSecretsTo(NavJsonToken token, DataError errorLevel, object secrets, ByRef<NavSecretText> result)
    {
        try
        {
            var backingToken = GetBackingToken(token);
            using var stringWriter = new StringWriter(CultureInfo.InvariantCulture);
            using var jsonWriter = new JsonTextWriter(stringWriter) { Formatting = Formatting.None };
            backingToken.WriteTo(jsonWriter);
            result.Value = NavSecretText.Create(stringWriter.ToString());
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
    /// Fallback WriteTo for non-JSON types (e.g. NavXmlCData, NavXmlElement).
    /// The rewriter intercepts all ALWriteTo calls regardless of receiver type, so XML nodes
    /// also land here. We call ALWriteTo natively via reflection — XML Write methods do not
    /// go through TrappableOperationExecutor and work standalone.
    /// </summary>
    public static bool WriteTo(object xmlNode, DataError errorLevel, ByRef<NavText> data)
    {
        try
        {
            // Find ALWriteTo(DataError, ByRef<NavText>) on the concrete XML node type.
            var writeMethod = xmlNode.GetType()
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "ALWriteTo"
                    && m.GetParameters() is { Length: 2 } ps
                    && ps[0].ParameterType == typeof(DataError)
                    && ps[1].ParameterType.IsGenericType
                    && ps[1].ParameterType.GetGenericTypeDefinition() == typeof(ByRef<>)
                    && ps[1].ParameterType.GetGenericArguments()[0] == typeof(NavText));
            if (writeMethod is not null)
            {
                writeMethod.Invoke(xmlNode, new object[] { errorLevel, data });
                return true;
            }

            // Fallback: build CDATA wrapper from ALValue for NavXmlCData nodes.
            var valueProp = xmlNode.GetType().GetProperty("ALValue",
                BindingFlags.Public | BindingFlags.Instance);
            if (valueProp?.GetValue(xmlNode) is NavText navText)
            {
                data.Value = new NavText(data.Value.MaxLength, $"<![CDATA[{(string)navText}]]>");
                return true;
            }

            throw new InvalidOperationException(
                $"MockJsonHelper.WriteTo: no write support for {xmlNode?.GetType().FullName}");
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            if (errorLevel == DataError.ThrowError)
                throw ex.InnerException;
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
    /// Fallback WriteTo(OutStream) for non-JSON types.
    /// See <see cref="WriteTo(object, DataError, ByRef{NavText})"/> for context.
    /// </summary>
    public static bool WriteTo(object xmlNode, DataError errorLevel, MockOutStream stream)
    {
        var raw = NavText.Default(0);
        var textRef = new ByRef<NavText>(() => raw, v => raw = v);
        bool ok = WriteTo(xmlNode, errorLevel, textRef);
        if (ok)
            stream.WriteText((string)raw);
        return ok;
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

    /// <summary>
    /// Replacement for NavJsonToken.ALPath(DataError).
    /// Returns the BC-style JSON path for the token (e.g. "$" for root, "$.foo" for a field).
    /// Newtonsoft.Json uses "" for root and "foo" for a field — this converts to BC format.
    /// AL: JsonToken.Path() → MockJsonHelper.Path(token, error)
    /// </summary>
    public static NavText Path(NavJsonToken token)
    {
        var backingToken = GetBackingToken(token);
        var newtonsoftPath = backingToken.Path;
        // Convert Newtonsoft path to BC $ format:
        //   ""    → "$"
        //   "[0]" → "$[0]"
        //   "foo" → "$.foo"
        string bcPath;
        if (string.IsNullOrEmpty(newtonsoftPath))
            bcPath = "$";
        else if (newtonsoftPath.StartsWith('['))
            bcPath = "$" + newtonsoftPath;
        else
            bcPath = "$." + newtonsoftPath;
        return new NavText(bcPath);
    }

    /// <summary>
    /// Overload for Cookie.Path — BC emits cookie.ALPath as a property access,
    /// which the rewriter converts to MockJsonHelper.Path(cookie). Cookies do not
    /// need path-format conversion; just wrap the raw string in NavText.
    /// </summary>
    public static NavText Path(MockCookie cookie)
        => new NavText(cookie.ALPath);

    /// <summary>
    /// Overload for TestHttpRequestMessage.Path — BC emits req.ALPath as a property access,
    /// which the rewriter converts to MockJsonHelper.Path(req). Returns the HTTP request path.
    /// </summary>
    public static NavText Path(MockTestHttpRequestMessage req)
        => req.ALPath;

    /// <summary>Returns true if the token's backing store is a JArray.</summary>
    public static bool IsArray(NavJsonToken token)
        => GetBackingToken(token) is JArray;

    /// <summary>Returns true if the token's backing store is a JObject.</summary>
    public static bool IsObject(NavJsonToken token)
        => GetBackingToken(token) is JObject;

    /// <summary>Returns true if the token's backing store is a JValue (primitive or null).</summary>
    public static bool IsValue(NavJsonToken token)
        => GetBackingToken(token) is JValue;

    /// <summary>
    /// Replacement for NavJsonToken.ALClone().
    /// Deep-clones the token and returns a new NavJsonToken wrapping the clone.
    /// </summary>
    public static NavJsonToken Clone(NavJsonToken token)
    {
        var backing = GetBackingToken(token);
        var cloned = backing.DeepClone();
        return NavJsonToken.Create(cloned);
    }

    /// <summary>
    /// Overload for <c>HttpHeaders.Keys()</c>.
    /// BC routes ALL <c>.Keys()</c> calls through MockJsonHelper; this overload
    /// handles the case where the receiver is a <c>MockHttpHeaders</c>.
    /// Returns a list of all distinct header names.
    /// </summary>
    public static NavList<NavText> Keys(MockHttpHeaders headers)
    {
        var list = NavList<NavText>.Default;
        foreach (var key in headers.HeaderNames)
            list.ALAdd(new NavText(key));
        return list;
    }

    /// <summary>
    /// Replacement for NavJsonObject.ALKeys(DataError).
    /// Returns a list of all property names in the object.
    /// AL: JsonObject.Keys()  →  MockJsonHelper.Keys(token, error)
    /// </summary>
    public static NavList<NavText> Keys(NavJsonToken token, DataError errorLevel)
    {
        var backingToken = GetBackingToken(token);
        if (backingToken is not JObject obj)
        {
            if (errorLevel == DataError.ThrowError)
                throw new Exception("The JSON token is not an object.");
            return NavList<NavText>.Default;
        }
        var list = NavList<NavText>.Default;
        foreach (var prop in obj.Properties())
            list.ALAdd(new NavText(prop.Name));
        return list;
    }

    /// <summary>
    /// Replacement for NavJsonObject.ALGetText(key).
    /// Returns the string value of the named property.
    /// AL: JsonObject.GetText('key')  →  MockJsonHelper.GetText(token, key)
    /// </summary>
    public static NavText GetText(NavJsonToken token, string key)
    {
        var backingToken = GetBackingToken(token);
        if (backingToken is not JObject obj)
            throw new Exception("The JSON token is not an object.");
        if (!obj.TryGetValue(key, out var val))
            throw new Exception($"The JSON object does not contain a property with the name '{key}'.");
        return new NavText(val.Value<string>() ?? string.Empty);
    }

    /// <summary>
    /// Replacement for NavJsonObject.ALGetInteger(key).
    /// Returns the integer value of the named property.
    /// AL: JsonObject.GetInteger('key')  →  MockJsonHelper.GetInteger(token, key)
    /// </summary>
    public static int GetInteger(NavJsonToken token, string key)
    {
        var backingToken = GetBackingToken(token);
        if (backingToken is not JObject obj)
            throw new Exception("The JSON token is not an object.");
        if (!obj.TryGetValue(key, out var val))
            throw new Exception($"The JSON object does not contain a property with the name '{key}'.");
        return val.Value<int>();
    }

    /// <summary>
    /// Replacement for NavJsonObject.ALGetDecimal(key).
    /// Returns the decimal value of the named property.
    /// AL: JsonObject.GetDecimal('key')  →  MockJsonHelper.GetDecimal(token, key)
    /// </summary>
    public static NavDecimal GetDecimal(NavJsonToken token, string key)
    {
        var backingToken = GetBackingToken(token);
        if (backingToken is not JObject obj)
            throw new Exception("The JSON token is not an object.");
        if (!obj.TryGetValue(key, out var val))
            throw new Exception($"The JSON object does not contain a property with the name '{key}'.");
        return NavDecimal.Create(new Decimal18(val.Value<decimal>()));
    }

    /// <summary>
    /// Replacement for NavJsonObject.ALGetObject(key).
    /// Returns the nested JsonObject value of the named property.
    /// AL: JsonObject.GetObject('key')  →  MockJsonHelper.GetObject(token, key)
    /// </summary>
    public static NavJsonObject GetObject(NavJsonToken token, string key)
    {
        var backingToken = GetBackingToken(token);
        if (backingToken is not JObject obj)
            throw new Exception("The JSON token is not an object.");
        if (!obj.TryGetValue(key, out var val))
            throw new Exception($"The JSON object does not contain a property with the name '{key}'.");
        if (val is not JObject jObj)
            throw new Exception($"The value of JSON property '{key}' is not an object.");
        return CreateJsonToken<NavJsonObject>(jObj);
    }

    /// <summary>
    /// Replacement for NavJsonObject.ALGetArray(key).
    /// Returns the nested JsonArray value of the named property.
    /// AL: JsonObject.GetArray('key')  →  MockJsonHelper.GetArray(token, key)
    /// </summary>
    public static NavJsonArray GetArray(NavJsonToken token, string key)
    {
        var backingToken = GetBackingToken(token);
        if (backingToken is not JObject obj)
            throw new Exception("The JSON token is not an object.");
        if (!obj.TryGetValue(key, out var val))
            throw new Exception($"The JSON object does not contain a property with the name '{key}'.");
        if (val is not JArray jArr)
            throw new Exception($"The value of JSON property '{key}' is not an array.");
        return CreateJsonToken<NavJsonArray>(jArr);
    }

    /// <summary>
    /// Stub replacement for NavJsonToken.ALWriteToYaml(DataError, ByRef&lt;NavText&gt;).
    /// BC's WriteToYaml requires YamlDotNet which is not available in the runner.
    /// Serializes as JSON instead — JSON is valid YAML, so this stub is sufficient for testing.
    /// AL: JsonObject.WriteToYaml(var Text)  →  MockJsonHelper.WriteToYaml(token, error, data)
    /// </summary>
    public static bool WriteToYaml(NavJsonToken token, DataError errorLevel, ByRef<NavText> data)
        => WriteTo(token, errorLevel, data);

    /// <summary>
    /// Stub replacement for NavJsonToken.ALWriteToYaml(DataError, OutStream).
    /// See <see cref="WriteToYaml(NavJsonToken, DataError, ByRef{NavText})"/> for context.
    /// </summary>
    public static bool WriteToYaml(NavJsonToken token, DataError errorLevel, MockOutStream stream)
        => WriteTo(token, errorLevel, stream);

    /// <summary>
    /// Stub replacement for NavJsonToken.ALReadFromYaml(DataError, string).
    /// BC's ReadFromYaml requires YamlDotNet which is not available in the runner.
    /// Parses as JSON instead — simple YAML using JSON notation (the common test case)
    /// is parsed correctly.
    /// AL: JsonObject.ReadFromYaml(Text)  →  MockJsonHelper.ReadFromYaml(token, error, text)
    /// </summary>
    public static bool ReadFromYaml(NavJsonToken token, DataError errorLevel, string data)
        => ReadFrom(token, errorLevel, data);

    /// <summary>
    /// Stub replacement for NavJsonToken.ALReadFromYaml(DataError, InStream).
    /// See <see cref="ReadFromYaml(NavJsonToken, DataError, string)"/> for context.
    /// </summary>
    public static bool ReadFromYaml(NavJsonToken token, DataError errorLevel, MockInStream stream)
        => ReadFrom(token, errorLevel, stream);

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

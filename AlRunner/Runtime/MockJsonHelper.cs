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

    // --- NavDate / NavTime construction helpers (avoid Telemetry.Abstractions in BC 28+) ---
    // Search the type itself first, then BaseType, mirroring how CreateNavDateTime works in AlScope.
    private static readonly FieldInfo? NavDateValueField =
        typeof(NavDate).GetField("value", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? typeof(NavDate).BaseType?.GetField("value", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly FieldInfo? NavTimeValueField =
        typeof(NavTime).GetField("value", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? typeof(NavTime).BaseType?.GetField("value", BindingFlags.Instance | BindingFlags.NonPublic);

    private static NavDate CreateNavDate(DateTime date)
    {
        // Strategy 1: Activator + reflection (avoids Telemetry.Abstractions crash in BC 28+)
        if (NavDateValueField != null)
        {
            try
            {
                var result = (NavDate)System.Activator.CreateInstance(typeof(NavDate), nonPublic: true)!;
                NavDateValueField.SetValue(result, date.Date);
                return result;
            }
            catch { }
        }
        // Strategy 2: Implicit/explicit operator via reflection (may trigger Telemetry.Abstractions
        //             in BC 28+ but worth trying for older BC versions)
        try
        {
            var op = typeof(NavDate).GetMethod("op_Implicit",
                         BindingFlags.Static | BindingFlags.Public,
                         null, new[] { typeof(DateTime) }, null)
                     ?? typeof(NavDate).GetMethod("op_Explicit",
                         BindingFlags.Static | BindingFlags.Public,
                         null, new[] { typeof(DateTime) }, null);
            if (op != null) return (NavDate)op.Invoke(null, new object[] { date.Date })!;
        }
        catch { }
        return NavDate.Default;
    }

    private static NavTime CreateNavTime(DateTime timeAsDateTime)
    {
        // Strategy 1: Activator + reflection
        if (NavTimeValueField != null)
        {
            try
            {
                var result = (NavTime)System.Activator.CreateInstance(typeof(NavTime), nonPublic: true)!;
                NavTimeValueField.SetValue(result, timeAsDateTime);
                return result;
            }
            catch { }
        }
        // Strategy 2: Implicit/explicit operator via reflection
        try
        {
            var op = typeof(NavTime).GetMethod("op_Implicit",
                         BindingFlags.Static | BindingFlags.Public,
                         null, new[] { typeof(DateTime) }, null)
                     ?? typeof(NavTime).GetMethod("op_Explicit",
                         BindingFlags.Static | BindingFlags.Public,
                         null, new[] { typeof(DateTime) }, null);
            if (op != null) return (NavTime)op.Invoke(null, new object[] { timeAsDateTime })!;
        }
        catch { }
        return NavTime.Default;
    }

    // --- JsonValue typed-getter / utility methods (issue #699) ---

    /// <summary>
    /// Replacement for NavJsonValue.ALAsBigInteger().
    /// Returns the backing integer value as NavBigInteger.
    /// AL: JsonValue.AsBigInteger()  →  MockJsonHelper.AsBigInteger(token)
    /// </summary>
    public static NavBigInteger AsBigInteger(NavJsonToken token, DataError errorLevel = default)
    {
        var backing = GetBackingToken(token);
        return NavBigInteger.Create(backing.Value<long>());
    }

    /// <summary>
    /// Replacement for NavJsonValue.ALAsByte().
    /// Returns the backing integer value as a byte.
    /// AL: JsonValue.AsByte()  →  MockJsonHelper.AsByte(token)
    /// </summary>
    public static byte AsByte(NavJsonToken token, DataError errorLevel = default)
    {
        var backing = GetBackingToken(token);
        return backing.Value<byte>();
    }

    /// <summary>
    /// Replacement for NavJsonValue.ALAsChar().
    /// Returns the backing integer value as a char (code point).
    /// AL: JsonValue.AsChar()  →  MockJsonHelper.AsChar(token)
    /// </summary>
    public static char AsChar(NavJsonToken token, DataError errorLevel = default)
    {
        var backing = GetBackingToken(token);
        return (char)backing.Value<int>();
    }

    /// <summary>
    /// Replacement for NavJsonValue.ALAsCode().
    /// Returns the backing string value as NavCode[250].
    /// AL: JsonValue.AsCode()  →  MockJsonHelper.AsCode(token)
    /// </summary>
    public static NavCode AsCode(NavJsonToken token, DataError errorLevel = default)
    {
        var backing = GetBackingToken(token);
        var s = backing.Value<string>() ?? "";
        return new NavCode(250, s);
    }

    /// <summary>
    /// Replacement for NavJsonValue.ALAsDate() or MockTestPageField.ALAsDate().
    /// ALAsDate exists on BOTH NavJsonValue and MockTestPageField; accepts object and dispatches.
    /// AL: JsonValue.AsDate()  →  MockJsonHelper.AsDate(token, ...)
    /// AL: TestField.AsDate()  →  MockJsonHelper.AsDate(testField, ...)
    /// </summary>
    public static NavDate AsDate(object tokenOrField, DataError errorLevel = default)
    {
        if (tokenOrField is MockTestPageField f) return f.ALAsDate();
        if (tokenOrField is NavJsonToken token)
        {
            var backing = GetBackingToken(token);
            if (backing.Type == JTokenType.Date)
                return CreateNavDate(backing.Value<DateTime>());
            if (backing.Type == JTokenType.String &&
                DateTime.TryParse(backing.Value<string>(), out var parsed))
                return CreateNavDate(parsed);
        }
        return NavDate.Default;
    }

    /// <summary>
    /// Replacement for NavJsonValue.ALAsDateTime() or MockTestPageField.ALAsDateTime().
    /// ALAsDateTime exists on BOTH NavJsonValue and MockTestPageField; accepts object and dispatches.
    /// AL: JsonValue.AsDateTime()  →  MockJsonHelper.AsDateTime(token, ...)
    /// AL: TestField.AsDateTime()  →  MockJsonHelper.AsDateTime(testField, ...)
    /// </summary>
    public static NavDateTime AsDateTime(object tokenOrField, DataError errorLevel = default)
    {
        if (tokenOrField is MockTestPageField f) return f.ALAsDateTime();
        if (tokenOrField is NavJsonToken token)
        {
            var backing = GetBackingToken(token);
            if (backing.Type == JTokenType.Date)
                return AlCompat.CreateNavDateTime(backing.Value<DateTime>());
            if (backing.Type == JTokenType.String &&
                DateTime.TryParse(backing.Value<string>(), out var parsed))
                return AlCompat.CreateNavDateTime(parsed);
        }
        return NavDateTime.Default;
    }

    /// <summary>
    /// Replacement for NavJsonValue.ALAsDuration().
    /// Returns the backing numeric value as a Duration (TimeSpan from milliseconds).
    /// AL: JsonValue.AsDuration()  →  MockJsonHelper.AsDuration(token)
    /// </summary>
    public static TimeSpan AsDuration(NavJsonToken token, DataError errorLevel = default)
    {
        var backing = GetBackingToken(token);
        if (backing.Type == JTokenType.TimeSpan)
            return backing.Value<TimeSpan>();
        return TimeSpan.FromMilliseconds(backing.Value<long>());
    }

    /// <summary>
    /// Replacement for NavJsonValue.ALAsOption().
    /// Returns the backing integer value as an option ordinal.
    /// AL: JsonValue.AsOption()  →  MockJsonHelper.AsOption(token)
    /// </summary>
    public static int AsOption(NavJsonToken token, DataError errorLevel = default)
    {
        var backing = GetBackingToken(token);
        return backing.Value<int>();
    }

    /// <summary>
    /// Replacement for NavJsonValue.ALAsTime() or MockTestPageField.ALAsTime().
    /// ALAsTime exists on BOTH NavJsonValue and MockTestPageField; accepts object and dispatches.
    /// AL: JsonValue.AsTime()  →  MockJsonHelper.AsTime(token, ...)
    /// AL: TestField.AsTime()  →  MockJsonHelper.AsTime(testField, ...)
    /// </summary>
    public static NavTime AsTime(object tokenOrField, DataError errorLevel = default)
    {
        if (tokenOrField is MockTestPageField f) return f.ALAsTime();
        if (tokenOrField is NavJsonToken token)
        {
            var backing = GetBackingToken(token);
            if (backing.Type == JTokenType.Date)
                return CreateNavTime(backing.Value<DateTime>());
            if (backing.Type == JTokenType.TimeSpan)
            {
                // TimeSpan stored directly — use as time-of-day
                var ts = backing.Value<TimeSpan>();
                return CreateNavTime(DateTime.MinValue + ts);
            }
            if (backing.Type == JTokenType.Integer)
            {
                // Stored as milliseconds since midnight
                var ms = backing.Value<long>();
                return CreateNavTime(DateTime.MinValue + TimeSpan.FromMilliseconds(ms));
            }
            if (backing.Type == JTokenType.String)
            {
                var s = backing.Value<string>() ?? "";
                if (DateTime.TryParse(s, out var dt))
                    return CreateNavTime(dt);
                if (TimeSpan.TryParse(s, out var tsv))
                    return CreateNavTime(DateTime.MinValue + tsv);
            }
        }
        return NavTime.Default;
    }

    /// <summary>
    /// Replacement for NavJsonValue.ALAsToken().
    /// Returns the JsonValue as a JsonToken — NavJsonValue IS a NavJsonToken.
    /// AL: JsonValue.AsToken()  →  MockJsonHelper.AsToken(token)
    /// </summary>
    public static NavJsonToken AsToken(NavJsonToken token, DataError errorLevel = default)
        => token;

    /// <summary>
    /// Replacement for NavJsonValue.ALIsUndefined().
    /// Returns true if the backing value has token type Undefined.
    /// AL: JsonValue.IsUndefined()  →  MockJsonHelper.IsUndefined(token)
    /// </summary>
    public static bool IsUndefined(NavJsonToken token, DataError errorLevel = default)
        => GetBackingToken(token).Type == JTokenType.Undefined;

    /// <summary>
    /// Replacement for NavJsonValue.ALPath (property / no-arg method).
    /// Returns the Newtonsoft.Json path of the backing token.
    /// AL: JsonValue.Path  →  MockJsonHelper.Path(token)
    /// </summary>
    public static string Path(NavJsonToken token, DataError errorLevel = default)
        => GetBackingToken(token).Path;

    /// <summary>
    /// Replacement for NavJsonValue.ALSetValueToUndefined().
    /// Sets the backing token to a Newtonsoft.Json Undefined value.
    /// AL: JsonValue.SetValueToUndefined()  →  MockJsonHelper.SetValueToUndefined(token)
    /// </summary>
    public static void SetValueToUndefined(NavJsonToken token, DataError errorLevel = default)
        => SetBackingToken(token, JValue.CreateUndefined());

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

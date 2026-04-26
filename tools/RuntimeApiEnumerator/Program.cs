// RuntimeApiEnumerator — enumerates every BC built-in AL type and method by
// reflecting on Microsoft.Dynamics.Nav.CodeAnalysis.dll.
//
// Every AL built-in type is exposed as a sealed <Name>ClassTypeSymbol with a
// static `Instance` property. Instance.GetMembers() returns BuiltInMethodTypeSymbol
// entries with Name + Kind. Methods appear once per overload, and we now extract
// per-overload parameter signatures from the Parameters property.
//
// Output: runtime-api.json
//   { "BigText": { "methods": [
//       { "name": "AddText", "signature": "(Text)" },
//       { "name": "AddText", "signature": "(BigText, Integer)" },
//       ...
//   ] }, ... }
//
// Usage:
//   DOTNET_ROLL_FORWARD=LatestMajor dotnet run --project tools/RuntimeApiEnumerator \
//       -- <compiler-dll-dir> <output-json>

using System.Reflection;
using System.Text.Json;

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: RuntimeApiEnumerator <compiler-dll-dir> <output-json>");
    return 1;
}

var dllDir = args[0];
var outputPath = args[1];
var dllPath = Path.Combine(dllDir, "Microsoft.Dynamics.Nav.CodeAnalysis.dll");
if (!File.Exists(dllPath))
{
    Console.Error.WriteLine($"Not found: {dllPath}");
    return 2;
}

AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
{
    var candidate = Path.Combine(dllDir, new AssemblyName(e.Name).Name + ".dll");
    return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
};

var asm = Assembly.LoadFrom(dllPath);
Type[] types;
try { types = asm.GetTypes(); }
catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray()!; }

// Top-level *ClassTypeSymbol types — skip interfaces, abstract bases, and the
// *InstanceClassTypeSymbol variants (those are thin wrappers; members live on
// the main class via inheritance).
var classTypeSymbols = types
    .Where(t => t is { IsClass: true, IsAbstract: false, DeclaringType: null }
                && t.Name.EndsWith("ClassTypeSymbol")
                && t.Name != "ClassTypeSymbol")
    .OrderBy(t => t.Name)
    .ToList();

Console.Error.WriteLine($"[enumerate] candidate types: {classTypeSymbols.Count}");

var output = new SortedDictionary<string, TypeEntry>(StringComparer.Ordinal);
var skipped = new List<string>();

foreach (var t in classTypeSymbols)
{
    var alTypeName = StripSuffix(t.Name, "ClassTypeSymbol");

    object? instance;
    try
    {
        var instanceProp = FindInstanceProp(t);
        if (instanceProp == null) { skipped.Add($"{alTypeName}: no Instance property"); continue; }
        instance = instanceProp.GetValue(null);
        if (instance == null) { skipped.Add($"{alTypeName}: Instance returned null"); continue; }
    }
    catch (Exception ex) { skipped.Add($"{alTypeName}: {ex.GetType().Name} on Instance"); continue; }

    var getMembers = FindGetMembers(instance.GetType());
    if (getMembers == null) { skipped.Add($"{alTypeName}: no GetMembers()"); continue; }

    System.Collections.IEnumerable? members;
    try { members = getMembers.Invoke(instance, null) as System.Collections.IEnumerable; }
    catch (Exception ex) { skipped.Add($"{alTypeName}: {ex.GetType().Name} on GetMembers"); continue; }
    if (members == null) continue;

    // Collect per-overload entries (one entry per method invocation in the symbol table).
    var methodEntries = new List<MethodEntry>();
    foreach (var m in members)
    {
        if (m == null) continue;
        var kind = m.GetType().GetProperty("Kind")?.GetValue(m)?.ToString();
        // Only include methods. Properties/fields/constants are out of scope for
        // the runtime-API coverage layer (the ask is method surface).
        if (kind != "Method") continue;
        var name = m.GetType().GetProperty("Name")?.GetValue(m) as string;
        if (string.IsNullOrEmpty(name)) continue;

        var signature = ExtractSignature(m);
        methodEntries.Add(new MethodEntry { Name = name, Signature = signature });
    }

    if (methodEntries.Count == 0) { skipped.Add($"{alTypeName}: no method members"); continue; }

    // Deduplicate (same name+signature can appear in both instance and static variants).
    var seen = new HashSet<string>();
    var deduped = new List<MethodEntry>();
    foreach (var e in methodEntries)
    {
        var key = $"{e.Name}|{e.Signature}";
        if (seen.Add(key)) deduped.Add(e);
    }

    output[alTypeName] = new TypeEntry
    {
        Methods = deduped
            .OrderBy(e => e.Name, StringComparer.Ordinal)
            .ThenBy(e => e.Signature, StringComparer.Ordinal)
            .ToList(),
    };
}

Console.Error.WriteLine($"[enumerate] types with methods: {output.Count}");
Console.Error.WriteLine($"[enumerate] total overloads: {output.Sum(kv => kv.Value.Methods.Count)}");
if (skipped.Count > 0)
{
    Console.Error.WriteLine($"[enumerate] skipped {skipped.Count}:");
    foreach (var s in skipped.Take(20)) Console.Error.WriteLine($"  - {s}");
}

var header = new
{
    generated = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
    compiler_dll = Path.GetFileName(dllPath),
    types = output,
};

Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
File.WriteAllText(outputPath,
    JsonSerializer.Serialize(header, new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    }));
Console.Error.WriteLine($"[enumerate] wrote {outputPath}");

return 0;

/// <summary>
/// Extracts the parameter-type signature string from a method symbol.
/// Uses the type symbol's Name property which contains the AL type name
/// (e.g. "Text", "Integer", "RecordRef").  Falls back to NavTypeKind when
/// Name is blank, and to "?" for unknown parameters.
/// </summary>
static string ExtractSignature(object methodSymbol)
{
    try
    {
        var paramsProp = methodSymbol.GetType().GetProperty("Parameters");
        if (paramsProp == null) return "()";
        var paramsList = paramsProp.GetValue(methodSymbol) as System.Collections.IEnumerable;
        if (paramsList == null) return "()";

        var typeNames = new List<string>();
        foreach (var param in paramsList)
        {
            if (param == null) { typeNames.Add("?"); continue; }
            var paramType = param.GetType().GetProperty("Type")?.GetValue(param);
            if (paramType == null) { typeNames.Add("?"); continue; }
            var typeName = paramType.GetType().GetProperty("Name")?.GetValue(paramType) as string;
            if (string.IsNullOrEmpty(typeName))
            {
                // Fall back to NavTypeKind enum name
                typeName = paramType.GetType().GetProperty("NavTypeKind")?.GetValue(paramType)?.ToString() ?? "?";
            }
            typeNames.Add(typeName);
        }
        return $"({string.Join(", ", typeNames)})";
    }
    catch
    {
        return "()";
    }
}

static string StripSuffix(string s, string suffix) =>
    s.EndsWith(suffix) ? s.Substring(0, s.Length - suffix.Length) : s;

static PropertyInfo? FindInstanceProp(Type t)
{
    var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    for (var cur = t; cur != null; cur = cur.BaseType)
    {
        var p = cur.GetProperty("Instance", flags | BindingFlags.DeclaredOnly);
        if (p != null) return p;
    }
    return null;
}

static MethodInfo? FindGetMembers(Type t)
{
    var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    for (var cur = t; cur != null; cur = cur.BaseType)
    {
        var m = cur.GetMethod("GetMembers", flags | BindingFlags.DeclaredOnly,
                               null, Type.EmptyTypes, null);
        if (m != null) return m;
    }
    return null;
}

internal sealed class TypeEntry
{
    public List<MethodEntry> Methods { get; set; } = new();
}

internal sealed class MethodEntry
{
    public string Name { get; set; } = "";
    public string Signature { get; set; } = "()";
}

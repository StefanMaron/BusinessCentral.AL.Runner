// RuntimeApiEnumerator — enumerates every BC built-in AL type and method by
// reflecting on Microsoft.Dynamics.Nav.CodeAnalysis.dll.
//
// Every AL built-in type is exposed as a sealed <Name>ClassTypeSymbol with a
// static `Instance` property. Instance.GetMembers() returns BuiltInMethodTypeSymbol
// entries with Name + Kind. Methods appear once per overload, so we group.
//
// Output: runtime-api.json
//   { "BigText": { "methods": [ { "name": "AddText", "overloads": 2 }, ... ] },
//     "RecordRef": { "methods": [ ... ] },
//     ... }
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

    var methodOverloads = new Dictionary<string, int>(StringComparer.Ordinal);
    foreach (var m in members)
    {
        if (m == null) continue;
        var kind = m.GetType().GetProperty("Kind")?.GetValue(m)?.ToString();
        // Only include methods. Properties/fields/constants are out of scope for
        // the runtime-API coverage layer (the ask is method surface).
        if (kind != "Method") continue;
        var name = m.GetType().GetProperty("Name")?.GetValue(m) as string;
        if (string.IsNullOrEmpty(name)) continue;
        methodOverloads[name] = methodOverloads.TryGetValue(name, out var c) ? c + 1 : 1;
    }

    if (methodOverloads.Count == 0) { skipped.Add($"{alTypeName}: no method members"); continue; }

    output[alTypeName] = new TypeEntry
    {
        Methods = methodOverloads
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new MethodEntry { Name = kv.Key, Overloads = kv.Value })
            .ToList(),
    };
}

Console.Error.WriteLine($"[enumerate] types with methods: {output.Count}");
Console.Error.WriteLine($"[enumerate] total methods: {output.Sum(kv => kv.Value.Methods.Count)}");
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
    public int Overloads { get; set; }
}

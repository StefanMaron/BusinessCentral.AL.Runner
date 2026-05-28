// EnumMetadataPatches — populate NCLOptionMetadata replacements for AL enums.
//
// Rationale:
//   The compiled AL emits `NCLEnumMetadata.Create(<enumId>).GetOrdinals()` /
//   `.GetNames()` for AL `Enum::"X".Ordinals()` / `.Names()` calls. The real
//   `NCLEnumMetadata.Create(int)` chains through NavGlobal.MetadataProvider
//   → SystemTenant which is null on the skeleton runtime, so MiscPatches
//   already hooks it to return `NCLOptionMetadata.Default`. However, that base
//   instance has virtual `GetNames()` / `GetOrdinals()` methods that throw
//   `NavNCLNotSupportedOperationException` — only the `NCLEnumMetadata`
//   subclass populates them.
//
//   Per HANDOFF §2.4 (reuse service-tier code before patching) we'd ideally
//   construct a real `NCLEnumMetadata`, but its protected ctor wires up
//   ServerUserSettings-backed LRU caches and per-value `NavOption.CreateBypassCache`
//   calls that aren't necessary just for `GetNames()`/`GetOrdinals()`. Instead
//   we ship a minimal `NCLOptionMetadata` subclass (`AlEnumOptionMetadata`)
//   that overrides exactly those two virtuals (and `OrdinalValues`/`Name`/`Id`
//   for completeness), constructed from the `(name, id, options[], indexes[])`
//   tuple captured by `BcCompiler.CaptureOutputter` at AL emit time.
//
// Decompile:
//   NCLOptionMetadata: Microsoft.Dynamics.Nav.Ncl.decompiled.cs:158163
//     - Base GetNames/GetOrdinals at 158334/158339 throw NotSupported.
//   NCLEnumMetadata override: 158980 / 158985 returns namesList / ordinalsList.
//
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using NCLOptionMetadata = Microsoft.Dynamics.Nav.Runtime.NCLOptionMetadata;
using NavList = Microsoft.Dynamics.Nav.Runtime.NavList<Microsoft.Dynamics.Nav.Runtime.NavText>;
using NavListInt = Microsoft.Dynamics.Nav.Runtime.NavList<int>;
using NavText = Microsoft.Dynamics.Nav.Runtime.NavText;

namespace AlRunnerV2;

/// <summary>
/// Captures (id, name, options[], indexes[]) for every AL enum compiled by
/// <see cref="BcCompiler"/>. Populated at emit time by <c>CaptureOutputter</c>;
/// consumed at runtime by <see cref="BcRuntime.NCLEnumMetadata_CreateById"/>.
/// </summary>
public static class AlEnumMetadataRegistry
{
    public sealed record Entry(int Id, string Name, string[] Options, int[] Indexes);

    private static readonly ConcurrentDictionary<int, Entry> _byId = new();

    /// <summary>Last-writer-wins; bundle-wide enum-id collisions are quarantined upstream.</summary>
    public static void Register(int id, string name, string[] options, int[] indexes)
    {
        if (options == null || indexes == null) return;
        if (options.Length != indexes.Length) return;
        _byId[id] = new Entry(id, name ?? string.Empty, options, indexes);
    }

    public static bool TryGet(int id, out Entry entry) => _byId.TryGetValue(id, out entry!);

    public static void Clear() => _byId.Clear();

    public static int Count => _byId.Count;

    public static void RegisterFromAppPath(string appPath)
    {
        if (string.IsNullOrEmpty(appPath) || !File.Exists(appPath)) return;
        try
        {
            foreach (var symbolJson in ReadSymbolReferences(appPath))
                RegisterFromSymbolReference(symbolJson);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[EnumMetadata] failed to read {Path.GetFileName(appPath)}: {ex.Message}");
        }
    }

    /// <summary>Snapshot of all currently registered entries. Used by the
    /// AL-output cache sidecar writer (Program.cs). Order is stable
    /// (sorted by Id) so the sidecar is byte-deterministic across runs.</summary>
    public static IReadOnlyList<Entry> Snapshot()
    {
        return _byId.Values.OrderBy(e => e.Id).ToList();
    }

    private static void RegisterFromSymbolReference(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        VisitSymbolContainer(root);
        if (root.TryGetProperty("Namespaces", out var namespaces) && namespaces.ValueKind == JsonValueKind.Array)
            foreach (var ns in namespaces.EnumerateArray())
                VisitSymbolContainer(ns);
    }

    private static void VisitSymbolContainer(JsonElement container)
    {
        if (container.TryGetProperty("EnumTypes", out var enumTypes) && enumTypes.ValueKind == JsonValueKind.Array)
        {
            foreach (var enumType in enumTypes.EnumerateArray())
                RegisterEnumType(enumType);
        }
        if (container.TryGetProperty("Namespaces", out var namespaces) && namespaces.ValueKind == JsonValueKind.Array)
        {
            foreach (var ns in namespaces.EnumerateArray())
                VisitSymbolContainer(ns);
        }
    }

    private static void RegisterEnumType(JsonElement enumType)
    {
        if (!enumType.TryGetProperty("Id", out var idProp) || !idProp.TryGetInt32(out var id))
            return;
        var name = enumType.TryGetProperty("Name", out var nameProp) ? nameProp.GetString() ?? string.Empty : string.Empty;
        if (!enumType.TryGetProperty("Values", out var values) || values.ValueKind != JsonValueKind.Array)
            return;

        var options = new List<string>();
        var indexes = new List<int>();
        var nextOrdinal = 0;
        foreach (var value in values.EnumerateArray())
        {
            var optionName = value.TryGetProperty("Name", out var optionNameProp)
                ? optionNameProp.GetString() ?? string.Empty
                : string.Empty;
            var ordinal = value.TryGetProperty("Ordinal", out var ordinalProp) && ordinalProp.TryGetInt32(out var explicitOrdinal)
                ? explicitOrdinal
                : nextOrdinal;
            options.Add(optionName);
            indexes.Add(ordinal);
            nextOrdinal = ordinal + 1;
        }
        Register(id, name, options.ToArray(), indexes.ToArray());
    }

    private static IEnumerable<string> ReadSymbolReferences(string appPath)
    {
        var bytes = File.ReadAllBytes(appPath);
        foreach (var json in ReadSymbolReferencesFromBytes(bytes))
            yield return json;
    }

    private static IEnumerable<string> ReadSymbolReferencesFromBytes(byte[] bytes)
    {
        using var zip = OpenZipFromNavx(bytes);
        var symbol = zip.Entries.FirstOrDefault(e =>
            e.FullName.Equals("SymbolReference.json", StringComparison.OrdinalIgnoreCase));
        if (symbol != null)
        {
            using var s = symbol.Open();
            using var reader = new StreamReader(s);
            yield return reader.ReadToEnd();
        }

        var nested = zip.Entries.FirstOrDefault(e =>
            e.FullName.EndsWith(".app", StringComparison.OrdinalIgnoreCase) && !e.FullName.Contains('/'));
        if (nested != null)
        {
            using var ns = nested.Open();
            using var ms = new MemoryStream();
            ns.CopyTo(ms);
            foreach (var json in ReadSymbolReferencesFromBytes(ms.ToArray()))
                yield return json;
        }
    }

    private static ZipArchive OpenZipFromNavx(byte[] bytes)
    {
        var offset = bytes.Length >= 8
            && bytes[0] == (byte)'N' && bytes[1] == (byte)'A'
            && bytes[2] == (byte)'V' && bytes[3] == (byte)'X'
                ? (int)BitConverter.ToUInt32(bytes, 4)
                : 0;
        var ms = new MemoryStream(bytes, offset, bytes.Length - offset, writable: false);
        return new ZipArchive(ms, ZipArchiveMode.Read);
    }
}

/// <summary>
/// Minimal <see cref="NCLOptionMetadata"/> subclass that satisfies
/// <c>GetNames()</c>/<c>GetOrdinals()</c> for AL enums by carrying the
/// captured names + ordinal indexes alongside the base <c>options</c> array.
/// </summary>
internal sealed class AlEnumOptionMetadata : NCLOptionMetadata
{
    private readonly NavList _names;
    private readonly NavListInt _ordinals;
    private readonly int[] _ordinalValues;
    private readonly string _name;
    private readonly int _id;

    public AlEnumOptionMetadata(string name, int id, string[] options, int[] indexes)
        : base(JoinOptions(options))
    {
        _name = name;
        _id = id;
        _ordinalValues = indexes;
        _optionNames = options;
        _names = (NavList)NavListCtorOfNavText.Invoke(
            new object[] { options.Select(o => NavText.Create(o ?? string.Empty)).ToList(), /*asReadOnly*/ true });
        _ordinals = (NavListInt)NavListCtorOfInt.Invoke(
            new object[] { indexes.ToList(), /*asReadOnly*/ true });
    }

    public override Microsoft.Dynamics.Nav.Runtime.NavList<NavText> GetNames() => _names;
    public override Microsoft.Dynamics.Nav.Runtime.NavList<int> GetOrdinals() => _ordinals;

    // IsEnum is consulted by NCLMetaField.InitValue/EmptyValue (line 150397/150423)
    // to decide whether to evaluate the initialValueText vs use the default-value
    // path. AL enum-typed fields must report IsEnum=true so InitValue evaluates
    // expressions like "Status::Closed" correctly.
    public override bool IsEnum => true;

    // AL emits `FieldRef.GetEnumValueCaptionFromOrdinalValue(ordinal)` →
    // `MetaField.FieldOptionMetadata.GetCaptionFromIndex(ordinal)` (Ncl line
    // 37205) and `GetEnumValueNameFromOrdinalValue(ordinal)` →
    // `GetOptionFromIndex(ordinal, emptyIfNotFound:true)` (Ncl line 37185).
    // The `ordinal` arg is a *true ordinal value* (e.g. 5, 10 for a sparse
    // AL enum), NOT a 0..Count-1 array index. The base NCLOptionMetadata
    // body treats the arg as an array index (line 158238), so for sparse
    // enums it returns either out-of-range stringification ("10") or the
    // wrong member ("5" → no member, falls through).
    //
    // The real BC subclass NCLEnumMetadata.GetOptionFromIndex (line 158792)
    // walks indexes[] looking for the matching ordinal value; we mirror that
    // here using our _ordinalValues. Likewise for GetCaptionFromIndex —
    // captions on AL-runner-emitted enums collapse to the member name (we
    // don't ingest CaptionML), so it forwards to GetOptionFromIndex.
    public override string GetOptionFromIndex(int index, bool emptyIfNotFound = false)
    {
        for (int i = 0; i < _ordinalValues.Length; i++)
        {
            if (_ordinalValues[i] == index)
            {
                // Mimic NCLOptionMetadata.GetOptionFromIndex by reflecting into
                // the base private `options` array (we passed it to the base
                // ctor as a comma-joined string). Cached via the constructor
                // captured _names so we just hand back the captured option text.
                return _optionNames[i];
            }
        }
        if (!emptyIfNotFound)
            return index.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return string.Empty;
    }

    public override string GetCaptionFromIndex(int index)
    {
        return GetOptionFromIndex(index);
    }

    public override bool IsValidOrdinal(int ordinal)
    {
        for (int i = 0; i < _ordinalValues.Length; i++)
            if (_ordinalValues[i] == ordinal) return true;
        return false;
    }

    public override int GetIndexFromOption(string option)
    {
        for (int i = 0; i < _optionNames.Length; i++)
        {
            if (string.Equals(_optionNames[i], option, StringComparison.Ordinal))
                return _ordinalValues[i];
        }
        if (int.TryParse(option, out var ord))
            return ord;
        return -1;
    }

    public override int GetIndexFromCaption(string caption) => GetIndexFromOption(caption);

    private readonly string[] _optionNames;

    // -- reflection cache for NavList<T> internal ctor --
    private static readonly ConstructorInfo NavListCtorOfNavText = ResolveNavListCtor<NavText>();
    private static readonly ConstructorInfo NavListCtorOfInt     = ResolveNavListCtor<int>();

    private static ConstructorInfo ResolveNavListCtor<T>()
    {
        var t = typeof(Microsoft.Dynamics.Nav.Runtime.NavList<T>);
        var ctor = t.GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null,
            new[] { typeof(System.Collections.Generic.List<T>), typeof(bool) },
            modifiers: null);
        if (ctor == null)
            throw new InvalidOperationException(
                $"NavList<{typeof(T).Name}>(List<{typeof(T).Name}>, bool) ctor not found");
        return ctor;
    }

    /// <summary>
    /// Build the comma-joined option string the base ctor expects. AL enum
    /// names are unique within an enum (BC compile-time enforced), so the
    /// duplicate check inside <c>NCLOptionMetadata(string)</c> won't fire.
    /// Empty / null members are normalized to empty string — matches BC's
    /// convention for the special " " (space-named) value.
    /// </summary>
    private static string JoinOptions(string[] options)
    {
        return string.Join(",", options.Select(o => o ?? string.Empty));
    }
}

public static partial class BcRuntime
{
    private static readonly ConcurrentDictionary<int, NCLOptionMetadata> _alEnumCache = new();

    /// <summary>
    /// Replacement for NCLEnumMetadata.Create(int).
    /// Look up the AL enum metadata captured at emit time; fall back to
    /// <c>NCLOptionMetadata.Default</c> for system / dependency enums whose
    /// metadata isn't in the registry (existing behavior — preserves ordinal
    /// arithmetic via NavOption.Value passthrough).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static NCLOptionMetadata NCLEnumMetadata_CreateByIdAlAware(int id)
    {
        if (_alEnumCache.TryGetValue(id, out var cached))
            return cached;
        if (AlEnumMetadataRegistry.TryGet(id, out var e))
        {
            try
            {
                var meta = new AlEnumOptionMetadata(e.Name, e.Id, e.Options, e.Indexes);
                return _alEnumCache.GetOrAdd(id, meta);
            }
            catch
            {
                // Fall through to Default on any construction issue (e.g.
                // duplicate option string the base ctor refuses) — preserves
                // pre-patch behavior for that one enum.
            }
        }
        return NCLOptionMetadata.Default;
    }
}

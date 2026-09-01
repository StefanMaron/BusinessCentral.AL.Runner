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
using System.Globalization;
using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;
using AlRunner.Patches;
using NCLOptionMetadata = Microsoft.Dynamics.Nav.Runtime.NCLOptionMetadata;
using NavCodeunitHandle = Microsoft.Dynamics.Nav.Runtime.NavCodeunitHandle;
using NavInterfaceHandle = Microsoft.Dynamics.Nav.Runtime.NavInterfaceHandle;
using NavList = Microsoft.Dynamics.Nav.Runtime.NavList<Microsoft.Dynamics.Nav.Runtime.NavText>;
using NavListInt = Microsoft.Dynamics.Nav.Runtime.NavList<int>;
using NavOption = Microsoft.Dynamics.Nav.Runtime.NavOption;
using NavText = Microsoft.Dynamics.Nav.Runtime.NavText;
using ITreeObject = Microsoft.Dynamics.Nav.Runtime.ITreeObject;
using StringHelper = Microsoft.Dynamics.Nav.Runtime.StringHelper;

namespace AlRunner;

/// <summary>
/// Captures (id, name, options[], indexes[], captions[]) for every AL enum compiled by
/// <see cref="BcCompiler"/>. Populated at emit time by <c>CaptureOutputter</c>;
/// consumed at runtime by <see cref="BcRuntime.NCLEnumMetadata_CreateById"/>.
/// </summary>
public static class AlEnumMetadataRegistry
{
    // Captions is parallel to Options/Indexes: Captions[i] is value i's declared
    // `Caption = '...'` text, or null when the value declares no Caption at all (issue
    // #1775). Null is meaningful — "declares none" — and the consumer
    // (AlEnumOptionMetadata.GetCaptionFromIndex) applies AL's own default (the member
    // name) rather than this record baking that default in, matching the same
    // null-means-"declares none" convention RecordPatches.AlSourceParser already uses
    // for field-level Caption. A null Captions array (not just a null element) means
    // "captured before caption-ingestion existed / caller didn't supply one" and is
    // treated exactly like an array of all-nulls.
    public sealed record Entry(int Id, string Name, string[] Options, int[] Indexes, int[][] Implementations, string?[]? Captions = null);

    // Base-enum registrations, keyed by the enum's own object Id. This also
    // absorbs precompiled-dependency enums (RegisterFromAppPath) and cache
    // replay (Program.cs sidecar), both of which hand in already-flattened
    // entries and must keep last-writer-wins semantics for id collisions.
    private static readonly ConcurrentDictionary<int, Entry> _byId = new();

    // Enumextension registrations, keyed by the TARGET base enum's object Id
    // (not the extension's own Id — enum and enumextension objects live in
    // separate AL object-type namespaces and their numbers can coincide or
    // differ independently of each other). AL emits one AddApplicationObject
    // per enumextension in addition to the base enum's, and BC's own
    // EnumExtensionTypeSymbol.Values never includes the base's values (see
    // SourceEnumExtensionTypeSymbol.LazyGetEnumValues) — only the extension's
    // own declared values. So base and extension entries are accumulated
    // separately here and merged on read (TryGet/Snapshot), because emit
    // order between a base enum and its extension(s) is not guaranteed
    // (issue #1625: registering both under one dictionary slot made whichever
    // fired last silently clobber the other instead of merging).
    private static readonly ConcurrentDictionary<int, ImmutableList<Entry>> _extByTargetId = new();

    /// <summary>Last-writer-wins for the base enum itself; bundle-wide enum-id
    /// collisions are quarantined upstream. Enumextension values are tracked
    /// separately — see <see cref="RegisterExtension"/>.</summary>
    public static void Register(int id, string name, string[] options, int[] indexes, int[][]? implementations = null, string?[]? captions = null)
    {
        if (options == null || indexes == null) return;
        if (options.Length != indexes.Length) return;
        implementations ??= Array.Empty<int[]>();
        if (implementations.Length != options.Length)
            implementations = Array.Empty<int[]>();
        if (captions != null && captions.Length != options.Length)
            captions = null;
        _byId[id] = new Entry(id, name ?? string.Empty, options, indexes, implementations, captions);
    }

    /// <summary>
    /// Registers an enumextension's own values against the base enum id it
    /// extends. Multiple extensions targeting the same base enum accumulate;
    /// none of them overwrite the base entry or each other.
    /// </summary>
    public static void RegisterExtension(int targetId, string name, string[] options, int[] indexes, int[][]? implementations = null, string?[]? captions = null)
    {
        if (options == null || indexes == null) return;
        if (options.Length != indexes.Length) return;
        implementations ??= Array.Empty<int[]>();
        if (implementations.Length != options.Length)
            implementations = Array.Empty<int[]>();
        if (captions != null && captions.Length != options.Length)
            captions = null;
        var entry = new Entry(targetId, name ?? string.Empty, options, indexes, implementations, captions);
        _extByTargetId.AddOrUpdate(
            targetId,
            ImmutableList.Create(entry),
            (_, list) => list.Add(entry));
    }

    /// <summary>
    /// Merges the base entry (if any) with every enumextension registered
    /// against <paramref name="id"/>, base values first, in declaration
    /// order, then each extension's values in the order the extensions were
    /// registered. Ordinal collisions keep the earliest occurrence.
    /// </summary>
    public static bool TryGet(int id, out Entry entry)
    {
        _byId.TryGetValue(id, out var baseEntry);
        _extByTargetId.TryGetValue(id, out var extensions);

        if (baseEntry == null && (extensions == null || extensions.IsEmpty))
        {
            entry = null!;
            return false;
        }

        if (extensions == null || extensions.IsEmpty)
        {
            entry = baseEntry!;
            return true;
        }

        var name = baseEntry?.Name ?? extensions[0].Name;
        var options = new List<string>();
        var indexes = new List<int>();
        var implementations = new List<int[]>();
        var captions = new List<string?>();
        var seenOrdinals = new HashSet<int>();

        void AddValues(Entry e)
        {
            for (int i = 0; i < e.Options.Length; i++)
            {
                if (!seenOrdinals.Add(e.Indexes[i]))
                    continue; // earliest occurrence wins on ordinal collision
                options.Add(e.Options[i]);
                indexes.Add(e.Indexes[i]);
                implementations.Add(i < e.Implementations.Length ? e.Implementations[i] : Array.Empty<int>());
                captions.Add(e.Captions != null && i < e.Captions.Length ? e.Captions[i] : null);
            }
        }

        if (baseEntry != null)
            AddValues(baseEntry);
        foreach (var ext in extensions)
            AddValues(ext);

        entry = new Entry(id, name, options.ToArray(), indexes.ToArray(), implementations.ToArray(), captions.ToArray());
        return true;
    }

    public static void Clear()
    {
        _byId.Clear();
        _extByTargetId.Clear();
    }

    public static int Count => _byId.Count;

    public static void RegisterFromAppPath(string appPath)
    {
        if (string.IsNullOrEmpty(appPath) || !File.Exists(appPath)) return;
        try
        {
            foreach (var enumSymbol in BcAppSymbolCache.Get(appPath).Enums)
                Register(
                    enumSymbol.Id,
                    enumSymbol.Name,
                    enumSymbol.Options.ToArray(),
                    enumSymbol.Indexes.ToArray(),
                    enumSymbol.Implementations.Select(i => i.ToArray()).ToArray(),
                    enumSymbol.Captions?.ToArray());
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[EnumMetadata] failed to read {Path.GetFileName(appPath)}: {ex.Message}");
        }
    }

    /// <summary>Snapshot of all currently registered entries, MERGED with any
    /// enumextension values (see <see cref="TryGet"/>) — the cache sidecar
    /// replays these via plain <see cref="Register"/> calls on a cache HIT, so
    /// each snapshotted entry must already carry the full base+extension set.
    /// Used by the AL-output cache sidecar writer (Program.cs). Order is
    /// stable (sorted by Id) so the sidecar is byte-deterministic across
    /// runs.</summary>
    public static IReadOnlyList<Entry> Snapshot()
    {
        var ids = new HashSet<int>(_byId.Keys);
        ids.UnionWith(_extByTargetId.Keys);
        var result = new List<Entry>(ids.Count);
        foreach (var id in ids)
            if (TryGet(id, out var merged))
                result.Add(merged);
        return result.OrderBy(e => e.Id).ToList();
    }

    /// <summary>Every enum id currently registered (base ids ∪ enumextension target
    /// ids) — used by <see cref="DependencyLoader"/> to snapshot "before this dep's
    /// emit" / "after this dep's emit" sets so a source-dep compile can persist only
    /// the entries IT contributed to its own cache sidecar (issue #1731's fix).</summary>
    public static int[] Ids
    {
        get
        {
            var ids = new HashSet<int>(_byId.Keys);
            ids.UnionWith(_extByTargetId.Keys);
            return ids.ToArray();
        }
    }

    /// <summary>
    /// Serialize the given enum ids' MERGED entries (base + enumextension values
    /// already flattened by <see cref="TryGet"/>) to a sidecar file. Mirrors
    /// Program.cs's bundle-level <c>SaveEnumRegistrySidecar</c> (schema v3), but
    /// scoped to <paramref name="onlyIds"/> so the dependency-loader's per-dep
    /// cache sidecar does not leak sibling-app/bundle entries into its own file —
    /// same convention as <see cref="AlReportMetadataRegistry.SaveSidecar(string, IEnumerable{int})"/>.
    /// Returns the number of entries written.
    /// </summary>
    public static int SaveSidecar(string path, IEnumerable<int> onlyIds)
    {
        var idSet = new HashSet<int>(onlyIds);
        var entries = new List<Entry>(idSet.Count);
        foreach (var id in idSet)
            if (TryGet(id, out var merged))
                entries.Add(merged);
        entries = entries.OrderBy(e => e.Id).ToList();

        var dto = new
        {
            enums = entries.Select(e => new
            {
                id = e.Id,
                name = e.Name,
                options = e.Options,
                indexes = e.Indexes,
                implementations = e.Implementations,
                captions = e.Captions,
            }).ToArray(),
        };
        var json = System.Text.Json.JsonSerializer.Serialize(dto);
        File.WriteAllText(path, json);
        return entries.Count;
    }

    /// <summary>
    /// Replay entries from a sidecar written by <see cref="SaveSidecar"/>. Each
    /// entry is already the merged base+extension set, so replay uses plain
    /// <see cref="Register"/> (never <see cref="RegisterExtension"/>) — matching
    /// how Program.cs's bundle-level sidecar replay works. Throws on corrupt JSON;
    /// callers treat that as a cache MISS and rebuild. Returns replayed entry count.
    /// </summary>
    public static int LoadSidecar(string path)
    {
        var json = File.ReadAllText(path);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("enums", out var arr)
            || arr.ValueKind != System.Text.Json.JsonValueKind.Array)
            throw new InvalidDataException("enum-registry.json: missing 'enums' array");
        int count = 0;
        foreach (var e in arr.EnumerateArray())
        {
            int id = e.GetProperty("id").GetInt32();
            string name = e.GetProperty("name").GetString() ?? string.Empty;
            var optsEl = e.GetProperty("options");
            var idxEl = e.GetProperty("indexes");
            var opts = new string[optsEl.GetArrayLength()];
            int oi = 0;
            foreach (var o in optsEl.EnumerateArray()) opts[oi++] = o.GetString() ?? string.Empty;
            var idxs = new int[idxEl.GetArrayLength()];
            int ii = 0;
            foreach (var x in idxEl.EnumerateArray()) idxs[ii++] = x.GetInt32();
            int[][] implementations = Array.Empty<int[]>();
            if (e.TryGetProperty("implementations", out var implEl)
                && implEl.ValueKind == System.Text.Json.JsonValueKind.Array
                && implEl.GetArrayLength() == opts.Length)
            {
                implementations = new int[implEl.GetArrayLength()][];
                int vi = 0;
                foreach (var valueImplEl in implEl.EnumerateArray())
                {
                    if (valueImplEl.ValueKind != System.Text.Json.JsonValueKind.Array)
                    {
                        implementations = Array.Empty<int[]>();
                        break;
                    }
                    var ids = new int[valueImplEl.GetArrayLength()];
                    int idi = 0;
                    foreach (var implId in valueImplEl.EnumerateArray())
                        ids[idi++] = implId.GetInt32();
                    implementations[vi++] = ids;
                }
            }
            string?[]? captions = null;
            if (e.TryGetProperty("captions", out var capEl)
                && capEl.ValueKind == System.Text.Json.JsonValueKind.Array
                && capEl.GetArrayLength() == opts.Length)
            {
                captions = new string?[capEl.GetArrayLength()];
                int ci = 0;
                foreach (var c in capEl.EnumerateArray())
                    captions[ci++] = c.ValueKind == System.Text.Json.JsonValueKind.Null ? null : c.GetString();
            }
            Register(id, name, opts, idxs, implementations, captions);
            count++;
        }
        return count;
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
    private readonly int[][] _implementations;
    private readonly string?[] _captions;
    private readonly string _name;
    private readonly int _id;

    public AlEnumOptionMetadata(string name, int id, string[] options, int[] indexes, int[][]? implementations = null, string?[]? captions = null)
        : base(JoinOptions(options))
    {
        _name = name;
        _id = id;
        _ordinalValues = indexes;
        _implementations = implementations != null && implementations.Length == options.Length
            ? implementations
            : Array.Empty<int[]>();
        _captions = captions != null && captions.Length == options.Length
            ? captions
            : new string?[options.Length];
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
    // here using our _ordinalValues. Likewise for GetCaptionFromIndex.
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

    // Issue #1775 — Format(<enum value>) and FieldRef.GetEnumValueCaptionFromOrdinalValue
    // both chain through here; a value's declared `Caption = '...'` (captured by
    // BcCompiler.CaptureOutputter off the resolved IEnumValueSymbol's Caption property
    // at emit time — see ReadEnumValueCaption) must win over the member name. A value
    // with NO declared Caption falls back to the member name, matching AL's own default
    // (the same fallback GetOptionFromIndex already returns) rather than returning an
    // empty string — an enum value's caption is never blank unless the AL author wrote
    // `Caption = '';` explicitly, and that case is indistinguishable from "no capture"
    // only in the sense that BOTH already resolve to the empty string here, which is
    // correct for BOTH.
    public override string GetCaptionFromIndex(int index)
    {
        for (int i = 0; i < _ordinalValues.Length; i++)
        {
            if (_ordinalValues[i] == index)
                return _captions[i] ?? _optionNames[i];
        }
        return GetOptionFromIndex(index);
    }

    public override bool IsValidOrdinal(int ordinal)
    {
        for (int i = 0; i < _ordinalValues.Length; i++)
            if (_ordinalValues[i] == ordinal) return true;
        return false;
    }

    // #2302 — text → ordinal resolution must match BC's own NCLEnumMetadata, not a
    // stricter ordinal string.Equals. BC's decompiled body is:
    //
    //     int r = StringHelper.FindStringInStringArrayUsingCurrentCulture(Options, option);
    //     if (r != -1) return OrdinalValues[r];
    //     if (int.TryParse(option, out r)) return r;
    //     return -1;
    //
    // and that helper TRIMS both sides, compares case-INSENSITIVELY in the session's
    // culture, and skips members whose name is zero-length. Delegating to the helper
    // rather than restating it keeps this observably identical to the service tier
    // (including the zero-length skip, which is the only reason " " and "" differ).
    //
    // The practical consequence is AL's blank enum member. `value(0; " ")` is named with a
    // single space, so `SetFilter(<enum field>, '<>''''')` — Base App codeunit 5055
    // "CustVendBank-Update" line 34, reached by ~170 of Microsoft's Tests-SINGLESERVER
    // tests — evaluates '' against it. Under the old ordinal compare that raised
    // NavNCLInvalidOptionStringException ("'' is not an option"); under BC's own rule
    // " ".Trim() == "".Trim() and it resolves to ordinal 0.
    public override int GetIndexFromOption(string option)
    {
        var i = StringHelper.FindStringInStringArrayUsingCurrentCulture(_optionNames, option);
        if (i != -1)
            return _ordinalValues[i];
        if (int.TryParse(option, out var ord))
            return ord;
        return -1;
    }

    // BC's NCLEnumMetadata.GetIndexFromCaption is NOT a synonym for GetIndexFromOption: it
    // matches against each value's CAPTION (falling back to the member name where a value
    // declares none), trims both sides, and compares case-insensitively — in the caption's
    // own language culture, or InvariantCulture when there is no translated caption to
    // carry an LCID. Our captures carry no LCID, so invariant is the whole of that rule
    // here. It also has NO zero-length guard, unlike the option side.
    public override int GetIndexFromCaption(string caption)
    {
        var target = caption.Trim();
        for (int i = 0; i < _optionNames.Length; i++)
        {
            var text = (_captions[i] ?? _optionNames[i]) ?? string.Empty;
            if (string.Compare(target, text.Trim(), ignoreCase: true, CultureInfo.InvariantCulture) == 0)
                return _ordinalValues[i];
        }
        return -1;
    }

    private readonly string[] _optionNames;

    public int GetImplementationCodeunitIdPublic(int ordinalValue, int interfaceIndex)
    {
        if (interfaceIndex < 0 || _implementations.Length == 0)
            return -1;
        for (int i = 0; i < _ordinalValues.Length; i++)
        {
            if (_ordinalValues[i] != ordinalValue)
                continue;
            var implementations = _implementations[i];
            return interfaceIndex < implementations.Length ? implementations[interfaceIndex] : -1;
        }
        return -1;
    }

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
                var meta = new AlEnumOptionMetadata(e.Name, e.Id, e.Options, e.Indexes, e.Implementations, e.Captions);
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

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static NavInterfaceHandle ALCompiler_ToInterfaceFromOption(ITreeObject parentOfResult, NavOption optionValue, int interfaceIndex)
    {
        var implementationCodeunitId = optionValue.NavOptionMetadata is AlEnumOptionMetadata alMetadata
            ? alMetadata.GetImplementationCodeunitIdPublic(optionValue.Value, interfaceIndex)
            : TryGetImplementationCodeunitIdViaReflection(optionValue.NavOptionMetadata, optionValue.Value, interfaceIndex);
        if (implementationCodeunitId < 0)
            throw new InvalidOperationException(
                $"Unable to cast enum '{optionValue.NavOptionMetadata.OptionString}' value '{optionValue}' to interface at index {interfaceIndex}.");

        // Build the implementing codeunit handle and wrap its live target in the interface
        // handle. We must NOT dispose `handle` afterwards: disposing the NavCodeunitHandle
        // tears down the codeunit instance's tree (including child handles such as a var-record
        // field like Codeunit7035.vendor, allocated in InitializeComponent). The returned
        // NavInterfaceHandle keeps a reference to that exact instance, so a later interface
        // dispatch (e.g. Price Source - Vendor.GetId reading `vendor.Target`) would observe a
        // disposed handle and get null → NRE. This mirrors BC's own ToInterface overloads
        // (ALCompiler.ToInterface(ITreeObject, NavApplicationObjectBaseHandle<T>) /
        // (ITreeObject, NavApplicationObjectBase)) which wrap the target and never dispose the
        // source handle — ownership of the target transfers to the interface handle.
        var handle = new NavCodeunitHandle(parentOfResult, implementationCodeunitId);
        return new NavInterfaceHandle(parentOfResult, handle.Target);
    }

    private static readonly MethodInfo? GetImplementationCodeunitIdMethod = typeof(NCLOptionMetadata).GetMethod(
        "GetImplementationCodeunitId",
        BindingFlags.Instance | BindingFlags.NonPublic);

    private static int TryGetImplementationCodeunitIdViaReflection(NCLOptionMetadata metadata, int ordinalValue, int interfaceIndex)
    {
        if (GetImplementationCodeunitIdMethod == null)
            return -1;
        try
        {
            return (int)(GetImplementationCodeunitIdMethod.Invoke(metadata, new object[] { ordinalValue, interfaceIndex }) ?? -1);
        }
        catch
        {
            return -1;
        }
    }
}

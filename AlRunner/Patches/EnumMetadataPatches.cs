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
using NavMetadataNotFoundException = Microsoft.Dynamics.Nav.Types.NavMetadataNotFoundException;

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
    // DefaultImplementations / UnknownImplementations are the ENUM-level fallbacks, indexed
    // by interface-declaration index exactly like a value's own Implementations entry (issue
    // #2306). They are not parallel to Options — one list per enum, not per value. An empty
    // array means the enum declares none, which is how most enums are written.
    public sealed record Entry(int Id, string Name, string[] Options, int[] Indexes, int[][] Implementations, string?[]? Captions = null,
        int[]? DefaultImplementations = null, int[]? UnknownImplementations = null);

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
    public static void Register(int id, string name, string[] options, int[] indexes, int[][]? implementations = null, string?[]? captions = null,
        int[]? defaultImplementations = null, int[]? unknownImplementations = null)
    {
        if (options == null || indexes == null) return;
        if (options.Length != indexes.Length) return;
        implementations ??= Array.Empty<int[]>();
        if (implementations.Length != options.Length)
            implementations = Array.Empty<int[]>();
        if (captions != null && captions.Length != options.Length)
            captions = null;
        _byId[id] = new Entry(id, name ?? string.Empty, options, indexes, implementations, captions,
            defaultImplementations, unknownImplementations);
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
        // An enumextension declares no DefaultImplementation/UnknownImplementation of its
        // own — those are properties of the base enum — so the merge in TryGet takes the
        // base entry's, and this entry carries none.
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

        entry = new Entry(id, name, options.ToArray(), indexes.ToArray(), implementations.ToArray(), captions.ToArray(),
            baseEntry?.DefaultImplementations, baseEntry?.UnknownImplementations);
        return true;
    }

    public static void Clear()
    {
        _byId.Clear();
        _extByTargetId.Clear();
    }

    public static int Count => _byId.Count;

    /// <summary>
    /// Register every enum a precompiled dependency .app declares.
    ///
    /// <para>#3143: this had the same swallow as the ten dependency-symbol reads that issue
    /// listed — `catch (Exception)` plus an `[EnumMetadata]`-tagged stderr line, which Log's
    /// default-verbosity filter drops because it starts with a bracketed component tag. The
    /// effect was that an unreadable .app registered NO enums, and AL casting one of them to
    /// its interface then failed as an ordinary-looking AL error naming the enum rather than
    /// the package that could not be read.</para>
    ///
    /// <para>#3143 confirmed what that issue only suspected: this method has NO live callers.
    /// <c>RecordPatches.AddBcAppPath</c> is the only path by which a dependency's enums reach
    /// this registry, and it already reads eagerly and refuses loudly. The swallow is
    /// converted rather than left alone because it is public and a future caller would
    /// inherit it silently; the empty/absent guard above is UNCHANGED, because "no path
    /// given" and "the file is not there" are genuinely "nothing to register", not
    /// "could not find out".</para>
    /// </summary>
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
                    enumSymbol.Captions?.ToArray(),
                    enumSymbol.DefaultImplementations?.ToArray(),
                    enumSymbol.UnknownImplementations?.ToArray());
        }
        catch (Exception ex) when (ex is not AlRunner.Infrastructure.BcAppSymbolReadException)
        {
            throw new AlRunner.Infrastructure.BcAppSymbolReadException(appPath, "enums", ex);
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
    /// Raw (unmerged) entries for serialization: each base registration paired with
    /// <c>ExtendsTargetId = null</c>, and each enumextension's OWN values (never
    /// merged with the base) paired with <c>ExtendsTargetId = &lt;the base id it
    /// targets&gt;</c>.
    ///
    /// Deliberately NOT <see cref="TryGet"/>/<see cref="Snapshot"/>'s merged view: a
    /// sidecar built from the merged view cannot round-trip through
    /// <see cref="Register"/> alone without one of two failures (issue #2709) —
    /// replayed AFTER the target's real base registration, it clobbers the base
    /// (multi-bundle: Base App enum 7011 lost); replayed BEFORE and then the real
    /// base registers over it, the extension's values are lost (single-bundle:
    /// enumextension value no longer casts). Keeping base and extension identities
    /// distinct in the sidecar — exactly as <see cref="_byId"/>/<see cref="_extByTargetId"/>
    /// already keep them distinct in memory — lets replay use
    /// <see cref="Register"/>/<see cref="RegisterExtension"/> the same way emit does,
    /// so a later real base (or extension) registration always merges instead of
    /// overwriting.
    /// </summary>
    public static IEnumerable<(Entry Entry, int? ExtendsTargetId)> SnapshotRaw(IEnumerable<int>? onlyIds = null)
    {
        IEnumerable<int> ids;
        if (onlyIds != null)
            ids = new HashSet<int>(onlyIds);
        else
        {
            var all = new HashSet<int>(_byId.Keys);
            all.UnionWith(_extByTargetId.Keys);
            ids = all;
        }
        foreach (var id in ids.OrderBy(i => i))
        {
            if (_byId.TryGetValue(id, out var baseEntry))
                yield return (baseEntry, null);
            if (_extByTargetId.TryGetValue(id, out var exts))
                foreach (var ext in exts)
                    yield return (ext, id);
        }
    }

    /// <summary>
    /// Serialize the given enum ids' RAW (unmerged) entries — see
    /// <see cref="SnapshotRaw"/> for why raw, not merged — to a sidecar file (schema
    /// v2: adds the <c>extends</c> marker; see #2709). Mirrors Program.cs's
    /// bundle-level <c>SaveEnumRegistrySidecar</c>, but scoped to
    /// <paramref name="onlyIds"/> so the dependency-loader's per-dep cache sidecar
    /// does not leak sibling-app/bundle entries into its own file — same convention
    /// as <see cref="AlReportMetadataRegistry.SaveSidecar(string, IEnumerable{int})"/>.
    /// Returns the number of entries written.
    /// </summary>
    public static int SaveSidecar(string path, IEnumerable<int> onlyIds)
    {
        var raw = SnapshotRaw(onlyIds).ToList();

        var dto = new
        {
            enums = raw.Select(r => new
            {
                id = r.Entry.Id,
                name = r.Entry.Name,
                options = r.Entry.Options,
                indexes = r.Entry.Indexes,
                implementations = r.Entry.Implementations,
                captions = r.Entry.Captions,
                // #2306 — the enum-level Default/UnknownValue implementation fallbacks. Absent
                // from a sidecar written before this and read back as null, which means
                // "declares none" and is the pre-#2306 behaviour; the AL-output cache key
                // hashes the runner assembly's own content, so such a sidecar is unreachable
                // from this build anyway.
                defaultImplementations = r.Entry.DefaultImplementations,
                unknownImplementations = r.Entry.UnknownImplementations,
                // #2709 — null for a base registration; the base enum id it extends for an
                // enumextension's own (unmerged) entry. Absent/null on replay means "plain
                // Register", exactly like a pre-#2709 sidecar (which never carried this
                // property) — but the cache key already hashes the runner assembly's own
                // content, so a pre-#2709 sidecar is unreachable from this build anyway; the
                // fallback is defensive, not load-bearing.
                extends = r.ExtendsTargetId,
            }).ToArray(),
        };
        var json = System.Text.Json.JsonSerializer.Serialize(dto);
        File.WriteAllText(path, json);
        return raw.Count;
    }

    /// <summary>
    /// Replay entries from a sidecar written by <see cref="SaveSidecar"/>. Each
    /// entry is already the merged base+extension set, so replay uses plain
    /// <see cref="Register"/> (never <see cref="RegisterExtension"/>) — matching
    /// how Program.cs's bundle-level sidecar replay works. Throws on corrupt JSON;
    /// callers treat that as a cache MISS and rebuild. Returns replayed entry count.
    /// </summary>
    /// <summary>A sidecar's optional int-array property, or null when absent/empty —
    /// "declares none" (issue #2306).</summary>
    private static int[]? ReadIdList(System.Text.Json.JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var el) || el.ValueKind != System.Text.Json.JsonValueKind.Array)
            return null;
        var ids = new int[el.GetArrayLength()];
        int k = 0;
        foreach (var v in el.EnumerateArray()) ids[k++] = v.GetInt32();
        return ids.Length > 0 ? ids : null;
    }

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
            // #2709 — a present, non-null `extends` marks this entry as an
            // enumextension's own (unmerged) values, targeting the base enum id it
            // names; replay through RegisterExtension so it accumulates alongside
            // whatever base/other-extension entries this process already holds,
            // instead of overwriting the base enum's own _byId slot. Absent (older
            // sidecar written before #2709, or a plain base entry) replays through
            // Register exactly as before.
            int? extendsTargetId = null;
            if (e.TryGetProperty("extends", out var extEl) && extEl.ValueKind == System.Text.Json.JsonValueKind.Number)
                extendsTargetId = extEl.GetInt32();

            if (extendsTargetId.HasValue)
                RegisterExtension(extendsTargetId.Value, name, opts, idxs, implementations, captions);
            else
                Register(id, name, opts, idxs, implementations, captions,
                    ReadIdList(e, "defaultImplementations"), ReadIdList(e, "unknownImplementations"));
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
    private readonly int[] _defaultImplementations;
    private readonly int[] _unknownImplementations;
    private readonly string?[] _captions;
    private readonly string _name;
    private readonly int _id;

    public AlEnumOptionMetadata(string name, int id, string[] options, int[] indexes, int[][]? implementations = null, string?[]? captions = null,
        int[]? defaultImplementations = null, int[]? unknownImplementations = null)
        : base(JoinOptions(options))
    {
        _defaultImplementations = defaultImplementations ?? Array.Empty<int>();
        _unknownImplementations = unknownImplementations ?? Array.Empty<int>();
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

    // #2306 — the same three-step fallback BC's NCLEnumMetadata.GetImplementationCodeunitId
    // runs: the value's own Implementation, then the enum's DefaultImplementation for a value
    // the enum declares, then its UnknownImplementation for an ordinal it does not. Only the
    // first step existed here, so an enum that names ONLY a DefaultImplementation — Base App
    // 205 "Alt. Cust VAT Reg. Doc." is one, and Codeunit 207.GetAltCustVATRegDocImpl casts it
    // to an interface on every Sales Header insert — resolved to -1 and threw.
    //
    // BC throws NavNCLArgumentOutOfRangeException where the fallback list is missing or too
    // short; this returns -1 for those, which the single caller
    // (BcRuntime.ALCompiler_ToInterfaceFromOption) turns into its own throw naming the enum,
    // the value and the interface index. Same outcome — a loud failure that identifies the
    // enum — reached one frame later.
    /// <summary>The AL enum's own object id and name, for diagnostics — the base class's
    /// Name/Id virtuals are internal, so a failure message cannot otherwise say WHICH enum
    /// it was looking at.</summary>
    public (int Id, string Name) IdentityForDiagnostics => (_id, _name);

    public int GetImplementationCodeunitIdPublic(int ordinalValue, int interfaceIndex)
    {
        if (interfaceIndex < 0)
            return -1;

        var known = false;
        for (int i = 0; i < _ordinalValues.Length; i++)
        {
            if (_ordinalValues[i] != ordinalValue)
                continue;
            known = true;
            var implementations = i < _implementations.Length ? _implementations[i] : Array.Empty<int>();
            if (interfaceIndex < implementations.Length)
                return implementations[interfaceIndex];
            break;
        }

        var fallback = known ? _defaultImplementations : _unknownImplementations;
        if (interfaceIndex >= fallback.Length)
            return -1;
        // BC reads `if (num <= 0) return -1;` — a declared 0 means "no implementer", never
        // codeunit 0, which would otherwise build a handle for an object that does not exist.
        var codeunitId = fallback[interfaceIndex];
        return codeunitId > 0 ? codeunitId : -1;
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
                var meta = new AlEnumOptionMetadata(e.Name, e.Id, e.Options, e.Indexes, e.Implementations, e.Captions,
                    e.DefaultImplementations, e.UnknownImplementations);
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

    /// <summary>
    /// BC's own 16 platform ("system") enums — ids 2000000001..2000000017 — registered into
    /// <see cref="AlEnumMetadataRegistry"/> from <c>PlatformMetadataProvider</c>, which is the
    /// inventory BC itself resolves them from.
    ///
    /// <para>Why this is needed at all: every OTHER enum reaches the registry because some app
    /// declares it (<c>RecordPatches.AddBcAppPath</c> reads each dependency .app's symbols).
    /// A system enum is declared by the PLATFORM — it appears in no app's SymbolReference.json,
    /// so nothing put it in the registry, and a field typed by one could not resolve. Base
    /// Application table 2000000132 has such a field (enum 2000000002 "Entity Text Scenario").</para>
    ///
    /// <para>Source of truth, and why it is BC's own: <c>GetEnumALCodeById</c> returns the AL
    /// source BC ships for the enum, so the values, ordinals and captions parsed here are
    /// Microsoft's own declaration rather than anything this runner invents. Measured on BC
    /// 28.1: 16 enums, 57 <c>value(...)</c> declarations, 73 captions, all in the one shape the
    /// regex below matches; three of the sixteen declare no values at all (they are extensible
    /// enums an app is expected to extend), and an empty option set is the correct answer for
    /// those rather than a reason to refuse.</para>
    /// </summary>
    private static int _systemEnumsRegistered;

    private static readonly System.Text.RegularExpressions.Regex _rxSystemEnumValue = new(
        // value(<ordinal>; <Name>) — the name is bare or "quoted"; an optional { Caption = '...'; }
        // body follows. Both name forms occur in BC's own source (measured: 27 of 57 quoted).
        @"value\s*\(\s*(?<ord>-?\d+)\s*;\s*(?:""(?<qname>[^""]*)""|(?<name>[A-Za-z_][A-Za-z0-9_]*))\s*\)"
        + @"(?<body>\s*\{(?<inner>[^{}]*)\})?",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex _rxSystemEnumCaption = new(
        @"Caption\s*=\s*'(?<cap>[^']*)'", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Register BC's platform enums, once per process. Idempotent and never throws: a BC build
    /// that does not expose this inventory leaves the registry exactly as it was, and a field
    /// typed by a system enum then fails the same loud way it did before this existed — a
    /// missing optimisation, not a silent wrong answer.
    /// </summary>
    internal static void EnsureSystemEnumsRegistered()
    {
        if (System.Threading.Interlocked.Exchange(ref _systemEnumsRegistered, 1) != 0) return;
        try
        {
            var pmpT = typeof(NCLOptionMetadata).Assembly
                .GetType("Microsoft.Dynamics.Nav.Runtime.PlatformMetadataProvider");
            var inst = pmpT?.GetProperty("Instance",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null);
            // BcShape.FindMethod, not a name-only GetMethod (#3069): an overload appearing on
            // either of these would otherwise pick one silently, and BC moving them is exactly
            // the case this whole method must degrade on rather than guess through. Absence
            // still returns null, which the guard below treats as "no inventory to read".
            var getEnums = pmpT == null ? null : AlRunner.Infrastructure.BcShape.FindMethod(
                pmpT, "GetSystemEnums", BindingFlags.Public | BindingFlags.Instance,
                "system enum registration", "PlatformMetadataProvider.GetSystemEnums",
                "BC's own inventory of the platform enums no app declares — see "
                + "docs/field-enum-metadata-resolution.md",
                types: Type.EmptyTypes);
            var getAl = pmpT == null ? null : AlRunner.Infrastructure.BcShape.FindMethod(
                pmpT, "GetEnumALCodeById", BindingFlags.Public | BindingFlags.Instance,
                "system enum registration", "PlatformMetadataProvider.GetEnumALCodeById",
                "the AL source BC ships for one platform enum, parsed for its value declarations",
                types: new[] { typeof(int) });
            if (inst == null || getEnums == null || getAl == null) return;
            if (getEnums.Invoke(inst, null) is not System.Collections.IDictionary dict) return;

            foreach (System.Collections.DictionaryEntry de in dict)
            {
                if (de.Key is not int id) continue;
                // Never overwrite a registration an app made: an enumextension on a system enum
                // is registered separately and merged by TryGet, and a base entry already
                // present is the one that came from real symbols.
                if (AlEnumMetadataRegistry.TryGet(id, out _)) continue;

                var name = de.Value?.GetType()
                    .GetProperty("Name", BindingFlags.Public | BindingFlags.Instance)
                    ?.GetValue(de.Value) as string ?? string.Empty;
                if (getAl.Invoke(inst, new object[] { id }) is not byte[] al || al.Length == 0)
                    continue;

                var (options, ordinals, captions) = ParseSystemEnumValues(
                    System.Text.Encoding.UTF8.GetString(al));
                AlEnumMetadataRegistry.Register(id, name, options, ordinals, captions: captions);
            }
        }
        catch
        {
            // Reading BC's inventory is best-effort by design (see the doc comment above): the
            // failure mode without it is the pre-existing loud one, never a wrong value.
        }
    }

    /// <summary>
    /// The <c>value(ord; Name) { Caption = '...'; }</c> declarations of one system enum's AL
    /// source. A doc comment can carry the word <c>value</c>, so matches are taken only from
    /// source with comments stripped.
    /// </summary>
    internal static (string[] Options, int[] Ordinals, string?[] Captions) ParseSystemEnumValues(string alSource)
    {
        var src = StripAlComments(alSource);
        var options = new List<string>();
        var ordinals = new List<int>();
        var captions = new List<string?>();
        foreach (System.Text.RegularExpressions.Match m in _rxSystemEnumValue.Matches(src))
        {
            if (!int.TryParse(m.Groups["ord"].Value, out var ord)) continue;
            var name = m.Groups["qname"].Success ? m.Groups["qname"].Value : m.Groups["name"].Value;
            var inner = m.Groups["inner"].Success ? m.Groups["inner"].Value : string.Empty;
            var cap = _rxSystemEnumCaption.Match(inner);
            options.Add(name);
            ordinals.Add(ord);
            // null means "declares no Caption", the same convention Register/AlEnumOptionMetadata
            // already use — the consumer then applies AL's own default (the member name).
            captions.Add(cap.Success ? cap.Groups["cap"].Value : null);
        }
        return (options.ToArray(), ordinals.ToArray(), captions.ToArray());
    }

    /// <summary>Strip <c>//</c> (including <c>///</c>) and <c>/* */</c> comments, leaving string
    /// literals alone so a caption containing <c>//</c> survives.</summary>
    private static string StripAlComments(string src)
    {
        var sb = new System.Text.StringBuilder(src.Length);
        bool inStr = false, inLine = false, inBlock = false;
        for (int i = 0; i < src.Length; i++)
        {
            char c = src[i];
            char n = i + 1 < src.Length ? src[i + 1] : '\0';
            if (inLine) { if (c == '\n') { inLine = false; sb.Append(c); } continue; }
            if (inBlock) { if (c == '*' && n == '/') { inBlock = false; i++; } continue; }
            if (inStr) { sb.Append(c); if (c == '\'') inStr = false; continue; }
            if (c == '\'') { inStr = true; sb.Append(c); continue; }
            if (c == '/' && n == '/') { inLine = true; continue; }
            if (c == '/' && n == '*') { inBlock = true; i++; continue; }
            sb.Append(c);
        }
        return sb.ToString();
    }

    // NCLFieldEnumMetadata.enumId — `private readonly int`. Read by field, never through the
    // Id property: that property is `GetAppGroupAwareEnumMetadata().Id`, which calls the very
    // method this helper replaces, so reading it here would recurse.
    private static FieldInfo? _fFieldEnumMetadataEnumId;

    /// <summary>
    /// Whether an enum object with this id can be resolved to real values in THIS bundle — i.e.
    /// whether <see cref="NCLFieldEnumMetadata_GetEnumMetadataFromRegistry"/> would answer
    /// rather than raise.
    ///
    /// <para>The MetaField builder asks before stating a field's <c>enumTypeId</c>, because
    /// stating it is what makes BC resolve it (#3594). A bundle that never loaded the symbols
    /// of the app declaring the enum has nothing to resolve: the corpus names Base Application
    /// as an explicit dependency and so registers its 721 enums, while a bundle declaring only
    /// an <c>application</c> FLOOR with <c>dependencies: []</c> resolves the stripped platform
    /// packages, which carry no enum symbols at all. Stating an id that cannot be resolved
    /// turned the whole bundle into a 0-of-N abort — the exact failure #3594 exists to remove,
    /// reproduced on a different manifest shape
    /// (<c>AlRunner.Tests/Fixtures/BcFloorSkip/healthy-suite</c>).</para>
    ///
    /// <para>Not stating the id is the FAITHFUL answer for such a bundle, not a silent fake:
    /// with no enum id the upstream BC factory builds the plain
    /// <c>NCLOptionMetadataWithCaptions</c> from the field's own inline option string, which is
    /// exactly what the runner answered before #3594 and what every such bundle has always
    /// seen. The value is stated wherever it can be backed and withheld where it cannot, rather
    /// than asserted everywhere and failing where it is unbacked.</para>
    /// </summary>
    public static bool CanResolveEnumMetadata(int enumId)
    {
        if (enumId == 0) return false;
        EnsureSystemEnumsRegistered();
        return AlEnumMetadataRegistry.TryGet(enumId, out _);
    }

    /// <summary>
    /// Replacement for <c>NCLFieldEnumMetadata.GetEnumMetadataFromMetadataProvider()</c> — the
    /// single point through which every accessor of an <c>Enum</c>-typed FIELD's option
    /// metadata resolves. <c>OptionString</c>, <c>Options</c>, <c>OrdinalValues</c>,
    /// <c>GetNames</c> and the rest all funnel through <c>GetAppGroupAwareEnumMetadata</c>,
    /// which caches per app group and calls this one virtual.
    ///
    /// <para>Observably equivalent: the real body is
    /// <c>NavGlobal.MetadataProvider.GetEnumMetadata(enumId)</c>, which resolves
    /// <c>NCLMetadata.TryGetMetaApplicationObject(ObjectType.Enum, id)</c> and returns that
    /// object's declared values, ordinals and captions. <see cref="AlEnumMetadataRegistry"/>
    /// holds exactly those, for a source-compiled enum and for a precompiled dependency's
    /// alike — <c>RecordPatches.AddBcAppPath</c> loads every dependency .app's enums into it.
    /// So this substitutes the lookup's DATA SOURCE, not its outcome: an enum id nothing
    /// declares still raises BC's own <c>NavMetadataNotFoundException</c>, which is the only
    /// exception <c>TryGetMetaApplicationObject</c> can answer <c>false</c> for.</para>
    ///
    /// <para>Same shape and same reason as
    /// <c>NCLMetaForm.ApplyAppGroupAwareEnumMetadataToPageExpressions</c> (#1896,
    /// PageEnumFieldMetadataPatches.cs): a by-id Enum lookup at a consumption point the runner
    /// never populated. Derivation, the corpus measurement and the residual system-enum gap
    /// are in docs/field-enum-metadata-resolution.md.</para>
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static NCLOptionMetadata NCLFieldEnumMetadata_GetEnumMetadataFromRegistry(object self)
    {
        if (self == null) throw new ArgumentNullException(nameof(self));

        var f = _fFieldEnumMetadataEnumId ??= self.GetType().GetField(
            "enumId", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                "[EnumMetadata] NCLFieldEnumMetadata.enumId not found — Ncl shape changed. "
                + "This helper replaces GetEnumMetadataFromMetadataProvider and has no other "
                + "way to learn which enum the field names.");

        var enumId = (int)(f.GetValue(self) ?? 0);

        // BC's own platform enums are declared by no app, so nothing else puts them in the
        // registry. Registered lazily here — the only consumption point that needs them.
        EnsureSystemEnumsRegistered();

        if (AlEnumMetadataRegistry.TryGet(enumId, out _))
            return NCLEnumMetadata_CreateByIdAlAware(enumId);

        // Not a fallback to Default: an enum id no app declares is a real metadata failure, and
        // BC's own type is what lets TryGetMetaApplicationObject answer `false` rather than
        // propagate. Answering Default here would make an unknown enum silently read as a
        // valueless one — the silent fake loud-failures.md forbids.
        throw new NavMetadataNotFoundException(Microsoft.Dynamics.Nav.Types.ObjectType.Enum, enumId);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static NavInterfaceHandle ALCompiler_ToInterfaceFromOption(ITreeObject parentOfResult, NavOption optionValue, int interfaceIndex)
    {
        var implementationCodeunitId = optionValue.NavOptionMetadata is AlEnumOptionMetadata alMetadata
            ? alMetadata.GetImplementationCodeunitIdPublic(optionValue.Value, interfaceIndex)
            : TryGetImplementationCodeunitIdViaReflection(optionValue.NavOptionMetadata, optionValue.Value, interfaceIndex);
        if (implementationCodeunitId < 0)
            throw new InvalidOperationException(
                $"Unable to cast enum '{optionValue.NavOptionMetadata.OptionString}' value '{optionValue}' to interface at index {interfaceIndex}. "
                + $"Metadata: {Describe(optionValue.NavOptionMetadata)}.");

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

    private static string Describe(NCLOptionMetadata metadata)
        => metadata is AlEnumOptionMetadata al
            ? $"{al.GetType().Name} enum {al.IdentityForDiagnostics.Id} '{al.IdentityForDiagnostics.Name}'"
            : metadata.GetType().Name;

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

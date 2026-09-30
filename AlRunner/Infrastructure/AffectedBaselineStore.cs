// AffectedBaselineStore — the affectedOnly selection baseline on disk, so a server start does not
// cost a full run (#4979 part 1). Rules: docs/server-mode.md#affectedonly-across-server-processes.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AlRunner.Patches;

namespace AlRunner.Infrastructure;

/// <summary>What the change model's baseline for one module held when coverage was recorded on it.</summary>
internal sealed record AffectedModuleSnapshot(
    string ManifestFingerprint,
    string SharedRefsFingerprint,
    Dictionary<string, string> FileHashByPath,
    Dictionary<string, AffectedObjectId> ObjectByPath);

/// <summary>One bundle's selection baseline, the same fields the server keeps in memory.</summary>
internal sealed record AffectedBundleBaseline(
    string EnvironmentKey,
    Dictionary<string, HashSet<string>> Coverage,
    HashSet<string> Unknown,
    HashSet<string> Failing,
    Dictionary<string, HashSet<string>> Events,
    List<SubscriberBinding>? Bindings,
    EventObservability? Observability);

internal sealed record AffectedBaseline(
    Dictionary<string, AffectedModuleSnapshot> Modules,
    Dictionary<string, AffectedBundleBaseline> Bundles);

internal static class AffectedBaselineStore
{
    // Bump on any change to the file's shape or to what a key means; a mismatch is "no baseline".
    // 2: #5008's table keys and the per-bundle "<bundle>" events entry.
    internal const int SchemaVersion = 2;
    internal const string CacheName = "affected-baseline";

    /// <summary>The file for one request's bundle set. Order and duplicates do not change the key.</summary>
    internal static string PathFor(string storeDir, IEnumerable<string> sourcePaths)
    {
        var joined = string.Join("\n", sourcePaths
            .Select(p => Path.GetFullPath(p))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(p => p, StringComparer.Ordinal));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined)))[..32].ToLowerInvariant();
        return Path.Combine(storeDir, hash + ".json");
    }

    internal readonly record struct LoadResult(AffectedBaseline? Baseline, string? Unusable);

    /// <summary>
    /// Null baseline and null reason: no file. Null baseline with a reason: a file exists and
    /// cannot be trusted (unreadable, truncated, another schema), which the caller reports.
    /// </summary>
    internal static LoadResult Load(string path)
    {
        if (!File.Exists(path)) return new(null, null);
        try
        {
            var dto = JsonSerializer.Deserialize<StoreDto>(File.ReadAllBytes(path), Json);
            if (dto == null) return new(null, $"{path} is empty");
            if (dto.Schema != SchemaVersion)
                return new(null, $"{path} has schema version {dto.Schema}, this runner reads {SchemaVersion}");
            return new(FromDto(dto), null);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException
                                       or InvalidDataException or ArgumentException or NotSupportedException)
        {
            return new(null, $"{path} could not be read: {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
        }
    }

    /// <summary>Temp file beside the target, then rename: a concurrent reader sees the old file or the new one.</summary>
    internal static void Write(string path, AffectedBaseline baseline)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(ToDto(baseline), Json);
        AlCacheWriter.AtomicPublish(path, tmp => File.WriteAllBytes(tmp, bytes));
    }

    internal readonly record struct ChangeResult(List<AffectedObjectId> Changed, string? ForceFullReason);

    /// <summary>
    /// The objects whose source differs between the stored snapshots and the change model's
    /// baselines now, by file content hash. Anything the snapshot cannot vouch for is a reason
    /// to run everything, never an empty change set.
    /// </summary>
    internal static ChangeResult ChangedSince(
        IReadOnlyDictionary<string, AffectedModuleSnapshot> stored,
        IEnumerable<string> moduleNames,
        Func<string, AffectedModuleSnapshot?> current)
    {
        var changed = new List<AffectedObjectId>();
        foreach (var module in moduleNames)
        {
            if (!stored.TryGetValue(module, out var before))
                return new(changed, $"it holds no snapshot of module {module}");
            var now = current(module);
            if (now == null)
                return new(changed, $"module {module} has no change-model baseline in this process to compare with");
            if (!string.Equals(before.ManifestFingerprint, now.ManifestFingerprint, StringComparison.Ordinal))
                return new(changed, $"app.json or the preprocessor symbols of {module} changed");
            if (!string.Equals(before.SharedRefsFingerprint, now.SharedRefsFingerprint, StringComparison.Ordinal))
                return new(changed, $"the resolved dependency set of {module} changed");

            foreach (var path in before.FileHashByPath.Keys.Union(now.FileHashByPath.Keys, StringComparer.Ordinal))
            {
                var hadFile = before.FileHashByPath.TryGetValue(path, out var oldHash);
                var hasFile = now.FileHashByPath.TryGetValue(path, out var newHash);
                if (hadFile && hasFile && string.Equals(oldHash, newHash, StringComparison.Ordinal)) continue;

                if (hadFile)
                {
                    if (!before.ObjectByPath.TryGetValue(path, out var oldObject))
                        return new(changed, $"'{path}' changed and was not a single-object file when the baseline was recorded");
                    changed.Add(oldObject);
                }
                if (hasFile)
                {
                    if (!now.ObjectByPath.TryGetValue(path, out var newObject))
                        return new(changed, $"'{path}' changed and does not declare exactly one object the change model tracks");
                    changed.Add(newObject);
                }
            }
        }
        return new(changed.Distinct().ToList(), null);
    }

    // ── serialized form ─────────────────────────────────────────────────────────────────────
    // Coverage and event keys repeat across tests, so each file stores them once in `Keys` and
    // per test as indices into it.

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private sealed class StoreDto
    {
        public int Schema { get; set; }
        public List<string> Keys { get; set; } = new();
        public Dictionary<string, ModuleDto> Modules { get; set; } = new();
        public Dictionary<string, BundleDto> Bundles { get; set; } = new();
    }

    private sealed class ModuleDto
    {
        public string Manifest { get; set; } = "";
        public string Refs { get; set; } = "";
        public Dictionary<string, string> Files { get; set; } = new();
        public Dictionary<string, ObjectDto> Objects { get; set; } = new();
    }

    private sealed class ObjectDto
    {
        public string Kind { get; set; } = "";
        public int? Id { get; set; }
        public string Name { get; set; } = "";
    }

    private sealed class BundleDto
    {
        public string Env { get; set; } = "";
        public Dictionary<string, int[]> Coverage { get; set; } = new();
        public List<string> Unknown { get; set; } = new();
        public List<string> Failing { get; set; } = new();
        public Dictionary<string, int[]> Events { get; set; } = new();
        public List<BindingDto>? Bindings { get; set; }
        public ObservabilityDto? Observability { get; set; }
    }

    private sealed class BindingDto
    {
        public string Kind { get; set; } = "";
        public int Id { get; set; }
        public string Procedure { get; set; } = "";
        public string Event { get; set; } = "";
        public string Identity { get; set; } = "";
    }

    private sealed class ObservabilityDto
    {
        public List<string> Observable { get; set; } = new();
        public Dictionary<string, bool> Publishers { get; set; } = new();
    }

    private static StoreDto ToDto(AffectedBaseline b)
    {
        var dto = new StoreDto { Schema = SchemaVersion };
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        int[] Encode(HashSet<string> keys)
        {
            var result = new int[keys.Count];
            var i = 0;
            foreach (var k in keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                if (!index.TryGetValue(k, out var n))
                {
                    n = dto.Keys.Count;
                    dto.Keys.Add(k);
                    index[k] = n;
                }
                result[i++] = n;
            }
            return result;
        }

        foreach (var (name, m) in b.Modules)
            dto.Modules[name] = new ModuleDto
            {
                Manifest = m.ManifestFingerprint,
                Refs = m.SharedRefsFingerprint,
                Files = new Dictionary<string, string>(m.FileHashByPath, StringComparer.Ordinal),
                Objects = m.ObjectByPath.ToDictionary(
                    kv => kv.Key, kv => new ObjectDto { Kind = kv.Value.Kind, Id = kv.Value.Id, Name = kv.Value.Name },
                    StringComparer.Ordinal),
            };

        foreach (var (bundle, x) in b.Bundles)
            dto.Bundles[bundle] = new BundleDto
            {
                Env = x.EnvironmentKey,
                Coverage = x.Coverage.ToDictionary(kv => kv.Key, kv => Encode(kv.Value), StringComparer.Ordinal),
                Unknown = x.Unknown.OrderBy(t => t, StringComparer.Ordinal).ToList(),
                Failing = x.Failing.OrderBy(t => t, StringComparer.Ordinal).ToList(),
                Events = x.Events.ToDictionary(kv => kv.Key, kv => Encode(kv.Value), StringComparer.Ordinal),
                Bindings = x.Bindings?.Select(s => new BindingDto
                {
                    Kind = s.SubscriberKind, Id = s.SubscriberId, Procedure = s.ProcedureName,
                    Event = s.EventKey, Identity = s.Identity,
                }).ToList(),
                Observability = x.Observability == null ? null : new ObservabilityDto
                {
                    Observable = x.Observability.ObservableEventKeys.OrderBy(k => k, StringComparer.Ordinal).ToList(),
                    Publishers = new Dictionary<string, bool>(x.Observability.PublisherObjects, StringComparer.Ordinal),
                },
            };
        return dto;
    }

    private static AffectedBaseline FromDto(StoreDto dto)
    {
        if (dto.Keys == null || dto.Modules == null || dto.Bundles == null)
            throw new InvalidDataException("a required section is missing");
        HashSet<string> Decode(int[]? indices)
        {
            if (indices == null) throw new InvalidDataException("a key list is missing");
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var i in indices)
            {
                if (i < 0 || i >= dto.Keys.Count) throw new InvalidDataException($"key index {i} is out of range");
                set.Add(dto.Keys[i] ?? throw new InvalidDataException("a key is null"));
            }
            return set;
        }
        T Required<T>(T? value, string what) where T : class
            => value ?? throw new InvalidDataException($"{what} is missing");

        var modules = new Dictionary<string, AffectedModuleSnapshot>(StringComparer.Ordinal);
        foreach (var (name, m) in dto.Modules)
        {
            Required(m, $"module {name}");
            modules[name] = new AffectedModuleSnapshot(
                Required(m.Manifest, "a manifest fingerprint"),
                Required(m.Refs, "a dependency fingerprint"),
                new Dictionary<string, string>(Required(m.Files, "a file hash table"), StringComparer.Ordinal),
                Required(m.Objects, "an object table").ToDictionary(
                    kv => kv.Key,
                    kv => new AffectedObjectId(
                        Required(Required(kv.Value, "an object").Kind, "an object kind"), kv.Value.Id, kv.Value.Name ?? ""),
                    StringComparer.Ordinal));
        }

        var bundles = new Dictionary<string, AffectedBundleBaseline>(StringComparer.Ordinal);
        foreach (var (bundle, x) in dto.Bundles)
        {
            Required(x, $"bundle {bundle}");
            bundles[bundle] = new AffectedBundleBaseline(
                Required(x.Env, "an environment key"),
                Required(x.Coverage, "coverage").ToDictionary(kv => kv.Key, kv => Decode(kv.Value), StringComparer.Ordinal),
                new HashSet<string>(Required(x.Unknown, "the unknown tests"), StringComparer.Ordinal),
                new HashSet<string>(Required(x.Failing, "the failing tests"), StringComparer.Ordinal),
                Required(x.Events, "events").ToDictionary(kv => kv.Key, kv => Decode(kv.Value), StringComparer.Ordinal),
                x.Bindings?.Select(s => new SubscriberBinding(
                    Required(Required(s, "a binding").Kind, "a binding kind"), s.Id,
                    Required(s.Procedure, "a binding procedure"), Required(s.Event, "a binding event"),
                    Required(s.Identity, "a binding identity"))).ToList(),
                x.Observability == null ? null : new EventObservability(
                    new HashSet<string>(Required(x.Observability.Observable, "observable events"), StringComparer.Ordinal),
                    new Dictionary<string, bool>(Required(x.Observability.Publishers, "publishers"), StringComparer.Ordinal)));
        }
        return new AffectedBaseline(modules, bundles);
    }
}

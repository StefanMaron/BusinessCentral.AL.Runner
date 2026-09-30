// AffectedEnvironmentDrift — #5028: an affectedOnly baseline recorded in another environment (another
// BC build, a changed package closure or package content) is used, narrowed by a per-object diff of
// the two environments' apps, with a warning. Rules: docs/server-mode.md#affectedonly-across-environments.
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AlRunner.Infrastructure;

/// <summary>One resolved app of an environment. <see cref="Objects"/> maps an object key
/// (<c>Kind|id:N</c> or <c>Kind|name:X</c>) to <c>hash:name</c>; null when the package carries
/// neither AL source nor, for a package with no compiled code, symbols to hash per object.</summary>
internal sealed record EnvironmentApp(string ContentHash, string Name, IReadOnlyDictionary<string, string>? Objects);

/// <summary>The apps a bundle resolved, by AppId.</summary>
internal sealed record EnvironmentSnapshot(IReadOnlyDictionary<Guid, EnvironmentApp> Apps);

/// <summary>
/// The environments a bundle's test records were taken in: each test's record names the snapshot
/// of the run that took it, so a later run diffs every record against its own environment. A test
/// with no entry has no known environment (a baseline from before #5028).
/// </summary>
internal sealed class BundleEnvironments
{
    public Dictionary<string, EnvironmentSnapshot> Snapshots { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> ByTest { get; } = new(StringComparer.Ordinal);

    /// <summary>Records <paramref name="test"/> as taken in <paramref name="snapshot"/>.</summary>
    public void Set(string test, EnvironmentSnapshot snapshot)
    {
        var id = AffectedEnvironmentDrift.IdOf(snapshot);
        Snapshots.TryAdd(id, snapshot);
        ByTest[test] = id;
    }

    /// <summary>Drops the snapshots no test record names any more.</summary>
    public void Prune()
    {
        var live = ByTest.Values.ToHashSet(StringComparer.Ordinal);
        foreach (var id in Snapshots.Keys.Where(k => !live.Contains(k)).ToList()) Snapshots.Remove(id);
    }
}

/// <summary>What changed between two environments. <see cref="Approximate"/> lists why the diff is
/// incomplete; empty means every difference is in <see cref="Changed"/>.</summary>
internal sealed record EnvironmentDiff(IReadOnlyList<AffectedObjectId> Changed, IReadOnlyList<string> Approximate);

/// <summary>The selection keys an environment diff selects on, and the changes no key can carry.</summary>
internal sealed record EnvironmentDriftKeys(HashSet<string> CoverageKeys, HashSet<string> EventKeys, IReadOnlyList<string> Unattributed);

/// <summary>A bundle's drift: the warning, each record environment's keys, and which of those diffs were exact.</summary>
internal sealed record EnvironmentDriftResolution(
    EnvironmentDriftInfo Info,
    IReadOnlyDictionary<string, EnvironmentDriftKeys> KeysByRecord,
    IReadOnlySet<string> ExactRecords);

internal static class AffectedEnvironmentDrift
{
    /// <summary>Prefix of a coverage key naming an object outside the request's own sources.</summary>
    internal const string DependencyKeyPrefix = "dep|";

    /// <summary>At most this many changed objects are named in a warning or a response.</summary>
    internal const int NamedObjectLimit = 50;

    private const string IsolationMarker = "|isolation=";
    private const string FileKeyPrefix = "file|";

    // Kinds whose use the recording attributes to a test: an instance built or a scope entered
    // (AlObjectUseTracker), or a record held (AlEventRaiseTracker's table keys). An interface has no
    // code of its own; a change to its signature changes its implementers too.
    private static readonly HashSet<string> BuiltKinds = new(StringComparer.Ordinal) { "Codeunit", "Page", "Report", "Query", "XmlPort" };

    // Kinds declared by name only.
    private static readonly HashSet<string> NamedKinds = new(StringComparer.Ordinal)
    {
        "Interface", "Profile", "ProfileExtension", "PageCustomization", "ControlAddIn", "DotNet", "Entitlement",
    };

    private static readonly Dictionary<string, string> CanonicalKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["codeunit"] = "Codeunit", ["table"] = "Table", ["tableextension"] = "TableExtension",
        ["page"] = "Page", ["pageextension"] = "PageExtension", ["pagecustomization"] = "PageCustomization",
        ["report"] = "Report", ["reportextension"] = "ReportExtension", ["query"] = "Query",
        ["xmlport"] = "XmlPort", ["enum"] = "Enum", ["enumextension"] = "EnumExtension",
        ["interface"] = "Interface", ["permissionset"] = "PermissionSet",
        ["permissionsetextension"] = "PermissionSetExtension", ["profile"] = "Profile",
        ["profileextension"] = "ProfileExtension", ["controladdin"] = "ControlAddIn",
        ["dotnet"] = "DotNet", ["entitlement"] = "Entitlement",
    };

    private static readonly Dictionary<string, string> SymbolArrayKinds = new(StringComparer.Ordinal)
    {
        ["Tables"] = "Table", ["Codeunits"] = "Codeunit", ["Pages"] = "Page", ["Reports"] = "Report",
        ["XmlPorts"] = "XmlPort", ["Queries"] = "Query", ["EnumTypes"] = "Enum", ["Interfaces"] = "Interface",
        ["TableExtensions"] = "TableExtension", ["PageExtensions"] = "PageExtension",
        ["ReportExtensions"] = "ReportExtension", ["EnumExtensionTypes"] = "EnumExtension",
        ["PermissionSets"] = "PermissionSet", ["PermissionSetExtensions"] = "PermissionSetExtension",
        ["ControlAddIns"] = "ControlAddIn", ["Profiles"] = "Profile", ["PageCustomizations"] = "PageCustomization",
        ["DotNetPackages"] = "DotNet",
    };

    // An object declaration at the start of a line: kind, optional id, name.
    private static readonly Regex ObjectHeader = new(
        @"^[ \t]*(codeunit|tableextension|table|pageextension|pagecustomization|page|reportextension|report|query|xmlport|enumextension|enum|interface|permissionsetextension|permissionset|profileextension|profile|controladdin|dotnet|entitlement)[ \t]+(?:(\d+)[ \t]+)?((?>""(?:[^""]|"""")*""|[A-Za-z_][A-Za-z0-9_]*))(?![ \t]*[=,;])",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant);

    // Per bundle: the resolved closure of its last load (RunBundleForServer), read when selection needs it.
    private static readonly ConcurrentDictionary<string, IReadOnlyList<(AppManifest Manifest, string AppPath)>> _closureByBundle = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, EnvironmentApp> _appByContentHash = new(StringComparer.Ordinal);

    /// <summary>A stable id for a snapshot: its apps' content hashes, which are all a diff reads.</summary>
    internal static string IdOf(EnvironmentSnapshot snapshot)
        => ShortHash(string.Join("\n", snapshot.Apps
            .OrderBy(a => a.Key)
            .Select(a => $"{a.Key:D}={a.Value.ContentHash}")));

    /// <summary>Why a change of BC build makes any diff approximate: the platform is not made of AL
    /// objects, so its changes select nothing. Null when the build is the same.</summary>
    internal static string? PlatformReason(string recordedKey, string currentKey)
    {
        var (before, now) = (BuildOf(recordedKey), BuildOf(currentKey));
        return string.Equals(before, now, StringComparison.Ordinal)
            ? null
            : $"the BC platform changed ({before} to {now}) and is not diffed";
    }

    internal static string ObjectKey(string kind, int? id, string name)
        => $"{kind}|{(id.HasValue ? "id:" + id.Value : "name:" + name)}";

    internal static string CanonicalKind(string kind) => CanonicalKinds.TryGetValue(kind, out var k) ? k : kind;

    /// <summary>The coverage key of an object a test used outside the request's own sources, from its
    /// class name (<c>Codeunit80</c>); null when the class names no AL object.</summary>
    internal static string? DependencyKeyOf(Type objectType)
    {
        var (label, id) = AlCallStackCapture.ParseObjectTypeAndIdForTests(objectType.Name);
        return id == 0 ? null : DependencyKeyPrefix + ObjectKey(CanonicalKind(label), id, "");
    }

    /// <summary>Remembers the closure a bundle resolved: packages under <paramref name="excludePath"/> are
    /// request bundles or sibling sources the change model covers, and a non-Microsoft package whose
    /// AppId runs a module compiled elsewhere does not describe the code that runs.</summary>
    internal static void RecordClosure(string bundlePath, IEnumerable<(AppManifest Manifest, string AppPath)> resolved,
        Func<string, bool> excludePath, Func<Guid, string, bool> loadedFromPackage)
    {
        _closureByBundle[bundlePath] = resolved
            .Where(r => r.Manifest.AppId != Guid.Empty && !excludePath(r.AppPath)
                && (string.Equals(r.Manifest.Publisher, "Microsoft", StringComparison.OrdinalIgnoreCase)
                    || loadedFromPackage(r.Manifest.AppId, r.AppPath)))
            .ToList();
    }

    internal static void ForgetClosure(string bundlePath) => _closureByBundle.TryRemove(bundlePath, out _);

    /// <summary>The environment a bundle ran in, or null when its closure was never recorded.</summary>
    internal static EnvironmentSnapshot? CurrentFor(string bundlePath)
        => _closureByBundle.TryGetValue(bundlePath, out var closure) ? Capture(closure) : null;

    internal static EnvironmentSnapshot Capture(IEnumerable<(AppManifest Manifest, string AppPath)> closure)
    {
        var apps = new Dictionary<Guid, EnvironmentApp>();
        foreach (var (manifest, appPath) in closure)
        {
            var hash = RunnerFingerprint.ComputeFileContentHashMemoized(appPath);
            // An unreadable package cannot be compared with anything, so it gets no object table.
            if (string.IsNullOrEmpty(hash) || hash == RunnerFingerprint.UnknownContentHash)
            {
                apps[manifest.AppId] = new EnvironmentApp("unreadable-" + Guid.NewGuid().ToString("N"), manifest.Name, null);
                continue;
            }
            apps[manifest.AppId] = _appByContentHash.GetOrAdd(hash, h => new EnvironmentApp(h, manifest.Name, ReadObjects(appPath)));
        }
        return new EnvironmentSnapshot(apps);
    }

    /// <summary>
    /// Per object, a hash of the source that declares it. A package without source is hashed per
    /// object from its SymbolReference.json only when it carries no compiled code (the platform's
    /// System app): there the symbols are all the package contributes. Otherwise null.
    /// </summary>
    internal static Dictionary<string, string>? ReadObjects(string appPath)
    {
        try
        {
            var sources = AppLoader.ExtractAlWithPaths(appPath);
            if (sources.Count > 0) return ObjectsFromSource(sources);
            if (AppLoader.IsR2R(appPath)) return null;
            var symbols = ReadSymbolReference(appPath);
            return symbols is { } root ? ObjectsFromSymbols(root) : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Every object a file declares gets that file's hash; an object declared in several
    /// files gets all of theirs. A file declaring nothing recognisable is kept under its path, so a
    /// change to it is seen and reported as unattributable.</summary>
    internal static Dictionary<string, string> ObjectsFromSource(IEnumerable<(string Path, string Source)> sources)
    {
        var parts = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (path, source) in sources)
        {
            var fileHash = ShortHash(source.Replace("\r\n", "\n"));
            var declaredAny = false;
            foreach (Match m in ObjectHeader.Matches(source))
            {
                var kind = CanonicalKind(m.Groups[1].Value);
                int? id = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : null;
                // A kind declared with an id that has none here is a reference, not a declaration:
                // a permission entry `table "Sales Header" = X`.
                if (id == null && !NamedKinds.Contains(kind)) continue;
                var name = m.Groups[3].Value.Trim('"').Replace("\"\"", "\"");
                Add(ObjectKey(kind, id, name), fileHash + ":" + name);
                declaredAny = true;
            }
            if (!declaredAny) Add(FileKeyPrefix + path, fileHash + ":" + path);
        }
        return parts.ToDictionary(kv => kv.Key, kv => kv.Value.Count == 1
            ? kv.Value[0]
            : ShortHash(string.Join("\n", kv.Value.OrderBy(v => v, StringComparer.Ordinal))) + ":" + NameOf(kv.Value[0]),
            StringComparer.Ordinal);

        void Add(string key, string value)
        {
            if (!parts.TryGetValue(key, out var list)) parts[key] = list = new List<string>();
            list.Add(value);
        }
    }

    internal static Dictionary<string, string> ObjectsFromSymbols(JsonElement root)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        Walk(root);
        return result;

        void Walk(JsonElement scope)
        {
            if (scope.ValueKind != JsonValueKind.Object) return;
            foreach (var property in scope.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Array) continue;
                if (property.NameEquals("Namespaces"))
                {
                    foreach (var ns in property.Value.EnumerateArray()) Walk(ns);
                    continue;
                }
                if (!SymbolArrayKinds.TryGetValue(property.Name, out var kind)) continue;
                foreach (var o in property.Value.EnumerateArray())
                {
                    if (o.ValueKind != JsonValueKind.Object) continue;
                    int? id = o.TryGetProperty("Id", out var idEl) && idEl.TryGetInt32(out var n) ? n : null;
                    var name = o.TryGetProperty("Name", out var nameEl) ? nameEl.GetString() ?? "" : "";
                    result[ObjectKey(kind, id, name)] = ShortHash(o.GetRawText()) + ":" + name;
                }
            }
        }
    }

    private static JsonElement? ReadSymbolReference(string appPath)
    {
        using var zip = AppLoader.OpenAppZip(appPath);
        var entry = zip.Entries.FirstOrDefault(e => e.FullName.Equals("SymbolReference.json", StringComparison.OrdinalIgnoreCase));
        if (entry == null) return null;
        using var s = entry.Open();
        using var doc = JsonDocument.Parse(s, new JsonDocumentOptions { AllowTrailingCommas = true });
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// The objects that differ. An app whose package bytes are equal on both sides contributes
    /// nothing; otherwise every object whose hash differs, or is missing on either side, is changed.
    /// An app whose objects cannot be read on a side it is present on makes the diff approximate,
    /// and so does a changed file that declares no object.
    /// </summary>
    internal static EnvironmentDiff Diff(EnvironmentSnapshot? recorded, EnvironmentSnapshot? current)
    {
        var changed = new List<AffectedObjectId>();
        var approximate = new List<string>();
        if (recorded == null)
            approximate.Add("the baseline holds no per-object record of the environment it was recorded in");
        if (current == null)
            approximate.Add("this run's dependency closure could not be read");
        if (recorded == null || current == null) return new EnvironmentDiff(changed, approximate);

        foreach (var appId in recorded.Apps.Keys.Union(current.Apps.Keys).OrderBy(g => g))
        {
            recorded.Apps.TryGetValue(appId, out var before);
            current.Apps.TryGetValue(appId, out var now);
            if (before != null && now != null && string.Equals(before.ContentHash, now.ContentHash, StringComparison.Ordinal))
                continue;
            if ((before != null && before.Objects == null) || (now != null && now.Objects == null))
                approximate.Add($"{(now ?? before)!.Name} changed and its objects cannot be read (no AL source in the package)");
            var old = before?.Objects ?? new Dictionary<string, string>();
            var neu = now?.Objects ?? new Dictionary<string, string>();
            foreach (var key in old.Keys.Union(neu.Keys).OrderBy(k => k, StringComparer.Ordinal))
            {
                var had = old.TryGetValue(key, out var a);
                var has = neu.TryGetValue(key, out var b);
                if (had && has && string.Equals(a, b, StringComparison.Ordinal)) continue;
                if (key.StartsWith(FileKeyPrefix, StringComparison.Ordinal))
                {
                    approximate.Add($"{(now ?? before)!.Name}: {key[FileKeyPrefix.Length..]} changed and declares no object");
                    continue;
                }
                changed.Add(ParseObjectKey(key, NameOf(b ?? a!)));
            }
        }
        return new EnvironmentDiff(changed.Distinct().ToList(), approximate);
    }

    /// <summary>
    /// The keys the changed objects select on: a built kind selects the tests that built it or
    /// entered it, a table or tableextension the tests that held its records. What no key can carry
    /// is returned as a reason instead: another kind, an instance built outside any one test, or a
    /// record held outside one.
    /// </summary>
    internal static EnvironmentDriftKeys SelectionKeys(IReadOnlyList<AffectedObjectId> changed,
        IReadOnlyDictionary<int, List<int>> currentExtensionBases, HashSet<string>? recordedBundleWide)
    {
        var coverage = new HashSet<string>(StringComparer.Ordinal);
        var events = new HashSet<string>(StringComparer.Ordinal);
        var unattributed = new List<string>();
        foreach (var o in changed)
        {
            if (o.Kind == "Interface") continue;
            if (BuiltKinds.Contains(o.Kind) && o.Id.HasValue)
            {
                var key = DependencyKeyPrefix + ObjectKey(o.Kind, o.Id, "");
                coverage.Add(key);
                if (recordedBundleWide?.Contains(AffectedEventSelection.LongLivedObjectKey(key)) ?? false)
                    unattributed.Add($"an instance of {Display(o)} was built outside any one test");
                continue;
            }
            if (o.Kind is "Table" or "TableExtension")
            {
                var r = AffectedEventSelection.ChangedTableKeys(new[] { (o.Kind, o.Id) }, currentExtensionBases, recordedBundleWide);
                events.UnionWith(r.Keys);
                if (r.ForceFullReason != null) unattributed.Add(r.ForceFullReason);
                continue;
            }
            unattributed.Add($"{Display(o)} changed, and no test recording holds the use of this kind of object ({o.Kind})");
        }
        return new EnvironmentDriftKeys(coverage, events, unattributed);
    }

    /// <summary>
    /// A bundle's drift: each test record's environment (<paramref name="recordEnvs"/>, "" for a
    /// record with none) is diffed against <paramref name="current"/> on its own, so the keys and the
    /// exactness are per record environment. A change of BC build makes every one approximate,
    /// because the platform is not diffed.
    /// </summary>
    internal static EnvironmentDriftResolution Resolve(string recordedKey, string currentKey,
        IEnumerable<string> recordEnvs, BundleEnvironments? recorded, EnvironmentSnapshot? current,
        IReadOnlyDictionary<int, List<int>> currentExtensionBases, HashSet<string>? recordedBundleWide)
    {
        var keysByRecord = new Dictionary<string, EnvironmentDriftKeys>(StringComparer.Ordinal);
        var exact = new HashSet<string>(StringComparer.Ordinal);
        var changed = new List<AffectedObjectId>();
        var why = new List<string>();
        var platform = PlatformReason(recordedKey, currentKey);
        if (platform != null) why.Add(platform);
        foreach (var recordEnv in recordEnvs.Distinct(StringComparer.Ordinal).OrderBy(e => e, StringComparer.Ordinal))
        {
            var snapshot = recordEnv.Length > 0 && recorded != null && recorded.Snapshots.TryGetValue(recordEnv, out var s) ? s : null;
            var diff = Diff(snapshot, current);
            var keys = SelectionKeys(diff.Changed, currentExtensionBases, recordedBundleWide);
            keysByRecord[recordEnv] = keys;
            if (platform == null && diff.Approximate.Count == 0 && keys.Unattributed.Count == 0) exact.Add(recordEnv);
            changed.AddRange(diff.Changed);
            why.AddRange(diff.Approximate);
            why.AddRange(keys.Unattributed);
        }
        changed = changed.Distinct().ToList();
        why = why.Distinct(StringComparer.Ordinal).ToList();
        var info = new EnvironmentDriftInfo(
            BuildOf(recordedKey),
            BuildOf(currentKey),
            changed.Count,
            why.Count == 0 ? EnvironmentDriftInfo.Diffed : EnvironmentDriftInfo.Approximate,
            changed.Select(Display).Take(NamedObjectLimit).ToList(),
            why.Count == 0 ? null : string.Join("; ", why.Take(5)) + (why.Count > 5 ? $"; and {why.Count - 5} more" : ""));
        return new EnvironmentDriftResolution(info, keysByRecord, exact);
    }

    /// <summary>The environment key without its test-isolation part (AffectedIsolationWidening.EnvironmentKey).</summary>
    internal static string WithoutIsolation(string environmentKey)
    {
        var at = environmentKey.LastIndexOf(IsolationMarker, StringComparison.Ordinal);
        return at < 0 ? environmentKey : environmentKey[..at];
    }

    internal static string IsolationOf(string environmentKey)
    {
        var at = environmentKey.LastIndexOf(IsolationMarker, StringComparison.Ordinal);
        return at < 0 ? "" : environmentKey[(at + IsolationMarker.Length)..];
    }

    /// <summary>The BC build an environment key names (its first segment).</summary>
    internal static string BuildOf(string environmentKey)
    {
        var bar = environmentKey.IndexOf('|');
        var build = bar < 0 ? environmentKey : environmentKey[..bar];
        return build.Length == 0 ? "unknown" : build;
    }

    internal static string Display(AffectedObjectId o)
        => o.Id.HasValue ? $"{o.Kind} {o.Id.Value}{(o.Name.Length > 0 ? " " + o.Name : "")}" : $"{o.Kind} {o.Name}";

    /// <summary>The warning a drifted selection prints, naming both builds and the changed objects.</summary>
    internal static string Warning(EnvironmentDriftInfo d)
    {
        var sb = new StringBuilder();
        sb.Append("WARNING: the affectedOnly baseline was recorded in another environment (BC ")
          .Append(d.Recorded).Append(", now BC ").Append(d.Current).Append("); ")
          .Append(d.ChangedObjects).Append(" object(s) differ");
        if (d.Objects.Count > 0)
            sb.Append(": ").Append(string.Join(", ", d.Objects))
              .Append(d.ChangedObjects > d.Objects.Count ? $", and {d.ChangedObjects - d.Objects.Count} more" : "");
        sb.Append(d.Mode == EnvironmentDriftInfo.Diffed
            ? ". Selection is narrowed by that diff."
            : $". Selection is APPROXIMATE: {d.Reason}.");
        sb.Append(" Set strictEnvironment (--strict-environment) to run everything instead.");
        return sb.ToString();
    }

    /// <summary>One request's drift from its bundles': approximate when any bundle's is. Bundles
    /// resolve largely the same apps, so the count is the largest bundle's, or the distinct named
    /// objects when those are more.</summary>
    internal static EnvironmentDriftInfo? Combine(IReadOnlyList<EnvironmentDriftInfo> perBundle)
    {
        if (perBundle.Count == 0) return null;
        if (perBundle.Count == 1) return perBundle[0];
        var approximate = perBundle.Where(d => d.Mode == EnvironmentDriftInfo.Approximate).ToList();
        var objects = perBundle.SelectMany(d => d.Objects).Distinct(StringComparer.Ordinal).ToList();
        return new EnvironmentDriftInfo(
            perBundle[0].Recorded,
            perBundle[0].Current,
            Math.Max(perBundle.Max(d => d.ChangedObjects), objects.Count),
            approximate.Count > 0 ? EnvironmentDriftInfo.Approximate : EnvironmentDriftInfo.Diffed,
            objects.Take(NamedObjectLimit).ToList(),
            approximate.Count > 0 ? string.Join("; ", approximate.Select(d => d.Reason).Distinct(StringComparer.Ordinal)) : null);
    }

    private static AffectedObjectId ParseObjectKey(string key, string name)
    {
        var bar = key.IndexOf('|');
        var kind = key[..bar];
        var rest = key[(bar + 1)..];
        return rest.StartsWith("id:", StringComparison.Ordinal) && int.TryParse(rest[3..], out var id)
            ? new AffectedObjectId(kind, id, name)
            : new AffectedObjectId(kind, null, rest.StartsWith("name:", StringComparison.Ordinal) ? rest[5..] : rest);
    }

    private static string NameOf(string value)
    {
        var colon = value.IndexOf(':');
        return colon < 0 ? "" : value[(colon + 1)..];
    }

    private static string ShortHash(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();

    /// <summary>Test-only: forget the recorded closures and the per-package object tables.</summary>
    internal static void ResetForTests()
    {
        _closureByBundle.Clear();
        _appByContentHash.Clear();
    }
}

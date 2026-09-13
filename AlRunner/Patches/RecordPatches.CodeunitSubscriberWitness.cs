// RecordPatches.CodeunitSubscriberWitness — which codeunits of a precompiled dependency carry an
// AL event subscriber, read off the assembly the run already loaded (#3788).
//
// WHAT THIS IS FOR
//   BC's ObjectMetadataEmitter writes a codeunit's ATTRIBUTED methods. SymbolReference.json
//   states the publishers exactly — by id, by name and in BC's document order — and states the
//   SUBSCRIBERS not at all, because the symbol file is an app's consumer-facing API surface and
//   an AL event subscriber is always `local`. So a <Methods> subtree built from the symbol file
//   alone is exact for a codeunit with no subscriber and silently SHORT for one with them, and
//   short is worse than absent: MetadataObjectDiff pairs Methods positionally, so a missing
//   element puts every later one in a different method's slot (loud-failures.md).
//
//   This registry answers the one question that decides between the two: does codeunit N of app
//   A declare a subscriber? The answer comes from the assembly's own ECMA-335 metadata, which is
//   where the subscriber attribute lives.
//
// WHY A WITNESS AND NOT A DATA SOURCE
//   The assembly could supply the subscriber methods themselves — it carries their names, and
//   MethodIdAttribute reproduces BC's <Method ID> for 140 of 140 subscribers on System
//   Application 28.1.49838.53910. What it cannot supply is BC's ORDER. BC's document order is
//   the AL SOURCE declaration order (70/70 on the codeunits with two or more emitted methods),
//   and the metadata table's order is alphabetical. Every ordering hypothesis tried against
//   BC's documents — alphabetical, metadata-table, MethodId ascending, kind-then-table — scored
//   at best 22 of 70. Recovering source order means parsing the AL shipped in the .app, which is
//   the parser treadmill #3491 describes.
//
//   So the assembly is used for the question it answers exactly and cheaply, and the symbol file
//   supplies the data for the codeunits it answers completely. Measured with that policy over
//   three BC builds (27.5.46862.53931, 28.1.49838.53910, 28.4.53241.54407 — two distinct
//   binaries across the 27.x/28.x boundary): 66 codeunits emitted on every build, all exact,
//   ZERO fabricated slots, and the honest-absence count falls from 326 to 174 on 28.1.
//
// COST, AND WHY NO NEW LOAD HAPPENS
//   Nothing is loaded here. DependencyLoader.LoadAll runs BEFORE the AddBcAppPath loop at both
//   Program.cs call sites, so every registered .app whose assemblies loaded at all has them in
//   the AppDomain by the time any metadata path can run; RegisterAppAssemblies is the one place
//   that holds (assemblies, appPath) together and is where the scan is driven from. The scan
//   itself reads the already-mapped metadata through AssemblyTypeIndex, the same instrument
//   EventSubscriberPatches drives over every dependency on every run: 34-35 ms for System
//   Application's 17 MiB assembly and its 533 Codeunit types, once per assembly per process.
//
// THE THIRD STATE IS THE POINT (guards-need-a-third-state.md)
//   Yes / no / UNKNOWN, and unknown is deliberately not spelled as no. An app whose assembly
//   never loaded — a platform symbol-only app, a load that failed into service-tier DLL dispatch
//   — has measured nothing about its subscribers, and reading that silence as "no subscribers"
//   restores exactly the fabrications this design removes. Unknown abstains, which is the
//   behaviour before this change: an honest one-directional absence.

using System.Collections.Concurrent;
using System.Reflection;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    /// <summary>
    /// What one app's loaded assemblies witnessed: the codeunit ids that declare a
    /// <c>[NavEventSubscriber]</c> method, and the ids the scan actually SAW — which is not the
    /// same set as "every codeunit the symbol file declares".
    ///
    /// <para><paramref name="ScannedCodeunitIds"/> is what makes the third state expressible. A
    /// codeunit absent from it was not measured — its type may live in a chunk that did not
    /// load, or in no assembly at all — so the right answer for it is "unknown", not "carries no
    /// subscriber". Without this set an unmeasured codeunit and a cleared one are
    /// indistinguishable, which is the collapse that produces a confidently wrong subtree.</para>
    /// </summary>
    private sealed record CodeunitSubscriberWitness(
        IReadOnlySet<int> SubscriberCodeunitIds, IReadOnlySet<int> ScannedCodeunitIds);

    private static readonly ConcurrentDictionary<string, CodeunitSubscriberWitness>
        _codeunitSubscriberWitness = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Record what an app's loaded assemblies say about its codeunits' event subscribers.
    /// Called from <c>DependencyLoader.RegisterAppAssemblies</c>, which is the one place that
    /// holds the assemblies and the .app path together.
    ///
    /// <para>Merges rather than replaces, because Microsoft ships Base Application as five R2R
    /// chunks and one app's codeunits are spread across all of them — a later chunk's scan must
    /// widen the witnessed set, never narrow it (#3054 makes the same point for the module
    /// registration this rides alongside).</para>
    /// </summary>
    internal static void RegisterCodeunitSubscriberWitness(
        string appPath, IReadOnlyCollection<int> subscriberCodeunitIds,
        IReadOnlyCollection<int> scannedCodeunitIds)
    {
        if (string.IsNullOrEmpty(appPath)) return;
        var key = Path.GetFullPath(appPath);
        _codeunitSubscriberWitness.AddOrUpdate(
            key,
            _ => new CodeunitSubscriberWitness(
                subscriberCodeunitIds.ToHashSet(), scannedCodeunitIds.ToHashSet()),
            (_, existing) =>
            {
                var subscribers = existing.SubscriberCodeunitIds.ToHashSet();
                subscribers.UnionWith(subscriberCodeunitIds);
                var scanned = existing.ScannedCodeunitIds.ToHashSet();
                scanned.UnionWith(scannedCodeunitIds);
                return new CodeunitSubscriberWitness(subscribers, scanned);
            });
    }

    /// <summary>
    /// Scan one app's loaded assemblies for the codeunits that declare a
    /// <c>[NavEventSubscriber]</c> method, and register the result.
    ///
    /// <para>Reads the assemblies' own ECMA-335 metadata through
    /// <see cref="AlRunner.Infrastructure.AssemblyTypeIndex"/> — the same instrument
    /// <c>EventSubscriberPatches</c> already drives over every dependency, so no RuntimeType is
    /// materialised for the thousands of Record/Table/Page types an emitted assembly carries.
    /// An assembly whose scan throws contributes nothing rather than being recorded as clear:
    /// that is the unknown state, and it is the honest one.</para>
    /// </summary>
    internal static void WitnessCodeunitSubscribers(
        IReadOnlyList<Assembly> assemblies, string appPath)
    {
        if (assemblies.Count == 0 || string.IsNullOrEmpty(appPath)) return;

        var subscribers = new HashSet<int>();
        var scanned = new HashSet<int>();
        foreach (var asm in assemblies)
        {
            List<string> codeunitTypeNames;
            List<MethodInfo> subscriberMethods;
            try
            {
                var index = AlRunner.Infrastructure.AssemblyTypeIndex.For(asm);
                // Not metadata-backed (a dynamic assembly) means the TypeDef table cannot be
                // read at all, so this assembly witnesses nothing. Skipping it leaves its
                // codeunits UNSCANNED — the unknown state — rather than clear.
                if (!index.IsMetadataBacked) continue;
                codeunitTypeNames = index.TypeNamesWithPrefix("Codeunit").ToList();
                subscriberMethods = index.FindAttributedMethods("Codeunit", "NavEventSubscriberAttribute");
            }
            catch (Exception ex)
            {
                // Loud, and NOT recorded as a clear scan. A swallowed failure here would be read
                // downstream as "this app's codeunits carry no subscribers", which is the silent
                // wrong answer the whole design avoids (loud-failures.md).
                Console.Error.WriteLine(
                    $"[warn] RecordPatches: could not scan {asm.GetName().Name} for event subscribers, so the "
                    + "codeunits it declares stay UNWITNESSED and their method tables stay absent rather than "
                    + $"being derived from an incomplete view: {ex.GetType().Name}: {ex.Message}");
                continue;
            }

            foreach (var typeName in codeunitTypeNames)
                if (TryParseCodeunitTypeId(typeName, out var scannedId)) scanned.Add(scannedId);

            foreach (var method in subscriberMethods)
            {
                var declaring = method.DeclaringType?.Name;
                if (declaring != null && TryParseCodeunitTypeId(declaring, out var id)) subscribers.Add(id);
            }
        }

        if (scanned.Count == 0) return;   // nothing measured — leave the app unwitnessed
        RegisterCodeunitSubscriberWitness(appPath, subscribers, scanned);
    }

    /// <summary>The AL object id in a <c>Codeunit&lt;N&gt;</c> emitted type name, or false for a
    /// type whose name merely starts with "Codeunit" without an id following.</summary>
    private static bool TryParseCodeunitTypeId(string typeName, out int id)
    {
        id = 0;
        const string prefix = "Codeunit";
        if (!typeName.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var rest = typeName.AsSpan(prefix.Length);
        return rest.Length > 0 && int.TryParse(rest, out id);
    }

    /// <summary>
    /// Whether the loaded assemblies PROVE codeunit <paramref name="codeunitId"/> of
    /// <paramref name="appPath"/> declares no event subscriber — so the symbol file's view of
    /// its emitted methods is complete.
    ///
    /// <para>False for all three of: the codeunit declares one, no witness exists for this app,
    /// and the scan never saw this codeunit. The last two are the UNKNOWN state and they answer
    /// false on purpose: the caller's next step is to emit a method table, and doing that on an
    /// unmeasured codeunit is how a fabricated association gets made.</para>
    /// </summary>
    internal static bool AssemblyProvesNoSubscriber(string appPath, int codeunitId)
        => !string.IsNullOrEmpty(appPath)
           && _codeunitSubscriberWitness.TryGetValue(Path.GetFullPath(appPath), out var witness)
           && witness.ScannedCodeunitIds.Contains(codeunitId)
           && !witness.SubscriberCodeunitIds.Contains(codeunitId);

    /// <summary>Test seam: forget every witness, so a test can drive the unknown state without
    /// depending on what an earlier test in the same process registered.</summary>
    internal static void ClearCodeunitSubscriberWitnessForTests() => _codeunitSubscriberWitness.Clear();
}

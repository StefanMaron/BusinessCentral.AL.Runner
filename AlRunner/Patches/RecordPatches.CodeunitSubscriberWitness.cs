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
// TWO ROUTES TO THE SAME ANSWER, AND WHY BOTH ARE NEEDED
//   Nothing is LOADED by either. They read the same bytes' ECMA-335 metadata by two paths:
//
//     registration  DependencyLoader.RegisterAppAssemblies, which is the one place holding
//                   (assemblies, appPath) together. LoadAll runs BEFORE the AddBcAppPath loop at
//                   both Program.cs call sites, so in a real run the assemblies are already in
//                   the AppDomain and the scan reads their already-mapped metadata through
//                   AssemblyTypeIndex — the same instrument EventSubscriberPatches drives over
//                   every dependency on every run. 34-35 ms for System Application's 17 MiB
//                   assembly and its 533 Codeunit types, once per assembly per process.
//
//     on demand     EnsureCodeunitSubscriberWitness, from the R2R chunks the .app package itself
//                   carries, via AppLoader.ExtractAllDllPaths (content-addressed, cached on
//                   disk) and PEReader.
//
//   The second is not redundant. A caller can register a .app WITHOUT loading it — the
//   metadata-equivalence harness does exactly that — and with only the first route the
//   derivation never fires there, so the harness would measure a feature that does not run and
//   report green (verify-execution-not-the-tick.md). Measured, and this is why the fallback
//   exists rather than as a precaution: registration-only gave 0 emitted subtrees and 326
//   absences over the harness's 558 codeunits; with the fallback, 66 codeunits / 152 methods /
//   174 absences, matching the model exactly.
//
// THE THIRD STATE IS THE POINT (guards-need-a-third-state.md)
//   Yes / no / UNKNOWN, and unknown is deliberately not spelled as no. An app whose assembly
//   never loaded — a platform symbol-only app, a load that failed into service-tier DLL dispatch
//   — has measured nothing about its subscribers, and reading that silence as "no subscribers"
//   restores exactly the fabrications this design removes. Unknown abstains, which is the
//   behaviour before this change: an honest one-directional absence.

using System.Collections.Concurrent;
using System.Reflection;
// GetMetadataReader() is an extension method on PEReader declared here, not an instance member.
using System.Reflection.Metadata;

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
    /// Whether the app's own code PROVES codeunit <paramref name="codeunitId"/> of
    /// <paramref name="appPath"/> declares no event subscriber — so the symbol file's view of
    /// its emitted methods is complete.
    ///
    /// <para>False for all three of: the codeunit declares one, no witness could be established
    /// for this app, and the scan never saw this codeunit. The last two are the UNKNOWN state
    /// and they answer false on purpose: the caller's next step is to emit a method table, and
    /// doing that on an unmeasured codeunit is how a fabricated association gets made.</para>
    /// </summary>
    internal static bool AssemblyProvesNoSubscriber(string appPath, int codeunitId)
    {
        var witness = EnsureCodeunitSubscriberWitness(appPath);
        return witness is not null
               && witness.ScannedCodeunitIds.Contains(codeunitId)
               && !witness.SubscriberCodeunitIds.Contains(codeunitId);
    }

    /// <summary>
    /// The witness for one app: the one the loader registered, or — when nothing registered one
    /// — derived on demand from the R2R assemblies the <c>.app</c> package itself carries.
    ///
    /// <para><b>Why the package and not only the loaded assembly.</b> The loader's registration
    /// covers a real run, where <c>LoadAll</c> has run before <c>AddBcAppPath</c>. It does not
    /// cover a caller that registers a <c>.app</c> without loading it — the metadata-equivalence
    /// harness does exactly that — and without this fallback the derivation would be measured on
    /// a path where it never fires, which is a green measurement of nothing
    /// (verify-execution-not-the-tick.md). Measured: the harness reported 0 emitted subtrees and
    /// 326 absences with the registration-only witness, and 66 / 152 / 174 with this one.</para>
    ///
    /// <para>Reads through <see cref="AppLoader.ExtractAllDllPaths"/>, which is content-addressed
    /// and cached on disk, and then through <c>PEReader</c> — the package's ECMA-335 metadata,
    /// never a load. Both routes answer the same question about the same bytes, so a run where
    /// both are available cannot get two answers.</para>
    /// </summary>
    private static CodeunitSubscriberWitness? EnsureCodeunitSubscriberWitness(string appPath)
    {
        if (string.IsNullOrEmpty(appPath)) return null;
        string key;
        try { key = Path.GetFullPath(appPath); }
        catch { return null; }

        if (_codeunitSubscriberWitness.TryGetValue(key, out var existing)) return existing;
        if (!File.Exists(key)) return null;

        var subscribers = new HashSet<int>();
        var scanned = new HashSet<int>();
        try
        {
            foreach (var dllPath in AppLoader.ExtractAllDllPaths(key))
                ScanPackageAssembly(dllPath, subscribers, scanned);
        }
        catch (Exception ex)
        {
            // Loud, and NOT cached as a clear scan: an app whose code could not be read has
            // measured nothing, and recording that as "no subscribers" is the silent wrong
            // answer this design exists to avoid (loud-failures.md).
            Console.Error.WriteLine(
                $"[warn] RecordPatches: could not read the R2R assemblies of '{Path.GetFileName(key)}' to "
                + "decide which of its codeunits declare event subscribers, so their method tables stay "
                + $"absent rather than being derived from an incomplete view: {ex.GetType().Name}: {ex.Message}");
            return null;
        }

        // A package carrying no readable Codeunit type witnessed nothing — a source-only app, or
        // one whose chunks did not extract. Not cached, so a later run that CAN read it is not
        // held to this answer.
        if (scanned.Count == 0) return null;

        var witness = new CodeunitSubscriberWitness(subscribers, scanned);
        return _codeunitSubscriberWitness.GetOrAdd(key, witness);
    }

    /// <summary>
    /// One R2R chunk's <c>Codeunit&lt;N&gt;</c> types and which of them carry a
    /// <c>[NavEventSubscriber]</c> method, read straight out of the TypeDef / CustomAttribute
    /// tables. No <c>Type</c> is resolved and nothing is loaded.
    /// </summary>
    private static void ScanPackageAssembly(string dllPath, HashSet<int> subscribers, HashSet<int> scanned)
    {
        using var stream = File.OpenRead(dllPath);
        using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
        if (!pe.HasMetadata) return;
        var md = pe.GetMetadataReader();

        foreach (var handle in md.TypeDefinitions)
        {
            var type = md.GetTypeDefinition(handle);
            if (!TryParseCodeunitTypeId(md.GetString(type.Name), out var id)) continue;
            scanned.Add(id);
            if (subscribers.Contains(id)) continue;

            foreach (var methodHandle in type.GetMethods())
            {
                var method = md.GetMethodDefinition(methodHandle);
                var found = false;
                foreach (var attributeHandle in method.GetCustomAttributes())
                {
                    if (AttributeTypeName(md, md.GetCustomAttribute(attributeHandle))
                        != "NavEventSubscriberAttribute") continue;
                    found = true;
                    break;
                }
                if (!found) continue;
                subscribers.Add(id);
                break;
            }
        }
    }

    /// <summary>The simple name of a custom attribute's type, or null when the constructor
    /// handle is neither a MemberReference nor a MethodDefinition — the two forms a compiler
    /// emits, and the pair <c>AssemblyTypeIndex.FindAttributedMethods</c> already handles.</summary>
    private static string? AttributeTypeName(
        System.Reflection.Metadata.MetadataReader md,
        System.Reflection.Metadata.CustomAttribute attribute)
    {
        switch (attribute.Constructor.Kind)
        {
            case System.Reflection.Metadata.HandleKind.MemberReference:
            {
                var member = md.GetMemberReference(
                    (System.Reflection.Metadata.MemberReferenceHandle)attribute.Constructor);
                return member.Parent.Kind switch
                {
                    System.Reflection.Metadata.HandleKind.TypeReference => md.GetString(
                        md.GetTypeReference(
                            (System.Reflection.Metadata.TypeReferenceHandle)member.Parent).Name),
                    System.Reflection.Metadata.HandleKind.TypeDefinition => md.GetString(
                        md.GetTypeDefinition(
                            (System.Reflection.Metadata.TypeDefinitionHandle)member.Parent).Name),
                    _ => null,
                };
            }
            case System.Reflection.Metadata.HandleKind.MethodDefinition:
            {
                var ctor = md.GetMethodDefinition(
                    (System.Reflection.Metadata.MethodDefinitionHandle)attribute.Constructor);
                return md.GetString(md.GetTypeDefinition(ctor.GetDeclaringType()).Name);
            }
            default:
                return null;
        }
    }

    /// <summary>Test seam: forget every witness, so a test can drive the unknown state without
    /// depending on what an earlier test in the same process registered.</summary>
    internal static void ClearCodeunitSubscriberWitnessForTests() => _codeunitSubscriberWitness.Clear();
}

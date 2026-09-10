// metadata-ground-truth — compile a Microsoft app with BC's OWN compiler and keep what BC's
// ObjectMetadataEmitter produced, as the ground truth the equivalence harness measures the
// runner's SymbolReference derivation against (issue #3533).
//
// Ground truth is BC's emitter, not a fixture. Nothing here writes an expected value, edits
// one, or defaults one: every byte in the bundle came out of `Compilation.Emit` on Microsoft's
// own source.
//
// Cost is why this is a separate tool rather than something a test does: Business Foundation
// is ~2s, System Application ~14s, Base Application ~116s and ~7GB. Per BC version that is
// affordable; per pull request it is not. So this runs once per (app, BC build), writes a
// bundle, and the harness reads the bundle.
//
// See docs/metadata-equivalence.md.

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using NavCA = Microsoft.Dynamics.Nav.CodeAnalysis;
using NavEmit = Microsoft.Dynamics.Nav.CodeAnalysis.Emit;

namespace AlRunner.Tools.MetadataGroundTruth;

internal static class Program
{
    /// <summary>
    /// Bumped whenever the bundle's shape changes. The harness refuses a bundle written by a
    /// different version rather than reading fields that may have moved.
    /// </summary>
    internal const int BundleSchema = 1;

    private static int Main(string[] args)
    {
        string? appPath = null, outDir = null, artifacts = null;
        bool declOnly = false;
        var dotnetFirst = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--app": appPath = args[++i]; break;
                case "--out": outDir = args[++i]; break;
                case "--artifacts": artifacts = args[++i]; break;
                case "--decl-only": declOnly = true; break;
                case "--dotnet-first": dotnetFirst.Add(args[++i]); break;
                case "-h" or "--help": Usage(); return 0;
                default: Console.Error.WriteLine($"unknown argument '{args[i]}'"); Usage(); return 2;
            }
        }
        if (appPath is null || outDir is null) { Usage(); return 2; }

        artifacts ??= Environment.GetEnvironmentVariable("AL_RUNNER_SERVICE_TIER");
        if (string.IsNullOrEmpty(artifacts))
        {
            Console.Error.WriteLine("--artifacts (or AL_RUNNER_SERVICE_TIER) must name the BC service-tier directory.");
            return 2;
        }

        BcHost.Install(artifacts);
        try { return Generate(appPath, outDir, artifacts, declOnly, dotnetFirst); }
        catch (Exception ex)
        {
            Console.Error.WriteLine("metadata-ground-truth FAILED: " + ex);
            return 1;
        }
    }

    private static void Usage() => Console.Error.WriteLine("""
        metadata-ground-truth --app <path-to.app> --out <bundle-dir> [--artifacts <service-tier-dir>]

          Compiles the app's shipped AL source with BC's own compiler and writes every
          metadata document the emitter produced into <bundle-dir>, with a manifest.

          --decl-only   stop after declaration diagnostics; reports whether the app WOULD
                        compile without paying for the emit.
        """);

    private static int Generate(string appPath, string outDir, string artifacts, bool declOnly,
        IReadOnlyList<string> dotnetFirst)
    {
        var swTotal = Stopwatch.StartNew();
        var identity = NavxPackage.ReadIdentity(appPath);
        Console.WriteLine($"[app] {identity.Publisher}_{identity.Name} {identity.Version} " +
                          $"id={identity.Id} target={identity.Target} platform={identity.Platform}");

        var work = Directory.CreateTempSubdirectory("al-runner-groundtruth-");
        try
        {
            NavxPackage.ExtractTo(appPath, work.FullName);
            var srcRoot = Path.Combine(work.FullName, "src");
            if (!Directory.Exists(srcRoot))
                throw new InvalidOperationException(
                    $"{Path.GetFileName(appPath)} ships no src/ — its ResourceExposurePolicy " +
                    "excludes source, so BC's emitter cannot be run over it here. A runtime " +
                    "package is the other route to this app's metadata (issue #3537).");

            var comp = AppCompilation.Create(identity, work.FullName, srcRoot, artifacts, appPath, dotnetFirst);

            var sw = Stopwatch.StartNew();
            var declErrors = comp.GetDeclarationDiagnostics()
                .Where(d => d.Severity == NavCA.Diagnostics.DiagnosticSeverity.Error).ToList();
            Console.WriteLine($"[decl] {sw.ElapsedMilliseconds}ms  errors={declErrors.Count}");
            foreach (var g in declErrors.GroupBy(d => d.Id).OrderByDescending(g => g.Count()).Take(10))
                Console.WriteLine($"   [{g.Key}] x{g.Count()} :: {g.First().GetMessage()} @ {g.First().Location}");
            if (declOnly) return declErrors.Count == 0 ? 0 : 1;

            var capture = new CaptureOutputter();
            sw.Restart();
            var result = comp.Emit(NavCA.EmitOptions.Default, capture);
            var emitMs = sw.ElapsedMilliseconds;
            var emitErrors = result.Diagnostics
                .Where(d => d.Severity == NavCA.Diagnostics.DiagnosticSeverity.Error).ToList();
            Console.WriteLine($"[emit] {emitMs}ms success={result.Success} " +
                              $"objects={capture.Items.Count} errors={emitErrors.Count}");
            foreach (var g in emitErrors.GroupBy(d => d.Id).OrderByDescending(g => g.Count()).Take(10))
                Console.WriteLine($"   [{g.Key}] x{g.Count()} :: {g.First().GetMessage()} @ {g.First().Location}");

            // Loud, not smaller. An app that emits nothing is the #3549 shape — Base
            // Application emitting ZERO objects because one .NET reference could not be
            // resolved — and writing an empty bundle would turn that into a harness that
            // silently measures nothing while still reporting a bundle.
            if (capture.Items.Count == 0)
                throw new InvalidOperationException(
                    "the emitter produced NO objects. That is a compile failure, not an empty " +
                    "app; writing a bundle here would leave the harness measuring nothing. " +
                    "See the diagnostics above and issue #3549.");

            return Write(outDir, appPath, identity, artifacts, capture, result.Success,
                emitErrors.Count, emitMs, swTotal);
        }
        finally
        {
            try { work.Delete(recursive: true); } catch { /* temp dir cleanup is best effort */ }
        }
    }

    private static int Write(
        string outDir, string appPath, AppIdentity identity, string artifacts,
        CaptureOutputter capture, bool emitSuccess, int emitErrors, long emitMs, Stopwatch swTotal)
    {
        var metaDir = Path.Combine(outDir, "metadata");
        if (Directory.Exists(metaDir)) Directory.Delete(metaDir, recursive: true);
        Directory.CreateDirectory(metaDir);

        var objects = new List<BundleObject>();
        var census = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in capture.Items)
        {
            var (kind, id) = ClassifyDocument(item.Metadata, item.SymbolKind);
            census[kind] = census.GetValueOrDefault(kind) + 1;
            if (string.IsNullOrEmpty(item.Metadata)) continue;

            // Collision-free by construction. An earlier dump keyed files on kind+id alone;
            // Report, Query, XmlPort and ReportExtension carry their id in a CHILD element,
            // so every one of them landed on id=0 and collapsed onto a single file per kind.
            // The name disambiguates, and a residual clash still gets a suffix rather than
            // overwriting — silently losing a document is the failure this whole harness is
            // meant to make impossible.
            var stem = $"{kind}-{id}-{Sanitize(item.Name)}";
            var file = stem;
            for (int n = 2; !used.Add(file); n++) file = $"{stem}~{n}";

            var rel = "metadata/" + file + ".xml";
            File.WriteAllText(Path.Combine(outDir, rel), item.Metadata);
            objects.Add(new BundleObject(kind, id, item.Name, rel, Sha256(item.Metadata)));
        }

        // Every kind the harness keys by id, not MetaTable alone. Query and XmlPort joined that
        // set in #3782 and both had reported id 0 for every document until ClassifyDocument
        // learned the child-element spelling — which a MetaTable-only guard could not see; Report
        // is the third kind with that spelling and joined in step 5.
        //
        // MetadataRuntimeDeltas is deliberately excluded: one <MetadataRuntimeDeltas> root covers
        // TableExtension, PageExtension and PermissionSetExtension, so several of them
        // legitimately carry the id of the object they extend — System Application's bundle has
        // two on id 774. Enum is excluded for the mirror-image reason: one <Enum> root covers
        // both Enum and EnumExtension, so an enum and an enumextension can carry the same id
        // without either being a duplicate.
        var idKeyedKinds = new[] { "MetaTable", "PageDefinition", "CodeUnit", "Query", "XmlPort", "Report", "PermissionSet" };
        var duplicateIds = objects
            .Where(o => idKeyedKinds.Contains(o.Kind, StringComparer.Ordinal))
            .GroupBy(o => (o.Kind, o.Id)).Where(g => g.Count() > 1)
            .Select(g => $"{g.Key.Kind} {g.Key.Id} x{g.Count()}").ToArray();
        if (duplicateIds.Length > 0)
            throw new InvalidOperationException(
                "documents share a (kind, id): " + string.Join("; ", duplicateIds) +
                ". The harness keys these kinds by id, so this would silently compare one and " +
                "drop the other. An id of 0 across a whole kind means ClassifyDocument did not " +
                "find where that kind states its id.");

        var manifest = new BundleManifest(
            BundleSchema,
            new BundleApp(identity.Id, identity.Name, identity.Publisher, identity.Version.ToString()),
            Path.GetFileName(Path.TrimEndingDirectorySeparator(artifacts)),
            Path.GetFileName(appPath),
            DateTime.UtcNow.ToString("O"),
            new BundleEmit(emitSuccess, capture.Items.Count, objects.Count, emitErrors, emitMs),
            census,
            objects.OrderBy(o => o.Kind, StringComparer.Ordinal).ThenBy(o => o.Id).ThenBy(o => o.Name, StringComparer.Ordinal).ToArray());

        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "manifest.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

        Console.WriteLine($"[bundle] {objects.Count} document(s) -> {outDir}");
        foreach (var kv in census) Console.WriteLine($"   {kv.Key,-24} {kv.Value,6}");
        Console.WriteLine($"[total] {swTotal.Elapsed.TotalSeconds:F1}s");
        return emitErrors == 0 ? 0 : 1;
    }

    /// <summary>
    /// The document's OWN root element decides the kind, never the compiler's SymbolKind.
    /// They disagree: MetadataRuntimeDeltas is the root for TableExtension, PageExtension AND
    /// PermissionSetExtension, and one &lt;Enum&gt; root covers both Enum and EnumExtension.
    /// Keying on SymbolKind would split documents that BC treats as one shape.
    ///
    /// <para>The id is read from the root ATTRIBUTE first and from a direct child
    /// <c>&lt;ID&gt;</c> ELEMENT second, because BC's emitter uses both spellings and which one
    /// it uses is a property of the kind: Query, XmlPort and Report state the id as a child
    /// element, every other kind as an attribute. Reading only the attribute reported
    /// <c>Id = 0</c> for all 12 of those documents in a System Application bundle — a value that
    /// keys nothing, is not unique, and reads exactly like a real id (#3782, steps 3/4/5).</para>
    /// </summary>
    private static (string Kind, int Id) ClassifyDocument(string? metadata, string symbolKind)
    {
        if (string.IsNullOrEmpty(metadata)) return ("<no-metadata>" + symbolKind, 0);
        try
        {
            var doc = new XmlDocument();
            doc.LoadXml(metadata);
            var root = doc.DocumentElement!;
            var idText = root.GetAttribute("ID");
            if (string.IsNullOrEmpty(idText)) idText = root.GetAttribute("Id");
            // A DIRECT child only: <QueryColumn><ID>…</ID></QueryColumn> is a column's id, and
            // a descendant search would return one of those for the query itself.
            if (string.IsNullOrEmpty(idText))
                foreach (XmlNode child in root.ChildNodes)
                    if (child is XmlElement e
                        && (e.Name == "ID" || e.Name == "Id")
                        && !string.IsNullOrEmpty(e.InnerText))
                    { idText = e.InnerText.Trim(); break; }
            int.TryParse(idText, out var id);
            return (root.Name, id);
        }
        catch (XmlException ex)
        {
            return ("<unparseable:" + ex.GetType().Name + ">", 0);
        }
    }

    private static string Sanitize(string name)
    {
        var chars = name.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray();
        var s = new string(chars).Trim('_');
        return s.Length > 60 ? s[..60] : (s.Length == 0 ? "unnamed" : s);
    }

    internal static string Sha256(string text)
        => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}

internal sealed record BundleApp(Guid Id, string Name, string Publisher, string Version);
internal sealed record BundleEmit(bool Success, int Objects, int WithMetadata, int Errors, long EmitMs);
internal sealed record BundleObject(string Kind, int Id, string Name, string File, string Sha256);

internal sealed record BundleManifest(
    [property: JsonPropertyName("schema")] int Schema,
    [property: JsonPropertyName("app")] BundleApp App,
    [property: JsonPropertyName("bcBuild")] string BcBuild,
    [property: JsonPropertyName("sourcePackage")] string SourcePackage,
    [property: JsonPropertyName("generatedAtUtc")] string GeneratedAtUtc,
    [property: JsonPropertyName("emit")] BundleEmit Emit,
    [property: JsonPropertyName("census")] IReadOnlyDictionary<string, int> Census,
    [property: JsonPropertyName("objects")] IReadOnlyList<BundleObject> Objects);

/// <summary>
/// Keeps every application object the emitter hands over, with the metadata document BC
/// produced for it. Nothing is filtered here: what a document is FOR is the harness's
/// question, and dropping a kind at capture time would decide it silently.
/// </summary>
internal sealed class CaptureOutputter : NavEmit.CodeModuleOutputter
{
    internal readonly List<(string Name, string SymbolKind, string? Metadata, int CodeLength)> Items = new();

    public CaptureOutputter() : base(NavCA.EmitOptions.Default) { }

    public override void InitializeModule(NavCA.IModuleSymbol m) { }

    public override void AddApplicationObject(
        NavCA.IApplicationObjectTypeSymbol s, byte[] code, string metadata, string debugCode)
        => Items.Add((s.Name, s.GetType().Name, metadata, code?.Length ?? 0));

    public override void AddProfileObject(NavCA.ISymbol s, byte[] code, string metadata, string debugCode) { }
    public override void AddNavigationObject(string s) { }
    public override void AddExternalBusinessEvent(string s) { }
    public override void AddMovedObjects(string s) { }
    public override void FinalizeModule() { }

    public override System.Collections.Immutable.ImmutableArray<NavCA.Diagnostics.Diagnostic> GetDiagnostics()
        => System.Collections.Immutable.ImmutableArray<NavCA.Diagnostics.Diagnostic>.Empty;
}

// DependencyMetadataProducer — BC's own metadata documents for a dependency that ships
// source but whose code the runner never compiles (issue #3549, "Producer A").
//
// WHY A DEPENDENCY NEEDS ITS OWN PRODUCER
//   DependencyLoader.LoadOne returns at Tier 1 (precompiled sidecar DLL) or Tier 2 (R2R
//   `publishedartifacts/*.dll`) whenever compiled code is available — which for Microsoft's
//   apps it always is. Tier 3, the source compile, is the ONLY tier that runs BC's emitter,
//   and it is the emitter that produces the metadata document AlObjectMetadataRegistry
//   captures (#3548). So an R2R dependency's tables are described by the runner's
//   SymbolReference hand-derivation and never by BC's own answer, even though the package
//   ships the source BC would need.
//
//   Measured on Business Foundation table 310 "No. Series Relationship", BC 28.1, read from AL
//   through RecordRef: the derivation answers SystemCreatedBy.Relation = 0, and BC's own
//   document — loaded through MetaTable.CreateMetaTableFromXml, which is what adds the platform
//   system fields and their relations — answers 2000000120, the User table.
//   docs/dependency-metadata-from-bc.md has the full RED/GREEN and the per-shape route table.
//
// AVAILABILITY DECIDES THE ROUTE — A FAILED COMPILE IS LOUD, NEVER A DOWNGRADE
//   This is the constraint the issue names as its main risk, and it shapes the whole class.
//   Producing a document is a COMPILE, so it has two failure modes that must not be conflated:
//
//     "no source in the package"  -> nothing to produce. The consumer's availability gate
//                                    finds no document and keeps the derivation. Correct, and
//                                    the state every symbol-only package is in (#3533/#3545).
//     "source present, emit failed" -> a BROKEN BUILD. Silently keeping the derivation here
//                                    would answer a table's metadata from a weaker source
//                                    because a compile crashed, under a green test run. That
//                                    is the silent-default shape .claude/rules/loud-failures.md
//                                    exists to prevent, so it throws.
//
//   The seam that separates them is HasCompilableSource: it is answered from the package
//   BEFORE any compile is attempted, so "unavailable" is never inferred from a failure.
//   #3590 is why that ordering is load-bearing rather than stylistic — BuildNCLMetaTable
//   swallows a construction failure into a cached null with its log line filtered out by
//   default, so a failure that reached the consumer would be indistinguishable from absence.
//
// COST, AND WHY IT IS OPT-IN PER APP
//   Once per (app id, app version, BC version), never per run: the documents persist to
//   AlObjectMetadataRegistry's sidecar format under the `dep-metadata` cache root and are
//   replayed on every later run. Business Foundation (96 AL files): ~6.2 s cold, 55 documents.
//
//   Emit is atomic per module, so one object BC cannot emit yields ZERO documents for the whole
//   app rather than a partial result. System Application hits exactly that — BadExpression on
//   `Business Chart.Initialize()` under the runner's .NET probing paths — which is why
//   AL_RUNNER_DEP_METADATA_FROM_BC takes app NAMES and not just "on". #3745 is that blocker;
//   the same app compiles clean in tools/metadata-ground-truth/, which ships .NET reference
//   shims the runner's probing paths do not.
//
// NOT BASE APPLICATION
//   Base Application ships 8,025 AL files and is deliberately excluded. Its emit needs a
//   PublicKeyToken=null copy of Microsoft.AspNetCore.StaticFiles that no BC artifact ships,
//   and without it the emitter produces ZERO objects — a hard blocker, not a cost question.
//   tests/expectations/metadata-equivalence/apps.json records the same exclusion for the same
//   reason. EmitProducedNothing below is what turns that into a loud failure rather than an
//   empty cache entry that would read as "this app has no metadata".

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AlRunner.Infrastructure;

namespace AlRunner;

/// <summary>
/// Compiles a source-shipping dependency's AL with BC's own compiler so BC's metadata
/// documents reach <see cref="AlObjectMetadataRegistry"/>, and persists them per
/// (app id, app version, BC version).
/// </summary>
internal static class DependencyMetadataProducer
{
    /// <summary>
    /// Apps this producer never compiles, by name. Base Application is the only entry and it
    /// is not a cost decision — see the header. Matched on the manifest name so a differently
    /// versioned or differently published copy is still excluded.
    /// </summary>
    private static readonly HashSet<string> NeverCompile =
        new(StringComparer.OrdinalIgnoreCase) { "Base Application" };

    /// <summary>
    /// True when this package carries AL source a compile could consume. Answered from the
    /// package alone, BEFORE any compile — that is what lets a caller tell "nothing to
    /// produce" apart from "the compile failed", which the header explains is the one
    /// distinction this class must never blur.
    /// </summary>
    internal static bool HasCompilableSource(string appPath)
    {
        try { return AppLoader.ExtractAlWithPaths(appPath).Count > 0; }
        catch { return false; }
    }

    internal static bool IsExcluded(AppManifest m) => NeverCompile.Contains(m.Name);

    /// <summary>
    /// The identity a dependency's metadata cache entry is stored under. The BC version is
    /// part of it because the documents are BC's own emitter output for one exact build — a
    /// key without it would serve one BC version's metadata to another, which is the failure
    /// the ground-truth generator avoids by keying its bundles the same way.
    /// </summary>
    internal static string CacheKey(AppManifest m)
        => $"{m.AppId:N}_{m.Version}_{AlRunner.Infrastructure.BcArtifacts.SelectedVersion}";

    private static string SidecarPath(AppManifest m)
        => Path.Combine(
            AlRunner.Infrastructure.CacheRoots.Resolve("dep-metadata"),
            CacheKey(m) + ".object-metadata.json");

    /// <summary>
    /// Make BC's metadata documents for <paramref name="m"/> available in
    /// <see cref="AlObjectMetadataRegistry"/>, compiling the package's source once if no
    /// cache entry exists yet. Returns the number of documents now available for this app,
    /// or 0 when the app has no source to compile (the ordinary symbol-only case).
    /// </summary>
    /// <exception cref="DependencyLoadException">
    /// The package HAS source and the compile or emit failed. Never downgraded to a silent 0 —
    /// see the header.
    /// </exception>
    internal static int Ensure(AppManifest m, string appPath, BcCompiler compiler)
    {
        if (IsExcluded(m)) return 0;

        var sidecar = SidecarPath(m);
        if (File.Exists(sidecar))
        {
            try
            {
                var replayed = AlObjectMetadataRegistry.LoadSidecar(sidecar);
                Trace($"cache HIT {m.Name} v{m.Version} — {replayed} document(s)");
                return replayed;
            }
            catch (Exception ex)
            {
                // A corrupt entry is a cache problem, not an app problem: delete it and
                // recompile. Distinct from an emit failure, which is the app's own build
                // breaking and must not be swallowed.
                Trace($"cache entry unreadable for {m.Name} ({ex.Message}); recompiling");
                try { File.Delete(sidecar); } catch { /* best effort */ }
            }
        }

        var sources = ReadSource(m, appPath);
        if (sources.Count == 0) return 0;   // symbol-only: nothing to produce, not a failure

        var keysBefore = new HashSet<string>(AlObjectMetadataRegistry.Keys, StringComparer.Ordinal);
        var work = Directory.CreateTempSubdirectory($"al-runner-depmeta-{m.AppId:N}-");
        try
        {
            // Written at the package's OWN relative paths, not flattened into one directory.
            // Two things break under flattening, and both present as AL0185 "X is missing" for
            // objects the app itself declares: a package ships several files with one base name
            // (System Application has an `EmailOutbox.Page.al` and an `EmailOutbox.Table.al`),
            // and BC resolves a resource — a control add-in's files, a report layout — relative
            // to the source that references it.
            foreach (var (path, src) in sources)
            {
                var rel = SafeRelativePath(path);
                var dest = Path.Combine(work.FullName, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.WriteAllText(dest, src);
            }

            // A shipped .app carries NavxManifest.xml, not app.json, and BcCompiler.Emit reads
            // its compiler inputs — target, features, preprocessor symbols — from an app.json
            // beside the source. Without one every input silently falls back to its default,
            // which is not this app's configuration: `Target` in particular decides whether
            // OnPrem-scoped objects compile at all. Synthesized rather than defaulted so the
            // compile matches how Microsoft built the package.
            WriteSynthesizedAppJson(work.FullName, m, appPath);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using (BcCompiler.ScopeCurrentAppIdentity(m.AppId, m.Publisher, m.Version))
                    compiler.Emit(new[] { work.FullName }, m.Name, work.FullName);
            }
            catch (Exception ex)
            {
                throw Loud(m, "METADATA-EMIT-FAIL", DependencyLoadException.FlattenException(ex), ex);
            }

            var produced = AlObjectMetadataRegistry.Keys
                .Where(k => !keysBefore.Contains(k)).ToArray();

            // The Base Application shape, and the reason this is a throw rather than an empty
            // cache entry: BC's emitter can return success having produced nothing at all when
            // a .NET reference does not resolve. Persisting that would record "this app has no
            // metadata" permanently, and every table would silently keep the derivation.
            if (produced.Length == 0)
                throw Loud(m, "METADATA-EMIT-ZERO",
                    "BC's emitter produced no metadata documents from this app's AL source. " +
                    "That is a compile failure, not an app without metadata — caching it would " +
                    "leave every table on the hand-derivation with nothing saying why. " +
                    "See issue #3549.", null);

            Persist(sidecar, produced, m, sw.ElapsedMilliseconds);
            return produced.Length;
        }
        finally
        {
            try { work.Delete(recursive: true); } catch { /* temp cleanup is best effort */ }
        }
    }

    /// <summary>
    /// Write an app.json describing <paramref name="m"/> next to the extracted source, carrying
    /// the attributes BC's compiler reads that the runtime <see cref="AppManifest"/> does not
    /// model — <c>target</c>, <c>features</c>, <c>preprocessorSymbols</c>, <c>runtime</c> —
    /// taken from the package's own NavxManifest.xml so the compile matches how the package was
    /// built. An attribute the manifest omits is omitted here too rather than guessed, leaving
    /// BcCompiler's own default to apply.
    /// </summary>
    private static void WriteSynthesizedAppJson(string dir, AppManifest m, string appPath)
    {
        var attrs = ReadNavxAppAttributes(appPath);
        string? Attr(string name) => attrs.TryGetValue(name, out var v) && !string.IsNullOrEmpty(v) ? v : null;

        var json = new System.Text.Json.Nodes.JsonObject
        {
            ["id"] = m.AppId.ToString(),
            ["name"] = m.Name,
            ["publisher"] = m.Publisher,
            ["version"] = m.Version.ToString(),
        };
        if (Attr("Target") is { } target) json["target"] = target;
        if (Attr("Runtime") is { } runtime) json["runtime"] = runtime;
        if (Attr("Platform") is { } platform) json["platform"] = platform;
        if (Attr("ContextSensitiveHelpUrl") is { } help) json["contextSensitiveHelpUrl"] = help;
        // `Features` and `PreprocessorSymbols` are space/comma-separated attribute lists in the
        // NAVX manifest and arrays in app.json.
        if (Attr("Features") is { } features)
            json["features"] = ToJsonArray(features);
        if (Attr("PreprocessorSymbols") is { } symbols)
            json["preprocessorSymbols"] = ToJsonArray(symbols);

        File.WriteAllText(Path.Combine(dir, "app.json"), json.ToJsonString());
    }

    private static System.Text.Json.Nodes.JsonArray ToJsonArray(string list)
    {
        var arr = new System.Text.Json.Nodes.JsonArray();
        foreach (var part in list.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            arr.Add(part);
        return arr;
    }

    /// <summary>
    /// The <c>&lt;App&gt;</c> element's attributes from a package's NavxManifest.xml, including
    /// the R2R nested-package case. Returns empty rather than throwing when the manifest cannot
    /// be read: every attribute it supplies has a working default, so an unreadable manifest
    /// degrades the compile's fidelity but must not be confused with the app having no source
    /// (the distinction the class header turns on).
    /// </summary>
    private static Dictionary<string, string> ReadNavxAppAttributes(string appPath)
    {
        var empty = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var xml = AppLoader.ReadNavxManifestXml(appPath);
            if (string.IsNullOrEmpty(xml)) return empty;
            var doc = System.Xml.Linq.XDocument.Parse(xml);
            var app = doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "App");
            if (app == null) return empty;
            foreach (var a in app.Attributes())
                empty[a.Name.LocalName] = a.Value;
            return empty;
        }
        catch { return empty; }
    }

    private static IReadOnlyList<(string Path, string Source)> ReadSource(AppManifest m, string appPath)
    {
        try { return AppLoader.ExtractAlWithPaths(appPath); }
        catch (Exception ex)
        {
            // Reading the package failed — distinct from the package having no source, which
            // ExtractAl reports as an empty list rather than a throw.
            throw Loud(m, "METADATA-SOURCE-UNREADABLE", ex.Message, ex);
        }
    }

    private static void Persist(string sidecar, string[] producedKeys, AppManifest m, long emitMs)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(sidecar)!);
            // Same write-temp-then-move the compiled-deps sidecars use, so a run interrupted
            // mid-write never leaves a half-file that the next run would read as corrupt.
            var tmp = sidecar + ".tmp-" + Environment.ProcessId;
            AlObjectMetadataRegistry.SaveSidecar(tmp, producedKeys);
            File.Move(tmp, sidecar, overwrite: true);
            Trace($"WROTE {m.Name} v{m.Version} — {producedKeys.Length} document(s), {emitMs}ms");
        }
        catch (Exception ex)
        {
            // The documents ARE in the registry; only their persistence failed. Degrading to
            // "recompile next run" is correct and costs time, not correctness — unlike an
            // emit failure, which changes the answer.
            Console.Error.WriteLine(
                $"[dep-metadata] cache write failed for {m.Name} v{m.Version}: {ex.Message}; " +
                "documents are available for this run and will be recompiled on the next one");
        }
    }

    private static DependencyLoadException Loud(
        AppManifest m, string stage, string detail, Exception? inner)
    {
        Console.Error.WriteLine($"[dep-metadata-fail] {m.Publisher}_{m.Name} v{m.Version}: {stage} — {detail}");
        return inner == null
            ? new DependencyLoadException(m.Publisher, m.Name, m.Version.ToString(), stage, detail)
            : new DependencyLoadException(m.Publisher, m.Name, m.Version.ToString(), stage, detail, inner);
    }

    /// <summary>
    /// A package-relative source path, made safe to write under the work directory while
    /// KEEPING its directory structure and file name — see the extraction loop for why
    /// flattening breaks the compile.
    ///
    /// <para>Package entry names are URL-encoded (`src/User%2520Details/...`), which is
    /// harmless to write literally and is left as it is: BC resolves object references by
    /// symbol, not by path, and decoding introduces its own escaping questions for no gain.
    /// The one thing that must hold is that the result stays inside the work directory, so a
    /// `..` segment is dropped rather than trusted.</para>
    /// </summary>
    private static string SafeRelativePath(string name)
    {
        var parts = name.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p != "." && p != "..")
            .Select(p => new string(p.Select(c =>
                Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()))
            .ToArray();
        return parts.Length == 0
            ? $"object_{System.Threading.Interlocked.Increment(ref _fileSeq)}.al"
            : Path.Combine(parts);
    }

    private static int _fileSeq;

    private static void Trace(string message)
    {
        var t = Environment.GetEnvironmentVariable("AL_RUNNER_TRACE_DEP_METADATA");
        if (t == "1" || t == "2") Console.Out.WriteLine($"[dep-metadata] {message}");
    }
}

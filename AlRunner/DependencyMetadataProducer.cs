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
//   The seam that separates them is ReadSource, and it separates them by ANSWER SHAPE rather
//   than by ordering: an empty list means the package ships no source, and a package that
//   could not be read THROWS (METADATA-SOURCE-UNREADABLE) instead of returning empty. So an
//   unreadable package can never enter through the "absence" door — which is the property
//   that matters, and the one a boolean precondition answered before the compile could not
//   give, because a boolean has no way to say "I could not tell" (guards-need-a-third-state.md).
//   #3590 is why that distinction is load-bearing rather than stylistic — BuildNCLMetaTable
//   swallows a construction failure into a cached null with its log line filtered out by
//   default, so a failure that reached the consumer would be indistinguishable from absence.
//
// COST, AND WHY IT IS OPT-IN PER APP
//   Once per (app id, app version, BC version), never per run: the documents persist to
//   AlObjectMetadataRegistry's sidecar format under the `dep-metadata` cache root and are
//   replayed on every later run. Business Foundation (96 AL files): ~6.2 s cold, 55 documents.
//
//   Emit is atomic per module, so one object BC cannot emit yields ZERO documents for the whole
//   app rather than a partial result. System Application hit exactly that until #3745: the
//   service tier ships only the net6.0 Newtonsoft.Json, one System Application call against it
//   raised AL0133, and all 1,319 files produced nothing. AlRunner.csproj now stages the
//   netstandard2.0 build into dotnet-shims and BcCompiler probes it ahead of the tier; the app
//   produces 1,218 documents in ~11-13 s (docs/dependency-metadata-from-bc.md).
//
// NOT BASE APPLICATION
//   Base Application ships 8,025 AL files and is deliberately excluded. Its emit needs a
//   PublicKeyToken=null copy of Microsoft.AspNetCore.StaticFiles that no BC artifact ships,
//   and without it the emitter produces ZERO objects — a hard blocker, not a cost question.
//   #3745's shim does NOT reach it: that is a different assembly, absent from the artifacts
//   entirely rather than present in a build that does not bind.
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
    /// The container the per-compile scratch directories live under, exposed so a test can
    /// build the same name shape without duplicating the literal.
    /// </summary>
    internal static string ScratchContainer => Path.Combine(Path.GetTempPath(), "al-runner-depmeta");

    /// <summary>
    /// Create the directory this app's source is written to and compiled out of, owner-marked
    /// so a run killed mid-compile leaves something a later runner start can reclaim (#3838).
    ///
    /// <para><see cref="ScratchDirs.Create"/> rather than <see cref="ScratchDirs.Reserve"/>: the
    /// caller writes the package's sources straight into this path, and Reserve deliberately
    /// does not create the leaf. Rather than Release-only ownership, because the lifecycle here
    /// is reserve-use-delete — <c>Ensure</c>'s <c>finally</c> releases it on every path it
    /// reaches, and the sidecar exists for the path it does not.</para>
    ///
    /// <para>Nested under a container rather than flat in the temp root, matching
    /// <see cref="AlRunner.Infrastructure.PerProcessScratch"/>: the sweep reaches a sidecar at
    /// depth 1 as readily as at depth 0, and one container keeps a temp listing legible when a
    /// run resolves several source-shipping dependencies. The app id stays in the leaf so a
    /// human reading that listing can still tell which app a directory belongs to, and a GUID
    /// separates two compiles of the SAME app — a <c>--watch</c> session re-resolving a
    /// dependency, or two runners sharing one TMPDIR — which the app id alone would collide.</para>
    /// </summary>
    internal static string CreateWorkDir(Guid appId)
        => ScratchDirs.Create(Path.Combine(
            ScratchContainer, $"{appId:N}-{Guid.NewGuid():N}"));

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
        var work = CreateWorkDir(m.AppId);
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
                var dest = Path.Combine(work, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.WriteAllText(dest, src);
            }

            // A shipped .app carries NavxManifest.xml, not app.json, and BcCompiler.Emit reads
            // its compiler inputs — target, features, preprocessor symbols — from an app.json
            // beside the source. Without one every input silently falls back to its default,
            // which is not this app's configuration: `Target` in particular decides whether
            // OnPrem-scoped objects compile at all. Synthesized rather than defaulted so the
            // compile matches how Microsoft built the package.
            WriteSynthesizedAppJson(work, m, appPath);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            BcEmitOutput emitOutput;
            try
            {
                using (BcCompiler.ScopeCurrentAppIdentity(m.AppId, m.Publisher, m.Version))
                    emitOutput = compiler.Emit(new[] { work }, m.Name, work);
            }
            catch (Exception ex)
            {
                throw Loud(m, "METADATA-EMIT-FAIL", DependencyLoadException.FlattenException(ex), ex);
            }

            var produced = AlObjectMetadataRegistry.Keys
                .Where(k => !keysBefore.Contains(k)).ToArray();

            // #2247, the partial case of METADATA-EMIT-ZERO below. This method cannot see a
            // shortfall by counting: it knows how many documents appeared, never how many BC
            // should have produced, so 55 of 70 and 70 of 70 are the same observation from
            // here — which is what #3875 measured on Business Foundation, reported as success
            // with zero AL0185 lines at default verbosity. The emit-retry loop's exclusion
            // list IS that missing denominator, and until this call kept the BcEmitOutput it
            // was discarded at the call site.
            //
            // Caching is what makes silence permanent rather than merely quiet: Persist below
            // writes a sidecar every later run replays without recompiling, so a partial
            // document set would be recorded as this app's complete metadata and every table
            // it did not cover would stay on the hand-derivation with nothing saying why —
            // the same reasoning METADATA-EMIT-ZERO's own comment gives for throwing.
            if (emitOutput.ExcludedObjects.Count > 0)
                throw Loud(m, "METADATA-EMIT-EXCLUDED",
                    DependencyLoader.BuildDependencyEmitExcludedDetail(
                        emitOutput.ExcludedObjects, emitOutput.Sources.Count,
                        emitOutput.ExcludedObjectDiagnostics ?? Array.Empty<string>())
                    + $" {produced.Length} metadata document(s) were produced; caching them"
                    + " would record that as the app's complete metadata, leaving every"
                    + " uncovered table on the hand-derivation.",
                    null);

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
            // Kept, not replaced by the sweep. ScratchDirs.Release deletes the tree AND the
            // sidecar and forgets the directory, so the happy path still reclaims immediately
            // rather than leaving a full source-tree copy for the next runner start to find.
            // The ownership record is the second net, for the run that never reaches here.
            ScratchDirs.Release(work);
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

    /// <summary>
    /// The seam between "nothing to produce" and "the compile failed" (#3748). An empty list
    /// means the package ships no AL source — the ordinary symbol-only case, which Ensure
    /// turns into a quiet 0. A package that cannot be READ throws instead, so an unreadable
    /// package never reaches the caller as an absence.
    /// </summary>
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

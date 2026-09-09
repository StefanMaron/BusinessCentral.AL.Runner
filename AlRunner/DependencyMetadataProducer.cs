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
//   Measured on Business Foundation table 230 "Source Code", BC 28.1: the derivation answers
//   KeyCount=2 and SystemCreatedBy.Relation=0; BC's own document, loaded through
//   MetaTable.CreateMetaTableFromXml, answers KeyCount=3 and 2000000120. Both are
//   AL-observable through RecordRef. docs/dependency-metadata-from-bc.md has the measurement
//   and the per-shape route table.
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
// COST
//   Once per (app id, app version, BC version), never per run: the documents persist to
//   AlObjectMetadataRegistry's sidecar format under the `dep-metadata` cache root and are
//   replayed on every later run. Measured cold on this box, BC 28.1: Business Foundation
//   (96 AL files, 12 tables) 3.0s; System Application (1,319 AL files, 138 tables) 14.5s.
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
        try { return AppLoader.ExtractAl(appPath).Count > 0; }
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
            foreach (var (name, src) in sources)
                File.WriteAllText(Path.Combine(work.FullName, SafeFileName(name)), src);

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

    private static IReadOnlyList<(string Name, string Source)> ReadSource(AppManifest m, string appPath)
    {
        try { return AppLoader.ExtractAl(appPath); }
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
    /// A source file name from inside the package, reduced to something writable on this
    /// filesystem. Collisions are made impossible by appending an index rather than by hoping
    /// the names are unique — a package may ship two `src/Foo.al` under different folders.
    /// </summary>
    private static int _fileSeq;
    private static string SafeFileName(string name)
    {
        var baseName = Path.GetFileNameWithoutExtension(name);
        var safe = new string(baseName.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
        if (safe.Length > 80) safe = safe[..80];
        return $"{safe}_{System.Threading.Interlocked.Increment(ref _fileSeq)}.al";
    }

    private static void Trace(string message)
    {
        var t = Environment.GetEnvironmentVariable("AL_RUNNER_TRACE_DEP_METADATA");
        if (t == "1" || t == "2") Console.Out.WriteLine($"[dep-metadata] {message}");
    }
}

namespace AlRunner;

// Suite discovery under a bundle root, and the enum-registry sidecar persisted
// alongside the AL-output cache. Split out of Program.cs (#2665) -- purely static,
// no captured state.
internal static partial class ProgramSupport
{

    // Sidecar: serialize AlEnumMetadataRegistry to <key>.enum-registry.json so
    // cache HIT can replay the side-effect that emit would have populated.
    // Schema (v10, #2709): { "enums": [ { "id": int, "name": string, "options": [string], "indexes": [int], "implementations": [[int]], "captions": [string?], "extends": int? }, ... ] }
    // v10 switched from AlEnumMetadataRegistry.Snapshot()'s MERGED base+extension view to
    // SnapshotRaw()'s unmerged one — a merged entry replayed through Register alone
    // clobbers whichever of base/extension registers for real later in the process (#2709;
    // see SnapshotRaw's doc comment for the two failure shapes this caused).
    internal static int SaveEnumRegistrySidecar(string path)
    {
        var raw = AlEnumMetadataRegistry.SnapshotRaw().ToList();
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
                // #2306 — the enum-level DefaultImplementation / UnknownValueImplementation
                // fallbacks, without which an enum that names no per-value Implementation cannot
                // be cast to its interface on a cache HIT.
                defaultImplementations = r.Entry.DefaultImplementations,
                unknownImplementations = r.Entry.UnknownImplementations,
                // #2709 — null for a base registration; the base enum id an enumextension's
                // own (unmerged) entry targets otherwise. See SnapshotRaw.
                extends = r.ExtendsTargetId,
            }).ToArray(),
            // v4: per-report runtime metadata XML captured from emit — replayed on
            // cache HIT so NavReportSync builds real MetaReport instances.
            reportMetadata = AlReportMetadataRegistry.Ids
                .OrderBy(i => i)
                .Select(i => new
                {
                    id = i,
                    xml = AlReportMetadataRegistry.TryGet(i, out var x) ? x : string.Empty,
                }).ToArray(),
            // v5: per-report rendering-layout declarations captured from the AL
            // compiler's ReportLayoutSymbol — replayed on cache HIT so layout
            // selection by name keeps working on a warm cache.
            reportLayouts = AlReportLayoutRegistry.Snapshot(),
            // v6: per-page runtime metadata XML captured from emit — replayed on cache
            // HIT so NCLMetaForm.LoadMetadata() still builds a real control tree on a
            // warm run. Emit only fires on a MISS; anything captured there and not
            // persisted here is silently gone on the next run.
            pageMetadata = AlPageMetadataRegistry.Ids
                .OrderBy(i => i)
                .Select(i => new
                {
                    id = i,
                    xml = AlPageMetadataRegistry.TryGet(i, out var x) ? x : string.Empty,
                }).ToArray(),
            // v8: per-xmlport runtime metadata XML captured from emit — replayed on cache
            // HIT so NCLMetaXmlPort.LoadMetadata() still builds a real node schema on a warm
            // run. Same emit-only capture hazard as pageMetadata above.
            xmlPortMetadata = AlXmlPortMetadataRegistry.Ids
                .OrderBy(i => i)
                .Select(i => new
                {
                    id = i,
                    xml = AlXmlPortMetadataRegistry.TryGet(i, out var x) ? x : string.Empty,
                }).ToArray(),
        };
        var json = System.Text.Json.JsonSerializer.Serialize(dto, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = false,
        });
        File.WriteAllText(path, json);
        return raw.Count;
    }

    // Replay AlEnumMetadataRegistry from <key>.enum-registry.json. Throws on
    // corrupt JSON; the caller treats any exception as cache MISS and rebuilds.
    internal static int LoadEnumRegistrySidecar(string path)
    {
        // A sidecar's optional int-array property, or null when absent/empty — "declares none"
        // (issue #2306).
        static int[]? ReadIdList(System.Text.Json.JsonElement parent, string name)
        {
            if (!parent.TryGetProperty(name, out var el) || el.ValueKind != System.Text.Json.JsonValueKind.Array)
                return null;
            var ids = new int[el.GetArrayLength()];
            int k = 0;
            foreach (var v in el.EnumerateArray()) ids[k++] = v.GetInt32();
            return ids.Length > 0 ? ids : null;
        }

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
            // v9: per-value Captions (issue #1775). Absent in pre-v9 sidecars — fine, the
            // cache key schema bump above makes those unreachable anyway.
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
            // #2709 — see AlEnumMetadataRegistry.LoadSidecar's matching comment: a present
            // `extends` marks this entry as an enumextension's own (unmerged) values, so
            // replay it through RegisterExtension, never Register, or it clobbers the base
            // enum's _byId slot instead of accumulating alongside it.
            int? extendsTargetId = null;
            if (e.TryGetProperty("extends", out var extEl) && extEl.ValueKind == System.Text.Json.JsonValueKind.Number)
                extendsTargetId = extEl.GetInt32();

            if (extendsTargetId.HasValue)
                AlEnumMetadataRegistry.RegisterExtension(extendsTargetId.Value, name, opts, idxs, implementations, captions);
            else
                AlEnumMetadataRegistry.Register(id, name, opts, idxs, implementations, captions,
                    ReadIdList(e, "defaultImplementations"), ReadIdList(e, "unknownImplementations"));
            count++;
        }
        // v4: replay per-report metadata XML (absent in pre-v4 sidecars — fine,
        // the cache key schema bump makes those unreachable anyway).
        if (doc.RootElement.TryGetProperty("reportMetadata", out var repArr)
            && repArr.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var e in repArr.EnumerateArray())
            {
                AlReportMetadataRegistry.Register(
                    e.GetProperty("id").GetInt32(),
                    e.GetProperty("xml").GetString() ?? string.Empty);
            }
        }
        // v5: replay per-report rendering-layout declarations.
        if (doc.RootElement.TryGetProperty("reportLayouts", out var layoutArr)
            && layoutArr.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            AlReportLayoutRegistry.LoadFromJsonArray(layoutArr);
        }
        // v6: replay per-page runtime metadata XML.
        if (doc.RootElement.TryGetProperty("pageMetadata", out var pageArr)
            && pageArr.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var e in pageArr.EnumerateArray())
            {
                AlPageMetadataRegistry.Register(
                    e.GetProperty("id").GetInt32(),
                    e.GetProperty("xml").GetString() ?? string.Empty);
            }
        }
        // v8: replay per-xmlport runtime metadata XML.
        if (doc.RootElement.TryGetProperty("xmlPortMetadata", out var xmlPortArr)
            && xmlPortArr.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var e in xmlPortArr.EnumerateArray())
            {
                AlXmlPortMetadataRegistry.Register(
                    e.GetProperty("id").GetInt32(),
                    e.GetProperty("xml").GetString() ?? string.Empty);
            }
        }
        return count;
    }

    internal static IEnumerable<string> EnumerateSuites(string root)
    {
        // Defence in depth for #1713. The CLI validates the positional roots up front, but
        // this runs again per watch-mode cycle and per bundle, and a directory can vanish
        // between the check and the walk (a watch session while the tree is being moved, a
        // submodule being re-checked-out). Yielding nothing lets the caller print its own
        // loud "SKIP (no suites)" line instead of throwing DirectoryNotFoundException out
        // of Main with exit 134 — the crash code, for a merely absent directory.
        if (!Directory.Exists(root)) yield break;

        // Root first: a directory that is itself one app (app.json at its root, or a
        // src//test/ split) is ONE bucket, however many category sub-directories it
        // holds. This is the al-language corpus shape — checking the root before
        // descending is what keeps the corpus a single compile unit.
        if (LooksLikeSuite(root)) { yield return Path.GetFullPath(root); yield break; }

        // Otherwise the root is a container of suites. Descend, but stop at the first
        // suite on each branch: a suite's own sub-directories are part of that suite,
        // never separate buckets.
        bool found = false;
        foreach (var d in EnumerateSuitesBelow(root))
        {
            found = true;
            yield return d;
        }

        // Flat bundle: no app.json and no src//test/ anywhere, but .al files exist.
        // Treat the whole root as one compilation + test unit.
        // SafeDirectoryScan: an unreadable subdirectory anywhere below `root` used to throw
        // out of this lazy .Any() and take the process down with exit 134 (#2206).
        if (!found && AlRunner.Infrastructure.SafeDirectoryScan.Files(root, "*.al").Count > 0)
            yield return Path.GetFullPath(root);
    }

    internal static IEnumerable<string> EnumerateSuitesBelow(string dir)
    {
        // Same guard as EnumerateSuites — this is the frame that actually threw in #1713,
        // and it also recurses into directories that may disappear mid-walk.
        if (!Directory.Exists(dir)) yield break;

        // #1713 guarded the directory that VANISHES; #2206 is the directory that is merely
        // UNREADABLE, which reached exactly the same `foreach` and produced exactly the same
        // exit 134 out of Main. Directory.EnumerateDirectories is lazy, so no try around the
        // call could have caught it either — the listing has to be materialised under the
        // guard, which is what SafeDirectoryScan does.
        foreach (var child in AlRunner.Infrastructure.SafeDirectoryScan.Directories(
                     dir, "*", SearchOption.TopDirectoryOnly))
        {
            if (LooksLikeSuite(child))
                yield return Path.GetFullPath(child);
            else
                foreach (var nested in EnumerateSuitesBelow(child))
                    yield return nested;
        }
    }

    // A directory is a suite if it declares its own app (app.json) or uses the
    // src//test/ split. The app.json clause is what makes flat suites — app.json plus
    // .al files, no sub-structure, the shape every tests/runner-extras suite uses —
    // enumerate individually instead of collapsing into one bundle (#1623, #1638).
    internal static bool LooksLikeSuite(string dir)
        => File.Exists(Path.Combine(dir, "app.json"))
        || Directory.Exists(Path.Combine(dir, "test"))
        || Directory.Exists(Path.Combine(dir, "src"));
}

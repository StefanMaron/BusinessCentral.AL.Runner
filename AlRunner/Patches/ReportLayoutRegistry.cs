// AlReportLayoutRegistry — the per-report `rendering { layout(Name) { … } }`
// declarations, captured from the AL compiler's own ReportLayoutSymbol at emit
// time.
//
// WHY A SEPARATE REGISTRY FROM AlReportMetadataRegistry
//   The runtime metadata XML that Compilation.Emit hands the ModuleOutputter
//   carries a <Layouts> block, but only Name / LayoutFriendlyName / Caption /
//   Summary — NOT the layout's Type, NOT its MimeType, and not the layout file.
//   (Verified by dumping the emitted XML for a two-layout report.) Those three
//   live on the compiler's ReportLayoutSymbol, so we read them there.
//
// WHAT CONSUMES IT
//   RecordPatches.ReportLayoutListVirtualTable populates the "Report Layout
//   List" system virtual table (2000000234) from these rows, which is exactly
//   where BC's OWN by-name resolution looks
//   (ReportLayoutSelection.GetLayoutByNameAndAppIDAsync filters that table by
//   Report ID + Name + App ID and hands the row to ReportLayout.Create).
//   Populating it means the real BC layout-selection code path runs unmodified.
//
// CACHE PARITY
//   Like the report-metadata registry, entries are persisted to a sidecar so a
//   compile-cache HIT replays exactly what the emit would have registered.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AlRunner;

/// <summary>One `layout(Name) { … }` declaration inside a report's `rendering` block.</summary>
public sealed record AlReportLayoutInfo(
    int ReportId,
    string Name,
    /// <summary>RDLC | Word | Excel | Custom — the AL `Type` property, verbatim.</summary>
    string LayoutType,
    /// <summary>The AL `MimeType` property, or "" when not declared.</summary>
    string MimeType,
    /// <summary>The AL `LayoutFile` property as written (app-root-relative).</summary>
    string LayoutFile,
    /// <summary>Absolute path the LayoutFile resolved to, or "" if it could not be resolved.</summary>
    string ResolvedPath,
    string Caption,
    string Summary,
    /// <summary>
    /// True when the report names this layout in its <c>DefaultRenderingLayout</c>
    /// property. AL requires every report using the <c>rendering</c> syntax to declare
    /// one, so this is what a plain <c>Report.SaveAs</c> with no explicit
    /// <c>SetTempLayoutSelectedName</c> selection renders through.
    /// </summary>
    bool IsDefault = false);

public static class AlReportLayoutRegistry
{
    private static readonly ConcurrentDictionary<int, List<AlReportLayoutInfo>> _byReportId = new();

    public static void Register(AlReportLayoutInfo layout)
    {
        if (layout.ReportId <= 0 || string.IsNullOrEmpty(layout.Name)) return;
        if (Environment.GetEnvironmentVariable("ALRUNNER_REPORT_LAYOUT_TRACE") == "1")
            Console.Error.WriteLine($"[ReportLayout] register {layout.ReportId} '{layout.Name}' type={layout.LayoutType} mime='{layout.MimeType}' file='{layout.LayoutFile}' resolved='{layout.ResolvedPath}' caption='{layout.Caption}'");
        var list = _byReportId.GetOrAdd(layout.ReportId, static _ => new List<AlReportLayoutInfo>());
        lock (list)
        {
            var existing = list.FindIndex(l => string.Equals(l.Name, layout.Name, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0) list[existing] = Merge(list[existing], layout);
            else list.Add(layout);
        }
    }

    /// <summary>
    /// Combines two registrations of the SAME layout, preferring a populated value over
    /// an empty one on every field.
    ///
    /// The same report layout is registered more than once per run — once per compilation
    /// that can see the report. Those passes are not equally informed: a pass compiling
    /// the app from source resolves LayoutFile to an absolute path, while a pass that sees
    /// the app only through its symbols has no source tree, so
    /// <c>ReportLayoutSymbol.FilepathSyntaxLocation</c> yields nothing and ResolvedPath
    /// comes back "". A last-writer-wins replace therefore let the poorer pass ERASE the
    /// resolved path (observed: report 71179675 registered with a real path, then again
    /// with ""), leaving the runtime unable to load the layout's bytes at all.
    ///
    /// Merging is safe because both registrations describe the same declaration: they can
    /// only disagree by one of them not knowing a value. A real value never overwrites a
    /// different real value — later wins only where it actually has something to say.
    /// </summary>
    internal static AlReportLayoutInfo Merge(AlReportLayoutInfo existing, AlReportLayoutInfo incoming) =>
        new(
            ReportId: incoming.ReportId,
            Name: incoming.Name,
            LayoutType: Prefer(existing.LayoutType, incoming.LayoutType),
            MimeType: Prefer(existing.MimeType, incoming.MimeType),
            LayoutFile: Prefer(existing.LayoutFile, incoming.LayoutFile),
            ResolvedPath: Prefer(existing.ResolvedPath, incoming.ResolvedPath),
            Caption: Prefer(existing.Caption, incoming.Caption),
            Summary: Prefer(existing.Summary, incoming.Summary),
            // Same "later wins only where it has something to say" rule: a pass that
            // could not read DefaultRenderingLayout reports false for every layout, and
            // must not un-mark a default an informed pass already established.
            IsDefault: existing.IsDefault || incoming.IsDefault);

    private static string Prefer(string existing, string incoming) =>
        string.IsNullOrEmpty(incoming) ? existing : incoming;

    public static IReadOnlyList<AlReportLayoutInfo> Get(int reportId)
    {
        if (!_byReportId.TryGetValue(reportId, out var list)) return Array.Empty<AlReportLayoutInfo>();
        lock (list) return list.ToArray();
    }

    public static int[] Ids => _byReportId.Keys.ToArray();

    public static int Count => _byReportId.Values.Sum(l => { lock (l) return l.Count; });

    public static void Clear() => _byReportId.Clear();

    /// <summary>All registered layouts, flattened (diagnostics + sidecar writers).</summary>
    public static AlReportLayoutInfo[] Snapshot()
        => _byReportId.Values
            .SelectMany(l => { lock (l) return l.ToArray(); })
            .OrderBy(l => l.ReportId).ThenBy(l => l.Name, StringComparer.Ordinal)
            .ToArray();

    // ── Sidecar (compile-cache parity) ────────────────────────────────────────

    public static int SaveSidecar(string path) => SaveSidecar(path, _byReportId.Keys);

    public static int SaveSidecar(string path, IEnumerable<int> onlyReportIds)
    {
        var idSet = new HashSet<int>(onlyReportIds);
        var rows = Snapshot().Where(l => idSet.Contains(l.ReportId)).ToArray();
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new { layouts = rows }));
        return rows.Length;
    }

    /// <summary>Replay a sidecar. Throws on corrupt JSON — callers treat that as cache MISS.</summary>
    public static int LoadSidecar(string path)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty("layouts", out var arr)
            || arr.ValueKind != System.Text.Json.JsonValueKind.Array)
            throw new InvalidDataException("report-layouts.json: missing 'layouts' array");
        return LoadFromJsonArray(arr);
    }

    /// <summary>Replay from an already-parsed JSON array (shared with the al-out enum sidecar).</summary>
    public static int LoadFromJsonArray(System.Text.Json.JsonElement arr)
    {
        int n = 0;
        foreach (var e in arr.EnumerateArray())
        {
            Register(new AlReportLayoutInfo(
                ReportId: e.GetProperty("ReportId").GetInt32(),
                Name: e.GetProperty("Name").GetString() ?? string.Empty,
                LayoutType: e.GetProperty("LayoutType").GetString() ?? string.Empty,
                MimeType: e.GetProperty("MimeType").GetString() ?? string.Empty,
                LayoutFile: e.GetProperty("LayoutFile").GetString() ?? string.Empty,
                ResolvedPath: e.GetProperty("ResolvedPath").GetString() ?? string.Empty,
                Caption: e.GetProperty("Caption").GetString() ?? string.Empty,
                Summary: e.GetProperty("Summary").GetString() ?? string.Empty,
                // Optional: sidecars written before IsDefault existed simply have no
                // default marker. Both cache keys hash the runner assembly, so such a
                // sidecar is already unreachable from a runner carrying this code —
                // tolerating it here just keeps a hand-copied cache from throwing.
                IsDefault: e.TryGetProperty("IsDefault", out var d)
                           && d.ValueKind == System.Text.Json.JsonValueKind.True));
            n++;
        }
        return n;
    }
}

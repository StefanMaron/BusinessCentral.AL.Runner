using System;
using System.IO;
using System.Reflection;

namespace AlRunnerV2.Patches;

/// <summary>
/// Completes the <c>ReportLayout</c> objects the runner hands to report rendering.
///
/// WHY THIS EXISTS
/// ---------------
/// For a layout declared <c>Type = Custom</c> (the shape every ISV rendering extension
/// uses) BC has no way to reach the layout's content in the runner:
///
///   ReportLayout.LayoutStream:
///       if (LegacyApplicationLayout &amp;&amp; layoutStream == null)
///           if ((uint)Format &lt;= 1u)            // RDLC / Word only
///               FetchLayoutFromApplication();
///       ... otherwise fall through to a NavMediaHelper media lookup
///
/// A Custom layout fails the <c>Format &lt;= 1</c> guard, so the application-layout fetch
/// is unreachable (measured: <c>FetchLayoutFromApplication</c> is never called once across
/// a full ISV run), and the media lookup finds nothing — there is no service tier and no
/// media rows. On top of that, the runner's own §report-layout-selection rewrite
/// synthesises a default ReportLayout "with empty media" when no selection row exists.
/// The result is a layout object with <c>layoutStream == null</c> and <c>Mimetype == ""</c>.
///
/// Both are consumed by the custom-render path:
///
///   ReportProcessorCustomGenerator.FinishAsync:
///       InvokeCustomDocumentMergerAsync(..., reportResultSet.Payload(printerName), ...,
///           customTemplate.LayoutStream ?? new MemoryStream(), ...)
///
/// so the AL subscriber receives an EMPTY template and a payload whose
/// <c>layoutmimetype</c> is "". Measured symptom: the subscriber declines, or renders
/// nothing and reports "LF-XML: The template is not well-formed XML".
///
/// WHY IT HOOKS ReportLayout AND NOT ReportResultSet
/// -------------------------------------------------
/// An earlier version hydrated <c>ReportResultSet.ReportLayout</c>. That is a DIFFERENT
/// OBJECT from the generator's <c>customTemplate</c> (measured: instance #10227578 vs
/// #57824120), so it fixed the payload's mimetype — which Payload does read from the
/// result set — while the layout STREAM the merger reads stayed null. Hydrating on
/// ReportLayout's own accessors, keyed by the instance's own ReportId, covers every
/// instance regardless of which object a given code path happens to hold.
///
/// FAITHFULNESS
/// ------------
/// The values come from the report's own AL <c>rendering { layout { MimeType; LayoutFile } }</c>
/// declaration, captured at compile time into <see cref="AlReportLayoutRegistry"/> and
/// persisted to a sidecar so a compile-cache hit replays them. That is what a live tier
/// would have published into the application layout table and served back. We only ever
/// FILL IN what BC left unset — anything BC resolved itself is untouched.
/// </summary>
public static class ReportLayoutHydration
{
    private static FieldInfo? _fLayoutStream;
    private static FieldInfo? _fMimetypeBacking;
    private static PropertyInfo? _pReportId;
    private static bool _bound;

    /// <summary>
    /// Prepended to <c>ReportLayout.get_LayoutStream</c>. Fills the backing field from the
    /// report's declared LayoutFile when BC has nothing, so the getter's own logic then
    /// returns real content instead of falling through to an empty media lookup.
    /// </summary>
    public static void HydrateLayoutStream(object layout)
    {
        try
        {
            if (layout is null) return;
            Bind(layout.GetType());

            var current = _fLayoutStream!.GetValue(layout) as Stream;
            // A non-null but EMPTY stream is the `?? new MemoryStream()` fallback having
            // already been stored — still nothing to render, so treat it as unset.
            if (current != null && current.Length > 0) return;

            var info = SingleDeclaredLayout(layout);
            if (info == null || string.IsNullOrEmpty(info.ResolvedPath)) return;

            if (!File.Exists(info.ResolvedPath))
                throw new InvalidOperationException(
                    $"[ReportLayout] report {info.ReportId} layout '{info.Name}' declares LayoutFile "
                    + $"'{info.LayoutFile}' resolved to '{info.ResolvedPath}', but no file exists there "
                    + "— the layout registry and the source tree disagree.");

            _fLayoutStream.SetValue(layout, new MemoryStream(File.ReadAllBytes(info.ResolvedPath)));

            if (Environment.GetEnvironmentVariable("ALRUNNER_REPORT_LAYOUT_TRACE") == "1")
                Console.Error.WriteLine(
                    $"[ReportLayout] hydrated stream for report {info.ReportId} from '{info.ResolvedPath}'");
        }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ReportLayout] stream hydration failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Prepended to <c>ReportLayout.CalculateMimetype</c>, which is what
    /// <c>ReportResultSet.Payload</c> reports as <c>layoutmimetype</c> — the value every ISV
    /// merger subscriber gates on. Without it BC falls back to a generic
    /// "Application/ReportLayout/Custom" that no ISV recognises.
    /// </summary>
    public static void HydrateMimetype(object layout)
    {
        try
        {
            if (layout is null) return;
            Bind(layout.GetType());

            if (!string.IsNullOrEmpty(_fMimetypeBacking!.GetValue(layout) as string)) return;

            var info = SingleDeclaredLayout(layout);
            if (info == null || string.IsNullOrEmpty(info.MimeType)) return;

            _fMimetypeBacking.SetValue(layout, info.MimeType);

            if (Environment.GetEnvironmentVariable("ALRUNNER_REPORT_LAYOUT_TRACE") == "1")
                Console.Error.WriteLine(
                    $"[ReportLayout] hydrated mimetype '{info.MimeType}' for report {info.ReportId}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ReportLayout] mimetype hydration failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// The report's declared layout, but only when there is exactly one. A multi-layout
    /// report is selected BY NAME through virtual table 2000000234 (see the
    /// report-layout-byname suite); if BC picked one of several it already knows more
    /// than we do, and guessing here could serve the wrong template.
    /// </summary>
    private static AlReportLayoutInfo? SingleDeclaredLayout(object layout)
    {
        var reportId = (int)_pReportId!.GetValue(layout)!;
        var declared = AlReportLayoutRegistry.Get(reportId);
        return declared.Count == 1 ? declared[0] : null;
    }

    private static void Bind(Type layoutType)
    {
        if (_bound) return;

        const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        _fLayoutStream = layoutType.GetField("layoutStream", Any)
            ?? throw new InvalidOperationException(
                "[ReportLayout] ReportLayout.layoutStream field not found — BC metadata shape changed.");
        _fMimetypeBacking = layoutType.GetField("<Mimetype>k__BackingField", Any)
            ?? throw new InvalidOperationException(
                "[ReportLayout] ReportLayout.Mimetype backing field not found — BC metadata shape changed.");
        _pReportId = layoutType.GetProperty("ReportId", Any)
            ?? throw new InvalidOperationException(
                "[ReportLayout] ReportLayout.ReportId not found — BC metadata shape changed.");

        _bound = true;
    }
}

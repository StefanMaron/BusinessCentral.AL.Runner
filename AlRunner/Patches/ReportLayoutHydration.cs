using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace AlRunner.Patches;

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
    private static PropertyInfo? _pName;
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

            var info = DeclaredLayoutFor(layout);
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

            var info = DeclaredLayoutFor(layout);
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
    /// The report's declared layout that this <c>ReportLayout</c> instance stands for.
    ///
    /// Matched BY NAME first. BC states which layout it selected on the instance itself —
    /// <c>ReportLayout.Name</c>, set from the Report Layout List (2000000234) row that
    /// GetLayoutByNameAndAppIDAsync found for a
    /// <c>SetTempLayoutSelectedName('Foo')</c> selection — so this reads BC's own answer
    /// rather than guessing.
    ///
    /// This method previously returned null for ANY report declaring more than one layout,
    /// on the reasoning that picking one of several could serve the wrong template. That
    /// was the right caution and the wrong conclusion: it meant a multi-layout report was
    /// never hydrated at all, so a by-name selection rendered from an EMPTY stream and the
    /// AL merger reported "LF-XML: The template is not well-formed XML: 'Root element is
    /// missing'". Nine Pageworks tests failed exactly this way; the same reports'
    /// unnamed/default renders passed, which is what made it look like a layout-content bug
    /// rather than a layout-identity one.
    ///
    /// The no-guessing rule still holds: with no usable name and several declared layouts,
    /// this yields null and leaves BC's own resolution untouched.
    /// </summary>
    private static AlReportLayoutInfo? DeclaredLayoutFor(object layout)
    {
        var reportId = (int)_pReportId!.GetValue(layout)!;
        return Choose(AlReportLayoutRegistry.Get(reportId), _pName!.GetValue(layout) as string);
    }

    /// <summary>
    /// The resolution rule itself, separated from the BC reflection so it can be tested
    /// directly. See <see cref="DeclaredLayoutFor"/> for what <paramref name="name"/> is.
    /// </summary>
    internal static AlReportLayoutInfo? Choose(IReadOnlyList<AlReportLayoutInfo> declared, string? name)
    {
        if (declared.Count == 0) return null;

        // Name match first — but ONLY as an addition. BC sets Name for its own reasons and
        // it does not always correspond to a declared layout (a default/placeholder
        // selection, a runtime-registered template, a tenant override). Treating a
        // non-matching name as "not ours" removed hydration the single-layout rule below
        // was already providing correctly, and cost 59 Pageworks tests — so a failed name
        // match must fall through, never short-circuit.
        if (!string.IsNullOrEmpty(name))
            foreach (var candidate in declared)
                if (string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))
                    return candidate;

        // No usable name — this is the report's DEFAULT layout render (a plain
        // Report.SaveAs with no SetTempLayoutSelectedName). One declared layout is
        // unambiguous on its own.
        if (declared.Count == 1) return declared[0];

        // With several, the answer is NOT a guess: AL requires any report using the
        // `rendering` syntax to declare `DefaultRenderingLayout`, and the compile-time
        // capture records which layout that names. Resolving to it is reading the AL
        // author's own declaration, exactly as a live tier would.
        //
        // This branch used to return null. That left the default render with a null
        // layout stream, so BC's custom-merge path handed the AL subscriber an EMPTY
        // template and the render produced a zero-page document — measured as 15 failing
        // Pageworks tests ("Expected at least 3 pages, got 0"), every one of them on the
        // default-layout path while its by-name siblings passed.
        AlReportLayoutInfo? theDefault = null;
        foreach (var candidate in declared)
        {
            if (!candidate.IsDefault) continue;
            // AL cannot express two defaults; seeing two means the capture is wrong, and
            // ambiguous is still ambiguous. Fall back to no-guessing rather than pick one.
            if (theDefault != null) return null;
            theDefault = candidate;
        }
        return theDefault;
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
        _pName = layoutType.GetProperty("Name", Any)
            ?? throw new InvalidOperationException(
                "[ReportLayout] ReportLayout.Name not found — BC metadata shape changed.");

        _bound = true;
    }
}

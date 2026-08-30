namespace AlRunner.Patches;

/// <summary>
/// The two gating rules behind the TestPage new-row line (issue #2089), kept apart from
/// LiveNavTestPage so they can be exercised without a loaded BC runtime.
///
/// An editable, insert-allowed repeater carries a trailing BLANK line past its data — the
/// line a user types into to create a record. BC's client appends it in
/// <c>DraftLinePattern.MakeDraftLines</c>, and it is part of the rowset the client hands the
/// test framework, so <c>TestPage.Next()</c> walks onto it and answers true.
///
/// Both rules are measured against a real service tier by corpus codeunit 60743, not derived:
/// a page opened with OpenView, a page declaring <c>Editable = false</c>, and a page declaring
/// <c>InsertAllowed = false</c> all answer false to that last <c>Next()</c>, and a part on a
/// read-only host does too.
///
/// Merged upstream as StefanMaron/BusinessCentral.AL.Language.Tests commit a5576344 (PR #76);
/// all nine arms passed on BOTH service-tier legs, BC 27.5 and BC 28.3.
///
/// ResolveStaticEditable's handler-driven case is measured by a DIFFERENT corpus suite in a
/// different PR, and the two should not be collapsed: codeunit 60743 above pins what the
/// editability answer DOES to the new-row line, while codeunit 60747 (commit 72281941, PR #77)
/// pins the page-level <c>TestPage.Editable()</c> answer itself. See ResolveStaticEditable.
/// </summary>
internal static class TestPageNewRowLineRule
{
    /// <summary>
    /// A page's STATIC editability.
    ///
    /// <paramref name="openModeEditable"/> is non-null only for a page the TEST opened, where
    /// the open mode (OpenEdit vs OpenView) decides the answer and has already been combined
    /// with the page's own <c>Editable</c>. It is null for every page BC hands to a
    /// [ModalPageHandler] / [PageHandler] and for every subpage part — those used to fall back
    /// to a flat "editable", which is what made an <c>Editable = false</c> page opened through
    /// RunModal report itself editable. That fallback is measured, not inferred from the rule
    /// BC applies to a page the test opens: corpus codeunit 60747 (StefanMaron/
    /// BusinessCentral.AL.Language.Tests commit 72281941, PR #77) reads
    /// <c>TestPage.Editable()</c> inside a [ModalPageHandler] and passed on BOTH BC 27.5 and
    /// BC 28.3 — false for a page declaring <c>Editable = false</c>, true for one declaring no
    /// <c>Editable</c> property at all.
    ///
    /// <paramref name="hostStaticEditable"/> is the host's answer for a subpage part, null for
    /// a top-level page. A part is only editable if the page hosting it is.
    /// </summary>
    internal static bool ResolveStaticEditable(bool? openModeEditable, bool? hostStaticEditable, bool pageEditable)
        => openModeEditable ?? ((hostStaticEditable ?? true) && pageEditable);

    /// <summary>
    /// Whether the page shows the implicit new-row line. BOTH conditions gate it, and each was
    /// measured separately — neither alone reproduces real BC's answers.
    /// </summary>
    internal static bool ShowsNewRowLine(bool staticEditable, bool insertAllowed)
        => staticEditable && insertAllowed;
}

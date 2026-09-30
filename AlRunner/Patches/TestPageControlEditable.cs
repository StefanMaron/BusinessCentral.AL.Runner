// TestPageControlEditable — what TestField.Editable() answers on a live TestPage.
using System;

namespace AlRunner;

/// <summary>
/// A Rec-bound control's <c>Editable()</c> is its own property, narrowed by the page's
/// editability, AND by the mode the test put the page in. <c>RunnerPageInstance.ControlEditable</c>
/// covers the first two; the open mode lives on the TestPage
/// (<c>LiveNavTestPage.OpenModeEditable</c>). Corpus codeunit 68015 "RSV Tests" (issue #5002):
/// OpenView, and the built-in View action on an OpenEdit card, make Rec-bound controls read-only,
/// while a PAGE-VARIABLE control on the same OpenView card stays editable — so only
/// <c>LiveNavTestField</c> is wired to this.
/// </summary>
internal static class TestPageControlEditable
{
    /// <summary>
    /// Trap: <paramref name="controlEditable"/> is evaluated by the caller before this runs, on
    /// purpose — a control whose property the runner cannot resolve refuses there, and a
    /// read-only open mode must not short-circuit past that refusal.
    /// </summary>
    internal static bool Combine(Func<bool>? openModeEditable, bool controlEditable)
        => controlEditable && (openModeEditable?.Invoke() ?? true);

    /// <summary>
    /// The open mode a page's Rec-bound controls follow. The mode the test set wins; a page with
    /// none follows its host when it is a part, and is read-only when run with
    /// <c>LookupMode(true)</c>. Corpus codeunit 68024 "CER Tests" (#5012): an <c>OpenView</c>
    /// host, or the host's View action, makes a part's Rec-bound control read-only, and so does
    /// lookup mode on a list or a card handed to a <c>[ModalPageHandler]</c>.
    /// </summary>
    internal static bool OpenMode(bool? testOpenMode, bool? hostOpenMode, bool lookupMode)
        => testOpenMode ?? ((hostOpenMode ?? true) && !lookupMode);

    /// <summary>
    /// Whether <c>LookupMode(true)</c> makes the page read-only, every control included. It does
    /// on a List, and not on a Card, a Document, a ListPlus or a Worksheet (corpus 68024
    /// "CER Tests", #5012; the List half also corpus 60309). A page whose type is unknown keeps
    /// the read-only answer the runner gave every lookup page before the type was measured.
    /// </summary>
    internal static bool LookupReadOnly(bool lookupMode, string? pageType)
        => lookupMode && (pageType == null || string.Equals(pageType, "List", StringComparison.OrdinalIgnoreCase));
}

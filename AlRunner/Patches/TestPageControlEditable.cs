// TestPageControlEditable — what TestField.Editable() answers on a live TestPage.
using System;

namespace AlRunner;

/// <summary>
/// A control's <c>Editable()</c> is its own property, narrowed by the page's editability, AND
/// by the mode the test put the page in. <c>RunnerPageInstance.ControlEditable</c> covers the
/// first two; the open mode lives on the TestPage (<c>LiveNavTestPage.OpenModeEditable</c>), so
/// a card opened with <c>OpenView()</c> answered <c>Editable() = No</c> for the page and Yes for
/// its fields. Measured on a service tier by corpus codeunit 68015 "RSV Tests" (issue #5002):
/// OpenView, and the built-in View action on an OpenEdit card, make the controls read-only too.
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
}

using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Patches;

internal sealed partial class RunnerPageInstance
{
    /// <summary>
    /// The control's CaptionClass caption, or null when the control declares no CaptionClass.
    ///
    /// <para>No new behaviour: the compiled page registers every CaptionClass control through
    /// BC's own <c>NavForm.RegisterDynamicCaptionExpression(controlId)</c>, which files a
    /// <c>Control{id}_DynamicCaption</c> source expression whose getter is
    /// <c>NavForm.GetDynamicCaptionAsync</c> — the page's own <c>EvaluateCaptionClass</c>
    /// followed by <c>UIHelperTriggers.InvokeCaptionClassTranslateAsync</c>, i.e. the System
    /// Application resolver and its <c>OnResolveCaptionClass</c> event. Reading that expression
    /// is the client's read. Corpus codeunit 60930 pins the answers (#4638).</para>
    ///
    /// <para>Trap: a failure here must propagate. Falling back to the field caption is the
    /// defect #4638 fixed, and a swallowed exception would reinstate it silently.</para>
    /// </summary>
    internal string? TryGetControlCaptionClass(int controlId)
    {
        var expression = _sourceExpressions[DynamicCaptionExpressionKey(controlId)];
        if (expression == null) return null;
        // An empty translation falls back to the static caption chain; no corpus test pins that
        // arm yet.
        var caption = GetValue(expression)?.ToString();
        return string.IsNullOrEmpty(caption) ? null : caption;
    }

    /// <summary>BC's own key, <c>NavForm.GetDynamicCaptionExpression</c>.</summary>
    internal static string DynamicCaptionExpressionKey(int controlId)
        => "Control" + controlId.ToString(System.Globalization.CultureInfo.InvariantCulture) + "_DynamicCaption";
}

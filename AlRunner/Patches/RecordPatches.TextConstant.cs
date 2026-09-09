// RecordPatches.TextConstant — NavTextConstant.get_Value and the NavStringValue conversion
// every AL Label read goes through.
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using AlRunner.Infrastructure;
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Patches;
public static partial class RecordPatches
{
    // ------------------------------------------------------------------
    // NavTextConstant.get_Value — sync underbelly hit by every NavText(constant) ctor
    // ------------------------------------------------------------------
    //
    // The real getter chains through `NavCurrentThread.ResolveAppGroup().GroupId` and
    // `NavCurrentThread.Session.LocalLanguage / GlobalLanguage` to pick a language.
    // NavCurrentThread.Session is null on the skeleton thread → NRE on every read of a
    // NavTextConstant (which the AL emitter generates for every Label, including the
    // five `TestFieldValidationCodeTxt`/`TestFieldCodeTxt`/etc. used by Assert codeunit).
    //
    // Empirically this is what causes `Assert.ExpectedTestFieldError` to NRE in Release
    // mode: the AL-emit OnRun has expressions like
    //     `NavTextExtensions.ALContains(this.lastErrorCode, new NavText(testFieldValidationCodeTxt))`
    // and the `new NavText(constant)` invokes the implicit `NavStringValue → string`
    // conversion which calls `NavTextConstant.Value` which NREs. Debug-mode emit happens
    // to evaluate these in a different order that hides the NRE; Release-mode does not.
    //
    // Replace with a skeleton-safe lookup: pick the first English (LCID 1033) entry, or
    // the first non-default entry, or empty. AL ships single-language ENU labels in v2,
    // so the result is byte-identical to what the real getter returns under normal
    // session state (LocalLanguage = 1033, fallback = 1033).

    private static FieldInfo? _fNavTextConstant_multiLanguage;
    private static FieldInfo? _fMultiLanguage_languageIds;
    private static FieldInfo? _fMultiLanguage_texts;

    /// <summary>
    /// Replacement for NavStringValue.op_Implicit(NavStringValue → string). Original is
    /// `value?.Value` — but `Value` on NavTextConstant NREs through NavCurrentThread.Session.
    /// Route through our skeleton-safe NavTextConstant_get_Value when the input is a
    /// NavTextConstant; otherwise read Value normally (other subtypes don't NRE).
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, PropertyInfo?> _pValueByType = new();

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string NavStringValue_op_Implicit(object? value)
    {
        if (value == null) return null!;
        var t = value.GetType();
        if (t.Name == "NavTextConstant")
            return NavTextConstant_get_Value(value);
        var prop = _pValueByType.GetOrAdd(t,
            x => x.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance));
        return prop?.GetValue(value) as string ?? string.Empty;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string NavTextConstant_get_Value(object self)
    {
        if (self == null) return string.Empty;
        try
        {
            if (_fNavTextConstant_multiLanguage == null)
                _fNavTextConstant_multiLanguage = self.GetType().GetField("multiLanguage",
                    BindingFlags.NonPublic | BindingFlags.Instance);
            var ml = _fNavTextConstant_multiLanguage?.GetValue(self);
            if (ml == null) return string.Empty;
            if (_fMultiLanguage_languageIds == null)
                _fMultiLanguage_languageIds = ml.GetType().GetField("languageIds",
                    BindingFlags.NonPublic | BindingFlags.Instance)
                    ?? ml.GetType().GetField("LanguageIds",
                        BindingFlags.NonPublic | BindingFlags.Instance);
            if (_fMultiLanguage_texts == null)
                _fMultiLanguage_texts = ml.GetType().GetField("texts",
                    BindingFlags.NonPublic | BindingFlags.Instance)
                    ?? ml.GetType().GetField("Texts",
                        BindingFlags.NonPublic | BindingFlags.Instance);
            // Try LanguageIds/Texts as properties (the public API on MultiLanguage).
            var langProp = ml.GetType().GetProperty("LanguageIds",
                BindingFlags.Public | BindingFlags.Instance);
            var textProp = ml.GetType().GetProperty("Texts",
                BindingFlags.Public | BindingFlags.Instance);
            var langs = langProp?.GetValue(ml) ?? _fMultiLanguage_languageIds?.GetValue(ml);
            var texts = textProp?.GetValue(ml) ?? _fMultiLanguage_texts?.GetValue(ml);
            if (langs is System.Collections.IList ll && texts is System.Collections.IList tl && ll.Count > 0 && tl.Count > 0)
            {
                // Prefer English (1033) first, then any non-default.
                for (int i = 0; i < ll.Count && i < tl.Count; i++)
                {
                    if (ll[i] is int lcid && lcid == 1033 && tl[i] is string s && !string.IsNullOrEmpty(s))
                        return s;
                }
                if (tl[0] is string first) return first;
            }
        }
        catch { }
        return string.Empty;
    }
}

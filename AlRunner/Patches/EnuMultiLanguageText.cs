using Microsoft.Dynamics.Nav.Types.Metadata;

namespace AlRunner.Patches;

/// <summary>
/// The runner's one-language (ENU) captions and texts, built and read through BC's own
/// <c>MultiLanguage</c> rather than by string concatenation or splitting. BC's parser ends an
/// unquoted value at the first <c>;</c> and reads a value opening with <c>"</c> as a quoted one,
/// and BC's serializer quotes exactly those, so a hand-built <c>"ENU=" + text</c> loses the text
/// after a <c>;</c> and a hand-split read keeps BC's quotes (#4640, #4282).
/// </summary>
internal static class EnuMultiLanguageText
{
    internal const int EnuLanguageId = 1033;

    /// <summary>A MultiLanguage holding <paramref name="text"/> as ENU, built with no parse.</summary>
    internal static MultiLanguage From(string text) => MultiLanguage.From(EnuLanguageId, text);

    /// <summary><paramref name="text"/> as the <c>*ML</c> value BC's emitter writes, quoted where BC quotes.</summary>
    internal static string ToMultiLanguageString(string text)
        => MultiLanguageExtensions.ToMultiLanguageString(From(text));

    /// <summary>
    /// The ENU text of a BC <c>*ML</c> value parsed by BC's own parser; when it states no ENU entry,
    /// the first entry if <paramref name="firstIfNoEnu"/>, else null. Null for a value with no entries.
    /// </summary>
    internal static string? ReadEnu(string multiLanguageString, bool firstIfNoEnu)
    {
        var ml = MultiLanguage.Parse(multiLanguageString);
        var ids = ml.LanguageIds;
        var texts = ml.Texts;
        for (int i = 0; i < ids.Count && i < texts.Count; i++)
            if (ids[i] == EnuLanguageId) return texts[i];
        return firstIfNoEnu && texts.Count > 0 ? texts[0] : null;
    }
}

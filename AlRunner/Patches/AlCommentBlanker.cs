// AlCommentBlanker — blanks out AL comments in raw .al source before the regex-based
// parsers run over it.
//
// The parsers in RecordPatches.Al*Parser.cs match property names with plain regexes
// (`\bInitValue\s*=\s*([^;]+);` and friends) against raw file text. A comment is just text
// to a regex, so a perfectly ordinary explanatory comment gets read as a property (#1690):
//
//     field(2; Accept; Boolean)
//     {
//         // InitValue = true: new rows default to accepted. A consumer filters on
//         // Accept = true before writing anything.
//         InitValue = true;
//     }
//
// `RxInitValue` matched inside the comment first — `[^;]+` ran to the semicolon four words
// later — so the field's InitValue became the comment prose, the real `InitValue = true;`
// below was never reached, and every Insert of the table died with
// `NavNCLEvaluateException: The value "true: new rows default to accepted. ..." can not be
// evaluated into type Boolean`. Real BC compiles both variants identically, of course:
// comments are not properties.
//
// Comments are replaced with spaces rather than removed so every character index in the
// returned string still lines up with the original. The table parser slices between
// regex-match offsets, so a length-preserving transform keeps that arithmetic — and any
// future offset-based logic — correct by construction.
//
// String-literal aware in both directions: `'...'` (with AL's doubled-quote escape) and
// `"..."` quoted identifiers are copied through untouched, so a caption like
// `Caption = 'see http://example.com';` does not lose its tail. If the source has an
// unterminated literal (not valid AL), the scan simply stays inside it and the text is
// returned unchanged — the pre-existing behaviour, never something worse.
namespace AlRunner.Patches;

internal static class AlCommentBlanker
{
    internal static string Blank(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        // Cheap bail-out for the overwhelmingly common case of source with no comment
        // markers at all — no allocation, no scan bookkeeping.
        if (text.IndexOf("//", StringComparison.Ordinal) < 0 &&
            text.IndexOf("/*", StringComparison.Ordinal) < 0)
            return text;

        var buf = new char[text.Length];
        text.CopyTo(0, buf, 0, text.Length);

        const int Code = 0, SingleQuote = 1, DoubleQuote = 2, LineComment = 3, BlockComment = 4;
        int state = Code;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            char next = i + 1 < text.Length ? text[i + 1] : '\0';

            switch (state)
            {
                case Code:
                    if (c == '\'') state = SingleQuote;
                    else if (c == '"') state = DoubleQuote;
                    else if (c == '/' && next == '/') { state = LineComment; buf[i] = buf[i + 1] = ' '; i++; }
                    else if (c == '/' && next == '*') { state = BlockComment; buf[i] = buf[i + 1] = ' '; i++; }
                    break;

                case SingleQuote:
                    // '' is AL's escape for a literal quote: closing then re-opening the
                    // literal lands back in this same state, so no special case is needed.
                    if (c == '\'') state = Code;
                    break;

                case DoubleQuote:
                    if (c == '"') state = Code;
                    break;

                case LineComment:
                    if (c == '\n') state = Code;   // keep the newline itself
                    else if (c != '\r') buf[i] = ' ';
                    break;

                case BlockComment:
                    if (c == '*' && next == '/') { state = Code; buf[i] = buf[i + 1] = ' '; i++; }
                    else if (c != '\n' && c != '\r') buf[i] = ' ';
                    break;
            }
        }

        return new string(buf);
    }
}

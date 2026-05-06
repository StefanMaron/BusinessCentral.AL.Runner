// Helper for issue #1603 — Format(Date) default locale format vs. Format(Date, 0, 9) XML format.
codeunit 1320210 "Date Default Format Helper"
{
    /// <summary>
    /// Returns Format(d) — the default (locale) date format.
    /// In BC this produces the session locale string, e.g. '12/31/2026' for en-US.
    /// </summary>
    procedure DefaultFormat(d: Date): Text
    begin
        exit(Format(d));
    end;

    /// <summary>
    /// Returns Format(d, 0, 9) — BC format number 9 = XML Standard = ISO 8601.
    /// Must always produce 'yyyy-MM-dd' regardless of session locale.
    /// </summary>
    procedure XmlFormat(d: Date): Text
    begin
        exit(Format(d, 0, 9));
    end;

    /// <summary>
    /// Returns Format(d, 0, '<Year4>-<Month,2>-<Day,2>') — explicit format string.
    /// Must produce 'yyyy-MM-dd'. Used to verify the string-format path is unaffected.
    /// </summary>
    procedure ExplicitIsoFormat(d: Date): Text
    begin
        exit(Format(d, 0, '<Year4>-<Month,2>-<Day,2>'));
    end;
}

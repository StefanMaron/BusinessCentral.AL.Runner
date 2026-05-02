/// Regression fixture for issue #1577.
///
/// In real-world usage "No. Series" is BaseApp Codeunit 310, auto-stubbed by the runner
/// when present in an .app package. The bug triggered when args [NavCode, NavDate]
/// were dispatched against a set of 2-param overloads on the auto-stub and
/// ScoreMethodMatch tied at score 11 — causing AreRelated(NavCode, NavCode) to win
/// over GetNextNo(MockVariant, NavDate) by reflection enumeration order.
///
/// This fixture compiles the multi-overload pattern from source so the test suite
/// can run without a BaseApp package — exercising the same runtime dispatch path
/// that fails in production.
///
/// The codeunit is given ID 310 to match the real No. Series codeunit so that the
/// runtime dispatch code path is identical.
codeunit 310 "No. Series"
{
    /// Simulates AreRelated(Code[20], Code[20]) — the WRONG 2-arg overload that was
    /// winning the score tie in the bug (its first param NavCode matched the first arg
    /// exactly at +10, then second param NavCode got +1 for the NavDate arg = 11).
    procedure AreRelated(NoSeriesCode: Code[20]; NoSeriesCode2: Code[20]): Boolean
    begin
        exit(false);
    end;

    /// Simulates GetNextNo(Code[20]) — 1-arg overload.
    procedure GetNextNo(NoSeriesCode: Code[20]): Code[20]
    begin
        exit('');
    end;

    /// Simulates GetNextNo(Code[20], Date) — the 2-arg overload that SHOULD win for
    /// args [NavCode, NavDate]. This is the exact #1577 regression test.
    procedure GetNextNo(NoSeriesCode: Code[20]; UsageDate: Date): Code[20]
    begin
        exit('');
    end;

    /// Simulates GetNextNo(Code[20], Date, Boolean) — 3-arg overload.
    procedure GetNextNo(NoSeriesCode: Code[20]; UsageDate: Date; HideErrorsAndWarnings: Boolean): Code[20]
    begin
        exit('');
    end;

    /// Simulates LookupRelatedNoSeries(Code[20], var Code[20]) — another 2-param
    /// overload that previously tied with GetNextNo at score 11 and now should score
    /// lower because its second param is ByRef<NavCode>, not NavDate.
    procedure LookupRelatedNoSeries(NoSeriesCode: Code[20]; var RelatedNoSeriesCode: Code[20])
    begin
    end;
}

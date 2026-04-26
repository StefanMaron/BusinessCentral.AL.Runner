/// Exercises Version.Create with string literals — the pattern that triggered
/// CS1503: 'string' → 'NavText' in telemetry issue #1322.
/// BC emits string literals as C# string, not NavText, when they appear
/// directly in Version.Create() calls inline (not via a Text variable).
codeunit 306000 "Version Create String Src"
{
    /// Returns true when ApiVersion (Text) <= the literal threshold.
    /// This reproduces the exact pattern from the telemetry report:
    ///   Version.Create(ApiVersion) <= Version.Create('25.0.0.0')
    procedure IsVersionAtOrBelow(ApiVersion: Text; Threshold: Text): Boolean
    var
        Actual: Version;
        Limit: Version;
    begin
        Actual := Version.Create(ApiVersion);
        Limit := Version.Create(Threshold);
        exit(Actual <= Limit);
    end;

    /// Inline comparison with a string literal on the right side:
    /// Version.Create(v) <= Version.Create('1.0.0.0')
    procedure IsAtOrBelow1000(v: Text): Boolean
    begin
        exit(Version.Create(v) <= Version.Create('1.0.0.0'));
    end;

    /// Inline comparison with a string literal on the left side:
    /// Version.Create('1.0.0.0') <= Version.Create(v)
    procedure IsAbove1000(v: Text): Boolean
    begin
        exit(Version.Create('1.0.0.0') < Version.Create(v));
    end;

    /// Returns the major component after creating from a string literal.
    procedure MajorFromLiteral(): Integer
    var
        V: Version;
    begin
        V := Version.Create('25.3.10000.5');
        exit(V.Major());
    end;
}

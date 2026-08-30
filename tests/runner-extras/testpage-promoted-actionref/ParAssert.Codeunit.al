/// Thin standalone assert helper — no dependency on Library Assert, mirroring the
/// pageext-action-dispatch / dep-tableext-platform-base suites' own local wrapper convention.
codeunit 64544 "Par Assert"
{
    procedure IsTrue(Value: Boolean; Msg: Text)
    begin
        if not Value then
            Error('Assert.IsTrue failed: %1', Msg);
    end;

    procedure IsFalse(Value: Boolean; Msg: Text)
    begin
        if Value then
            Error('Assert.IsFalse failed: %1', Msg);
    end;

    procedure Contains(Actual: Text; Fragment: Text; Msg: Text)
    begin
        if StrPos(Actual, Fragment) = 0 then
            Error('Assert.Contains failed: %1 (expected a fragment ''%2'' in ''%3'')', Msg, Fragment, Actual);
    end;
}

/// <summary>
/// Minimal assertion helper for this fixture app (own ID range so it
/// stands alone from the corpus Assert).
/// </summary>
codeunit 70026 "WPMR Assert"
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
}

/// <summary>
/// Probe for issue #1250: HttpContent.ReadAs(var Text) in an 'if' condition.
///
/// BC emits: if CStmtHit(N) &amp; (content.ALReadAs(DataError.TrapError, ByRef&lt;NavText&gt;))
/// MockHttpContent.ALReadAs was void — so '&amp;' between bool and void caused CS0019.
/// The fix: ALReadAs returns bool (true = success, matching the real BC API).
///
/// ReadAsConditional() exercises the exact pattern that triggered CS0019.
/// GetProbeVersion() is a pure-logic sentinel called by tests to confirm compilation.
/// </summary>
codeunit 1250 "HTTP ReadAs Bool Probe"
{
    /// <summary>
    /// Uses HttpContent.ReadAs(var Text) as the condition of an 'if' statement.
    /// BC emits this as: CStmtHit(N) &amp; (content.ALReadAs(DataError.TrapError, ByRef&lt;NavText&gt;))
    /// which fails with CS0019 when ALReadAs returns void.
    /// Never called by tests — HTTP is not available in standalone mode.
    /// </summary>
    procedure ReadAsConditional(var Content: HttpContent): Text
    var
        ResponseBodyText: Text;
    begin
        if Content.ReadAs(ResponseBodyText) then
            exit(ResponseBodyText);
        exit('');
    end;

    /// <summary>
    /// Pure-logic sentinel with no HTTP variables.
    /// Tests call this to confirm the codeunit compiled without CS0019.
    /// </summary>
    procedure GetProbeVersion(): Integer
    begin
        exit(1250);
    end;
}

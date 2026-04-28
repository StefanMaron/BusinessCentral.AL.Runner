/// Tests for HttpClient.UseDefaultNetworkWindowsAuthentication (issue #1532).
///
/// BC's HttpClient.UseDefaultNetworkWindowsAuthentication() is a 0-arg method
/// (a setter-style call with no boolean parameter in AL).
/// MockHttpClient had it as a property — BC's emit calls it as a method,
/// causing CS1955: Non-invocable member cannot be used like a method.
codeunit 1320412 "HC UseDefaultNetwork Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    // ── UseDefaultNetworkWindowsAuthentication ───────────────────────────────

    /// Positive: Calling UseDefaultNetworkWindowsAuthentication() must not throw.
    [Test]
    procedure UseDefaultNetwork_Call_NoError()
    var
        client: HttpClient;
    begin
        // BC emits: client.ALUseDefaultNetworkWindowsAuthentication(DataError)
        // as a method call (0 AL args). Was a property — CS1955.
        client.UseDefaultNetworkWindowsAuthentication();
        // [THEN] No error — method call succeeds
    end;
}

/// <summary>
/// Proves the tableextension field OnValidate trigger added by THIS bundle
/// runs even when server-reload-dep's "SRW Item" table was already recorded
/// wired by an EARLIER --server request that loaded the dep alone (issue
/// #1860). Not exercised by an ordinary single-shot bundle run — see
/// scripts/tests/server-reload-test.sh, which drives the two-request
/// sequence over a real --server session; the load-bearing assertion is
/// there, not in an ordinary CLI bundle run of this pair.
/// </summary>
codeunit 64453 "SRW Reload Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "SRW Assert";

    [Test]
    procedure ExtensionField_Validate_RunsOnValidateTrigger()
    var
        Item: Record "SRW Item";
    begin
        Item.Init();
        Item."Code" := 'RELOAD1';
        Item.Insert();

        Item.Validate("Extra", 'payload');

        Assert.AreEqualText(
            'validated:payload', Item.Log,
            'OnValidate on the tableextension-added field "Extra" must run and set Log');
    end;
}

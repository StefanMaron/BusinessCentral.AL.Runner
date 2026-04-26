codeunit 311001 "HCA Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "HCA Src";

    // ── Positive: assignment copies state ──────────────────────────────

    [Test]
    procedure HttpClient_Assign_CopiesBaseAddress()
    begin
        // Positive: source base address must appear on target after assignment
        Assert.AreEqual('https://api.example.com',
            Src.ConfigureAndAssign('https://api.example.com'),
            'Target GetBaseAddress must equal source after := assignment');
    end;

    [Test]
    procedure HttpClient_Assign_BaseAddress_IsDistinctForDifferentSources()
    begin
        // Strengthen: two different source URLs produce different results
        Assert.AreNotEqual(
            Src.ConfigureAndAssign('https://a.example.com'),
            Src.ConfigureAndAssign('https://b.example.com'),
            'Different source base URLs must not collide after assignment');
    end;

    [Test]
    procedure HttpClient_Assign_CopiesDefaultRequestHeaders()
    begin
        // Positive: header added to source must be present on target after assignment
        Assert.IsTrue(
            Src.AssignCopiesHeaders('X-Test-Header', 'hello'),
            'DefaultRequestHeaders from source must be accessible on target after assignment');
    end;

    [Test]
    procedure HttpClient_Assign_EmptyClientBaseAddressIsEmpty()
    begin
        // Positive: assigning empty client gives empty base address on target
        Assert.AreEqual('',
            Src.AssignedEmptyClientBaseAddress(),
            'Target GetBaseAddress must be empty when source was never configured');
    end;

    // ── Self-assign / stability ────────────────────────────────────────

    [Test]
    procedure HttpClient_SelfAssign_PreservesBaseAddress()
    begin
        // Negative-flavoured: self-assign must not corrupt state
        Assert.AreEqual('https://self.example.com',
            Src.SelfAssign('https://self.example.com'),
            'Self-assignment must preserve the stored base address');
    end;

    // ── var-parameter (ConfigureServerCertificateValidation pattern) ───

    [Test]
    procedure HttpClient_AssignAndCallByVar_DoesNotThrow()
    begin
        // Exact trigger from issue #1447: assign then pass by var — must not throw
        Assert.IsTrue(
            Src.AssignAndCallByVar('https://api.example.com'),
            'Assigning an HttpClient and passing the result by var must not throw');
    end;
}

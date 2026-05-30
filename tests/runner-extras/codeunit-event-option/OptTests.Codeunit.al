/// <summary>
/// Regression proof that a codeunit IntegrationEvent carrying an Option
/// argument is marshalled to a subscriber whose parameter is Option-typed.
///
/// Before the fix the CodeunitEventDispatcher passed the publisher's NavOption
/// scope-field value straight into reflection's MethodInfo.Invoke against the
/// subscriber's Int32 parameter slot, throwing
/// 'Object of type NavOption cannot be converted to type System.Int32'.
///
/// The subscriber raises Error('RECEIVED:%1', Choice) so the marshalled ordinal
/// is directly observable here via asserterror — no SingleInstance state sharing
/// required.
/// </summary>
codeunit 60354 "Opt Tests CEO"
{
    Subtype = Test;

    var
        Assert: Codeunit "Opt Assert CEO";

    [Test]
    procedure CodeunitEventOptionArg_MarshalsSecond()
    var
        Publisher: Codeunit "Opt Publisher CEO";
    begin
        // [WHEN] the publisher fires OnDoChoice(Second) — option ordinal 1
        asserterror Publisher.Fire(1);

        // [THEN] the subscriber fired and received the Option value, ordinal 1
        Assert.AreEqual('RECEIVED:Second', GetLastErrorText(),
            'Subscriber must receive the correct Option ordinal (Second = 1)');
    end;

    [Test]
    procedure CodeunitEventOptionArg_MarshalsThird()
    var
        Publisher: Codeunit "Opt Publisher CEO";
    begin
        // [WHEN] firing with ordinal 2 (Third)
        asserterror Publisher.Fire(2);

        Assert.AreEqual('RECEIVED:Third', GetLastErrorText(),
            'Subscriber must receive the correct Option ordinal (Third = 2)');
    end;

    [Test]
    procedure CodeunitEventOptionArg_MarshalsFirst()
    var
        Publisher: Codeunit "Opt Publisher CEO";
    begin
        // Negative-direction guard: ordinal 0 must NOT be masked as a default.
        // The subscriber error must carry RECEIVED:First (the real ordinal),
        // proving the marshalled value is genuinely passed through, and must
        // NOT equal the other ordinals.
        asserterror Publisher.Fire(0);

        Assert.AreEqual('RECEIVED:First', GetLastErrorText(),
            'Subscriber must receive ordinal 0 (First) — not a masked default');
    end;
}

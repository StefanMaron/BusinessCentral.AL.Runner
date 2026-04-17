/// Proves ErrorInfo.Create() (0-arg static factory) and the Message property/method
/// surface work in the runner.
///
/// Gaps NOT covered here (BC 17+ / BC 16.2 AL compiler limitation):
///   ErrorInfo.Create(Message: Text)      — 1-arg overload
///   ErrorInfo.Create(Message, ErrorType) — 2-arg overload
///   ErrorInfo.ErrorType                  — getter/setter (ErrorType enum is BC 17+)
codeunit 97901 "EIC Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "EIC Src";

    // ------------------------------------------------------------------
    // ErrorInfo.Create() — 0-arg static factory
    // ------------------------------------------------------------------

    [Test]
    procedure Create_DefaultMessageIsEmpty()
    begin
        // [GIVEN] ErrorInfo.Create() with no arguments
        // [WHEN]  Message is read back
        // [THEN]  Returns empty string (fresh default)
        Assert.AreEqual('', Src.GetDefaultMessage(),
            'ErrorInfo.Create() default Message should be empty');
    end;

    [Test]
    procedure Create_MessagePropertyRoundtrip()
    begin
        // [GIVEN] ErrorInfo.Create() followed by Message := 'hello'
        // [WHEN]  Message property is read back
        // [THEN]  Returns 'hello'
        Assert.AreEqual('hello', Src.GetMessageFromCreated('hello'),
            'ErrorInfo.Create() + Message := ... should preserve the message');
    end;

    [Test]
    procedure Create_EmptyMessagePropertyRoundtrip()
    begin
        // Empty string is a valid message value
        Assert.AreEqual('', Src.GetMessageFromCreated(''),
            'ErrorInfo.Create() + Message := empty should round-trip empty');
    end;

    // ------------------------------------------------------------------
    // ErrorInfo.Message — method-form setter and getter
    // ------------------------------------------------------------------

    [Test]
    procedure MessageMethodSetter_ReturnsSetValue()
    begin
        // [GIVEN] ErrInfo.Message('world') — method-form setter
        // [WHEN]  Message property is read
        // [THEN]  Returns 'world'
        Assert.AreEqual('world', Src.SetMessageViaMethod('world'),
            'Message(text) method setter should be readable via Message property getter');
    end;

    [Test]
    procedure MessageMethodGetter_ReturnsPropertyValue()
    begin
        // [GIVEN] ErrInfo.Message := 'test' — property setter
        // [WHEN]  Message() method form is called
        // [THEN]  Returns 'test'
        Assert.AreEqual('test', Src.GetMessageViaMethodGetter('test'),
            'Message() method getter should return the same value as Message property');
    end;

    [Test]
    procedure MessageMethod_EmptyString()
    begin
        // Method form handles empty strings the same as non-empty
        Assert.AreEqual('', Src.SetMessageViaMethod(''),
            'Message(empty) setter should leave Message empty');
    end;
}

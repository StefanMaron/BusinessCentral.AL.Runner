codeunit 60251 "XDL Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "XDL Src";

    [Test]
    procedure Create_Version()
    begin
        Assert.AreEqual('1.0', Src.CreateAndGetVersion(),
            'XmlDeclaration.Version must return the supplied version');
    end;

    [Test]
    procedure Create_Encoding()
    begin
        Assert.AreEqual('utf-8', Src.CreateAndGetEncoding(),
            'XmlDeclaration.Encoding must return the supplied encoding');
    end;

    [Test]
    procedure Create_Standalone()
    begin
        Assert.AreEqual('yes', Src.CreateAndGetStandalone(),
            'XmlDeclaration.Standalone must return the supplied standalone flag');
    end;

    [Test]
    procedure SetVersion_RoundTrip()
    begin
        Assert.AreEqual('1.1', Src.SetVersion('1.1'),
            'Version setter must round-trip');
    end;

    [Test]
    procedure SetEncoding_RoundTrip()
    begin
        Assert.AreEqual('utf-16', Src.SetEncoding('utf-16'),
            'Encoding setter must round-trip');
    end;

    [Test]
    procedure SetStandalone_RoundTrip()
    begin
        Assert.AreEqual('no', Src.SetStandalone('no'),
            'Standalone setter must round-trip');
    end;

    [Test]
    procedure Version_NotEncoding_NegativeTrap()
    begin
        Assert.AreNotEqual(Src.CreateAndGetEncoding(), Src.CreateAndGetVersion(),
            'Version and Encoding must not alias');
    end;
}

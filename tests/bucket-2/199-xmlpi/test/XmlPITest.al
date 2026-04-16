codeunit 60261 "XPI Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "XPI Src";

    [Test]
    procedure Create_GetTarget()
    begin
        Assert.AreEqual('xml-stylesheet', Src.CreateAndGetTarget(),
            'XmlProcessingInstruction.GetTarget must return the created target');
    end;

    [Test]
    procedure Create_GetData()
    begin
        Assert.AreEqual('type="text/css"', Src.CreateAndGetData(),
            'XmlProcessingInstruction.GetData must return the created data');
    end;

    [Test]
    procedure SetTarget_RoundTrip()
    begin
        Assert.AreEqual('newTarget', Src.SetTargetAndRead('newTarget'),
            'SetTarget must update and round-trip through GetTarget');
    end;

    [Test]
    procedure SetData_RoundTrip()
    begin
        Assert.AreEqual('href="style.css"', Src.SetDataAndRead('href="style.css"'),
            'SetData must update and round-trip through GetData');
    end;

    [Test]
    procedure Target_NotData_NegativeTrap()
    begin
        Assert.AreNotEqual(Src.CreateAndGetTarget(), Src.CreateAndGetData(),
            'GetTarget and GetData must not alias');
    end;
}

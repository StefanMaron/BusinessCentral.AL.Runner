codeunit 84408 "Media Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "Media Src";

    [Test]
    procedure Media_PlaceholderTest()
    begin
        Assert.IsTrue(true, 'Placeholder test');
    end;

}

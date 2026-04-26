codeunit 60421 "CCT Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "CCT Src";

    [Test]
    procedure CaptionClassTranslate_ReturnsInput()
    begin
        // Standalone contract: no caption-class resolver available; return input unchanged.
        Assert.AreEqual('1,1,Sales', Src.TranslateCaption('1,1,Sales'),
            'CaptionClassTranslate must return the input unchanged in standalone mode');
    end;

    [Test]
    procedure CaptionClassTranslate_Empty_ReturnsEmpty()
    begin
        Assert.AreEqual('', Src.TranslateCaption(''),
            'CaptionClassTranslate on empty input must return empty');
    end;

    [Test]
    procedure CaptionClassTranslate_DoesNotThrow()
    begin
        Src.TranslateCaption('anything');
        Assert.IsTrue(true, 'CaptionClassTranslate must not throw');
    end;
}

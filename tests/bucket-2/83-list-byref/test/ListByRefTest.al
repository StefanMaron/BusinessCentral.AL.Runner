codeunit 59101 "List ByRef Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure TestJsonListByRef()
    var
        Items: List of [JsonObject];
        Processor: Codeunit "List Processor";
        JObj: JsonObject;
        JTok: JsonToken;
    begin
        // Positive: cross-codeunit var parameter populates the list
        Processor.BuildJsonList(Items);
        Assert.AreEqual(2, Items.Count(), 'Should have 2 JsonObject items after BuildJsonList');

        // Verify content of first item
        Items.Get(1, JObj);
        JObj.Get('name', JTok);
        Assert.AreEqual('Alice', JTok.AsValue().AsText(), 'First item name should be Alice');
    end;

    [Test]
    procedure TestJsonListByRefEmpty()
    var
        Items: List of [JsonObject];
    begin
        // Negative: list starts empty before any cross-codeunit call
        Assert.AreEqual(0, Items.Count(), 'New list should be empty');
    end;

    [Test]
    procedure TestTextListByRef()
    var
        Items: List of [Text];
        Processor: Codeunit "List Processor";
    begin
        // Positive: cross-codeunit var parameter with NavList<NavText>
        Processor.BuildTextList(Items);
        Assert.AreEqual(3, Items.Count(), 'Should have 3 text items after BuildTextList');
    end;

    [Test]
    procedure TestTextListByRefDefault()
    var
        Items: List of [Text];
    begin
        // Negative: list is empty without calling the builder
        Assert.AreEqual(0, Items.Count(), 'Default text list should be empty');
    end;

    [Test]
    procedure TestMixedParamsWithByRefList()
    var
        Items: List of [Text];
        Processor: Codeunit "List Processor";
    begin
        // Positive: var list as second param, non-var Text as first
        Items.Add('AB-one');
        Items.Add('CD-two');
        Items.Add('AB-three');
        Processor.FilterList('AB', Items);
        Assert.AreEqual(2, Items.Count(), 'Should have 2 items after filtering by prefix AB');
    end;

    [Test]
    procedure TestMixedParamsFilterRemovesAll()
    var
        Items: List of [Text];
        Processor: Codeunit "List Processor";
    begin
        // Negative: filtering with a prefix that matches nothing empties the list
        Items.Add('Hello');
        Items.Add('World');
        Processor.FilterList('ZZ', Items);
        Assert.AreEqual(0, Items.Count(), 'Should have 0 items after filtering by non-matching prefix');
    end;

    [Test]
    procedure TestOverloadedByRefOneArg()
    var
        Items: List of [JsonObject];
        Processor: Codeunit "List Processor";
    begin
        // Positive: call the 1-arg overload (var List only) — previously crashed
        // because MockCodeunitHandle.Invoke resolved the wrong overload for
        // the suffixed C# method name emitted by the BC compiler.
        Processor.AddToList(Items);
        Assert.AreEqual(1, Items.Count(), 'One-arg overload should add 1 default item');
    end;

    [Test]
    procedure TestOverloadedByRefTwoArgs()
    var
        Items: List of [JsonObject];
        Processor: Codeunit "List Processor";
        JObj: JsonObject;
    begin
        // Positive: call the 2-arg overload (JsonObject + var List)
        JObj.Add('custom', 'value');
        Processor.AddToList(JObj, Items);
        Assert.AreEqual(1, Items.Count(), 'Two-arg overload should add the given item');
    end;

    [Test]
    procedure TestOverloadedByRefBothCalled()
    var
        Items: List of [JsonObject];
        Processor: Codeunit "List Processor";
        JObj: JsonObject;
    begin
        // Negative: calling only the 1-arg overload should not produce 2 items
        Processor.AddToList(Items);
        Assert.AreNotEqual(2, Items.Count(), 'Single call should not produce 2 items');
    end;
}

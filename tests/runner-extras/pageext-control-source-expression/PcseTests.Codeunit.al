codeunit 65984 "Pcse Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "Pcse Assert";

    /// The failing arm of issue #4145. A control the pageextension declares, bound to a variable
    /// the PAGEEXTENSION itself declares, must be findable and must read the value the
    /// extension's OnOpenPage wrote. Before the fix this raised
    /// NavTestFieldNotFoundException ("The field with ID = ... is not found on the page"),
    /// because the extension's InitializeComponent -- where its source expressions register --
    /// never ran.
    [Test]
    procedure PcseExtControlBoundToExtensionGlobal_IsFoundAndReadsItsValue()
    var
        T: TestPage "Pcse Card";
    begin
        T.OpenEdit();
        Assert.AreEqual('ext', T.ExtGlobalField.Value(), 'extension-global-bound control');
        T.Close();
    end;

    /// The discriminator. Same addlast(Content) block, bound to a SourceTable field instead.
    /// This one resolved before the fix too, which is precisely what rules out "the page
    /// metadata does not carry the extension's controls" -- corpus 60978 measured BC finding
    /// both.
    [Test]
    procedure PcseExtControlBoundToRecField_IsFoundAndReadsItsValue()
    var
        Row: Record "Pcse Row";
        T: TestPage "Pcse Card";
    begin
        Row.DeleteAll();
        Row.Init();
        Row."Entry No." := 1;
        Row."Rec Text" := 'recval';
        Row.Insert();

        T.OpenEdit();
        Assert.AreEqual('recval', T.ExtRecField.Value(), 'extension Rec-bound control');
        T.Close();
    end;

    /// Issue #3228's other refused shape: a control bound to a Label the pageextension declares.
    [Test]
    procedure PcseExtControlBoundToExtensionLabel_IsFoundAndReadsItsValue()
    var
        T: TestPage "Pcse Card";
    begin
        T.OpenEdit();
        Assert.AreEqual('Ext label', T.ExtLabelField.Value(), 'extension Label-bound control');
        T.Close();
    end;

    /// The base page's own global-bound control still reads. A regression here would mean the
    /// widened guard broke the path it was meant to leave alone.
    [Test]
    procedure PcseBaseControlBoundToPageGlobal_StillReadable()
    var
        T: TestPage "Pcse Card";
    begin
        T.OpenEdit();
        Assert.AreEqual('base', T.BaseField.Value(), 'base page global-bound control');
        T.Close();
    end;
}

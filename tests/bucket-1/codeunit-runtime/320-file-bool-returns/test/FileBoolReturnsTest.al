/// Tests for File.Open / File.Create / File.Erase returning Boolean (issue #1530).
///
/// BC's File.Open and File.Erase return Boolean so they can be called in
/// boolean contexts: if not f.Open('x') then Error(...).
/// MockFile must match that signature; void-returning methods fail CS0023.
codeunit 1320410 "File Bool Returns Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    // ── File.Open bool return ────────────────────────────────────────────────

    /// Positive: Open returns a boolean that can be tested.
    [Test]
    procedure Open_ReturnsBool_IsTrue()
    var
        f: File;
        ok: Boolean;
    begin
        ok := f.Open('dummy.txt');
        // MockFile.ALOpen always succeeds (in-memory, no real FS) → returns true
        Assert.AreEqual(true, ok, 'File.Open must return Boolean true in stub');
    end;

    /// Positive: Open can be used directly in an if condition (boolean context).
    [Test]
    procedure Open_BoolContext_NoError()
    var
        f: File;
    begin
        // This pattern fails with CS0023 if ALOpen returns void.
        if not f.Open('dummy.txt') then
            Error('open should succeed in stub');
        // [THEN] No error raised — mock returned true
    end;

    // ── File.Erase bool return ────────────────────────────────────────────────

    /// Positive: Erase returns a boolean (no real FS, always succeeds stub-wise).
    [Test]
    procedure Erase_ReturnsBool_IsTrue()
    var
        ok: Boolean;
    begin
        ok := File.Erase('dummy.txt');
        Assert.AreEqual(true, ok, 'File.Erase must return Boolean true in stub');
    end;

    /// Positive: Erase in boolean context must compile and run.
    [Test]
    procedure Erase_BoolContext_NoError()
    begin
        // This pattern fails with CS0023 if ALErase returns void.
        if not File.Erase('nonexistent.txt') then
            Error('Erase should return true in stub');
        // [THEN] No error raised
    end;

    // ── File.Copy bool return ────────────────────────────────────────────────

    /// Positive: Copy returns a boolean.
    [Test]
    procedure Copy_ReturnsBool_IsTrue()
    var
        ok: Boolean;
    begin
        ok := File.Copy('a.txt', 'b.txt');
        Assert.AreEqual(true, ok, 'File.Copy must return Boolean true in stub');
    end;
}

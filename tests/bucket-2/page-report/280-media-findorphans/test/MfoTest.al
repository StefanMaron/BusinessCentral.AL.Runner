codeunit 127002 "MFO Test"
{
    Subtype = Test;
    var
        Assert: Codeunit Assert;

    // ── Media.FindOrphans ─────────────────────────────────────────────────────

    [Test]
    procedure Media_FindOrphans_ReturnsEmptyList()
    var
        Src: Codeunit "MFO Source";
        Orphans: List of [Guid];
    begin
        // Positive: FindOrphans returns an empty list in standalone mode (no real blob store).
        Orphans := Src.GetOrphanedMedia();
        Assert.AreEqual(0, Orphans.Count(), 'Media.FindOrphans must return empty list in runner');
    end;

    // ── MediaSet.FindOrphans ──────────────────────────────────────────────────

    [Test]
    procedure MediaSet_FindOrphans_ReturnsEmptyList()
    var
        Src: Codeunit "MFO Source";
        Orphans: List of [Guid];
    begin
        // Positive: MediaSet.FindOrphans returns an empty list in standalone mode.
        Orphans := Src.GetOrphanedMediaSet();
        Assert.AreEqual(0, Orphans.Count(), 'MediaSet.FindOrphans must return empty list in runner');
    end;
}

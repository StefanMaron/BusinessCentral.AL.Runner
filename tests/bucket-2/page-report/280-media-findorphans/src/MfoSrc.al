/// Source codeunit for Media/MediaSet.FindOrphans tests (issue #949).
codeunit 127001 "MFO Source"
{
    procedure GetOrphanedMedia(): List of [Guid]
    begin
        exit(Media.FindOrphans());
    end;

    procedure GetOrphanedMediaSet(): List of [Guid]
    begin
        exit(MediaSet.FindOrphans());
    end;
}

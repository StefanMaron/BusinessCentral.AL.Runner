/// Exercises MediaSet.FindOrphans() — static method returning List of [Guid].
codeunit 117001 "MSF Src"
{
    /// Returns the orphaned MediaSet GUIDs via the static MediaSet.FindOrphans() call.
    procedure GetOrphans(): List of [Guid]
    begin
        exit(MediaSet.FindOrphans());
    end;
}

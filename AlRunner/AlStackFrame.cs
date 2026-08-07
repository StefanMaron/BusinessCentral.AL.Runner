// Ported from protocol-v2 (v1 architecture, PR #1607) as part of #1641 — the type
// shapes are architecture-agnostic (plain records describing one AL stack frame /
// error-kind bucket for the streaming wire protocol), so they carry over unchanged.
namespace AlRunner;

public enum FramePresentationHint
{
    Normal,
    Subtle,
    Deemphasize,
    Label
}

public enum AlErrorKind
{
    Assertion,
    Runtime,
    Compile,
    Setup,
    Timeout,
    Unknown
}

public record AlStackFrame(
    string? File,
    int? Line,
    int? Column,
    bool IsUserCode,
    string? Name,
    FramePresentationHint Hint
);

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

namespace AlRunner.Runtime;

/// <summary>
/// Thrown when a codeunit or record type cannot be found in the compiled assembly
/// because it was excluded during Roslyn compilation (partial-compile fallback).
/// This is a tooling/configuration error, not a test assertion failure.
/// </summary>
public class CompilationExcludedException : Exception
{
    public CompilationExcludedException(string message) : base(message) { }
}

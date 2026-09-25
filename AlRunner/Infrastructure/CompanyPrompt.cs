namespace AlRunner.Infrastructure;

/// <summary>
/// Where an ambiguous <c>--test-data-company</c> prefix asks the user to pick (#4553). Null
/// means non-interactive: the run fails listing the candidates instead of asking.
/// </summary>
internal sealed record CompanyPrompt(TextReader In, TextWriter Out)
{
    /// <summary>Stdin and stdout both terminals; anything else (CI, a pipe, --server, a
    /// --jobs child) never prompts.</summary>
    internal static CompanyPrompt? FromConsole()
        => !Console.IsInputRedirected && !Console.IsOutputRedirected
            ? new CompanyPrompt(Console.In, Console.Error)
            : null;
}

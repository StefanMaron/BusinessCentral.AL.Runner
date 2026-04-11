using System.Text;

namespace AlRunner;

/// <summary>
/// Writes each line to two underlying TextWriters — used by Pipeline.Run
/// so Console.Error output is both captured for tests and still visible
/// on the real stderr when the pipeline is invoked from the CLI.
/// </summary>
internal sealed class TeeTextWriter : TextWriter
{
    private readonly TextWriter _a;
    private readonly TextWriter _b;

    public TeeTextWriter(TextWriter a, TextWriter b)
    {
        _a = a;
        _b = b;
    }

    public override Encoding Encoding => _a.Encoding;

    public override void Write(char value)
    {
        _a.Write(value);
        _b.Write(value);
    }

    public override void Write(string? value)
    {
        _a.Write(value);
        _b.Write(value);
    }

    public override void WriteLine(string? value)
    {
        _a.WriteLine(value);
        _b.WriteLine(value);
    }

    public override void Flush()
    {
        _a.Flush();
        _b.Flush();
    }
}

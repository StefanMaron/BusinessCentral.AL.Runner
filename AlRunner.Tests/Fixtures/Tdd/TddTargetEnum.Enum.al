/// <summary>
/// The "implementing app" enum half of the fixture. Deliberately does NOT declare an
/// Archived value yet — TddBrokenEnumTests.Codeunit.al references it as if it already
/// existed.
/// </summary>
enum 65003 "Tdd Target Enum"
{
    Extensible = true;

    value(0; Open) { }
}

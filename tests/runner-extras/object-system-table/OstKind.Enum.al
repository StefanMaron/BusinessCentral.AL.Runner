// An enum exists ONLY so a test can assert that an object kind the Object table's own option
// string cannot name gets no row. Object (2000000001) is the legacy object registry: its
// "Type" option is TableData,Table,,Report,,Codeunit,XMLport,MenuSuite,Page,Query,System,
// FieldNumber — there is no Enum member, so a name-matched mapping skips this object and an
// invented ordinal would not. Never referenced from AL code; its presence in the runner's
// object inventory is the whole point.
enum 65552 "OST Kind"
{
    Extensible = false;

    value(0; None) { }
}

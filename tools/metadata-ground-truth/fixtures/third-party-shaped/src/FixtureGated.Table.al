// Declared only under the manifest's <PreprocessorSymbols>. A compile that ignores them
// parses this file to nothing, and the bundle carries one table fewer than expected.json.
#if TPFIXTURE
table 70002 "TP Fixture Gated"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; Code; Code[10]) { }
    }

    keys
    {
        key(PK; Code) { Clustered = true; }
    }
}
#endif

codeunit 70720 "QMD Tests"
{
    Subtype = Test;

    // Opening each query is what forces its MetaQuery design to be built, which is what the
    // trace this fixture exists for reports on. The join behaviour itself is adjudicated on a
    // real service tier by corpus PR #297, not here.
    [Test]
    procedure OpensBothQueries()
    var
        Divergent: Query "QMD Divergent";
        Plain: Query "QMD Plain";
    begin
        Divergent.Open();
        Divergent.Close();

        Plain.Open();
        Plain.Close();
    end;
}

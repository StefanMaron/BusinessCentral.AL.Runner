codeunit 1320516 "CGA Worker"
{
    procedure GetValue(): Integer
    begin
        exit(42);
    end;
}

codeunit 1320517 "CGA Manager"
{
    procedure BuildList(): List of [Codeunit "CGA Worker"]
    var
        list: List of [Codeunit "CGA Worker"];
        worker: Codeunit "CGA Worker";
    begin
        list.Add(worker);
        exit(list);
    end;

    procedure FillDictionary(var dict: Dictionary of [Text, List of [Codeunit "CGA Worker"]])
    var
        list: List of [Codeunit "CGA Worker"];
    begin
        list := BuildList();
        dict.Add('A', list);
    end;

    procedure GetFirstValue(): Integer
    var
        dict: Dictionary of [Text, List of [Codeunit "CGA Worker"]];
        list: List of [Codeunit "CGA Worker"];
        worker: Codeunit "CGA Worker";
    begin
        FillDictionary(dict);
        if dict.Get('A', list) then
            if list.Get(1, worker) then
                exit(worker.GetValue());
        exit(0);
    end;

    procedure AddDuplicateKey()
    var
        dict: Dictionary of [Text, List of [Codeunit "CGA Worker"]];
        list: List of [Codeunit "CGA Worker"];
    begin
        FillDictionary(dict);
        list := BuildList();
        dict.Add('A', list);
    end;
}

codeunit 110000 XmlDocNodeManipSrc
{
    procedure RemoveDoc(var Doc: XmlDocument)
    begin
        Doc.Remove();
    end;

    procedure RemoveDocLocal()
    var
        Doc: XmlDocument;
    begin
        XmlDocument.ReadFrom('<root/>', Doc);
        Doc.Remove();
    end;

    procedure ReplaceDocWith(var Doc: XmlDocument; NewDoc: XmlDocument)
    begin
        Doc.ReplaceWith(NewDoc);
    end;

    procedure AddDocAfterSelf(var Doc: XmlDocument; Sibling: XmlNode)
    begin
        Doc.AddAfterSelf(Sibling);
    end;

    procedure AddDocBeforeSelf(var Doc: XmlDocument; Sibling: XmlNode)
    begin
        Doc.AddBeforeSelf(Sibling);
    end;
}

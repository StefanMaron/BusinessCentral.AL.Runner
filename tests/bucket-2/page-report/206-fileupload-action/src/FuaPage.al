/// Page with a fileupload() action in a Processing action area.
/// fileupload() is a UI-only declaration; the runner strips it so AL
/// compilation succeeds. No runtime behaviour is expected.
page 97800 "FUA Page"
{
    PageType = Card;
    actions
    {
        area(Processing)
        {
            fileupload(UploadFile)
            {
                Caption = 'Upload File';
                AllowedFileExtensions = '.csv,.xlsx';
            }
        }
    }
}

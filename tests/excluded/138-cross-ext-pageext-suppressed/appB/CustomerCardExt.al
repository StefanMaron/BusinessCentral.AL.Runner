// App B also defines a pageextension named "SharedPageExt".
// This is valid in real BC (separate extensions compile independently).
// The runner must suppress this cross-extension collision (exit 0, not exit 3).
pageextension 310511 "SharedPageExt" extends "Customer List" { }

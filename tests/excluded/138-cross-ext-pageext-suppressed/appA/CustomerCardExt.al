// App A defines a pageextension named "SharedPageExt".
// In real BC, two separate extensions can each define a pageextension with the
// same name — they compile independently and never collide.
// The runner compiles all sources in a single pass, which causes a false AL0197.
// Case 3 in Program.cs suppresses this cross-extension collision as a runner artifact.
pageextension 310510 "SharedPageExt" extends "Customer List" { }

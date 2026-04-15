// Second pageextension named "DuplicatedExt" — same extension as PageExt1.
// This must cause an AL0197 error that is NOT suppressed because both
// objects come from the same extension identity.
pageextension 56351 "DuplicatedExt" extends "Customer List" { }

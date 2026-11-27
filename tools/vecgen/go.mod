module github.com/victorcondackdev/pix-automatico-dotnet/tools/vecgen

go 1.22

// The generator runs against a local checkout of the Go implementation,
// cloned next to this repository (../pix-automatico-go relative to the repository root).
replace github.com/victorcondackdev/pix-automatico-go => ../../../pix-automatico-go

require github.com/victorcondackdev/pix-automatico-go v0.0.0-00010101000000-000000000000

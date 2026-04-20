module BitThicket.Steward.Api.Test.Tests

open Xunit
open Swensen.Unquote

[<Fact>]
let ``sanity check`` () =
    test <@ 1 + 1 = 2 @>

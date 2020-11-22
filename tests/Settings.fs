module Tests.Settings

open Expecto
open EdLauncher
open EdLauncher.Settings
open EdLauncher.Types

[<Tests>]
let tests =
    let parseWithFallback fallback args =
        match parseArgs Settings.defaults fallback args with
        | Ok settings -> settings
        | Error _ -> Settings.defaults        
    let parse args =
        parseWithFallback (fun () -> Ok ".") args
      
    testList "Parsing command line arguments" [
        test "Matches /steamid" {
            let settings = parse [| "/steamid" |]
            Expect.equal settings.Platform Steam ""
            Expect.equal settings.ForceLocal true ""
        }
        test "Matches /steam" {
            let settings = parse [| "/steam" |]
            Expect.equal settings.Platform Steam ""
            Expect.equal settings.ForceLocal true ""
        }
        test "Matches /epic" {
            let settings = parse [| "/epic" |]
            Expect.equal settings.Platform (Epic EpicDetails.Empty) ""
            Expect.equal settings.ForceLocal true ""
        }
        test "Matches epic password" {
            let settings = parse [| "-AUTH_PASSWORD=asdf" |]
            Expect.equal settings.Platform (Epic { EpicDetails.Empty with ExchangeCode = "asdf" }) ""
        }
        test "Matches epic type" {
            let settings = parse [| "-AUTH_TYPE=asdf" |]
            Expect.equal settings.Platform (Epic { EpicDetails.Empty with Type = "asdf" }) ""
        }
        test "Matches epic env" {
            let settings = parse [| "-epicenv=asdf" |]
            Expect.equal settings.Platform (Epic { EpicDetails.Empty with Env = "asdf" }) ""
        }
        test "Matches epic userid" {
            let settings = parse [| "-epicuserid=asdf" |]
            Expect.equal settings.Platform (Epic { EpicDetails.Empty with UserId = "asdf" }) ""
        }
        test "Matches epic locale" {
            let settings = parse [| "-epiclocale=asdf" |]
            Expect.equal settings.Platform (Epic { EpicDetails.Empty with Locale = "asdf" }) ""
        }
        test "Matches epic refresh token" {
            let settings = parse [| "/epicrefreshtoken"; "asdf" |]
            Expect.equal settings.Platform (Epic { EpicDetails.Empty with RefreshToken = Some "asdf" }) ""
        }
        test "Matches epic log" {
            let settings = parse [| "/logepicinfo" |]
            Expect.equal settings.Platform (Epic { EpicDetails.Empty with Log = true }) ""
        }
        test "Matches epic token name" {
            let settings = parse [| "/epictokenname"; "asdf" |]
            Expect.equal settings.Platform (Epic { EpicDetails.Empty with TokenName = "asdf" }) ""
        }
        test "Matches /oculus nonce" {
            let settings = parse [| "/oculus"; "123" |]
            Expect.equal settings.Platform (Oculus "123") ""
            Expect.equal settings.ForceLocal true ""
        }
        test "Ignores /oculus without nonce as next arg" {
            let settings = parse [| "/oculus"; "/123" |]
            Expect.equal settings.Platform Settings.defaults.Platform ""
        }
        test "Matches /vr" {
            let settings = parse [| "/vr" |]
            Expect.equal settings.DisplayMode Vr ""
            Expect.equal settings.AutoRun true "VR mode should autorun the game"
        }
        test "Matches /autorun" {
            let settings = parse [| "/autorun" |]
            Expect.equal settings.AutoRun true ""
        }
        test "Matches /autoquit" {
            let settings = parse [| "/autoquit" |]
            Expect.equal settings.AutoQuit true ""
        }
        test "Matches /forcelocal" {
            let settings = parse [| "/forcelocal" |]
            Expect.equal settings.ForceLocal true ""
        }
        test "Matches /ed" {
            let settings = parse [| "/ed" |]
            Expect.equal (settings.ProductWhitelist.Contains "ed") true ""
        }
        test "Matches /edh" {
            let settings = parse [| "/edh" |]
            Expect.equal (settings.ProductWhitelist.Contains "edh") true ""
        }
        test "Matches /eda" {
            let settings = parse [| "/eda" |]
            Expect.equal (settings.ProductWhitelist.Contains "eda") true ""
        }
        test "Matches proton args" {
            let protonPath = "steamapps/common/Proton"
            let protonAction = "action"
            let launcherDir = "launchDir"
            let launcherPath = launcherDir + "/EDLaunch.exe"
            let settings = parse [| protonPath; protonAction; launcherPath |]
            Expect.equal settings.Proton (Some (protonPath, protonAction)) ""
            Expect.equal settings.CbLauncherDir launcherDir ""
        }
        test "Fewer than three args means no Proton" {
            let settings = parse [| "asdf"; "fdsa" |]
            Expect.equal settings.Proton None ""
        }
        test "First arg doesn't contain steamapps/common/Proton means no Proton" {
            let settings = parse [| "asdf"; "fdsa"; "launchDir" |]
            Expect.equal settings.Proton None ""
        }
        test "Non Proton uses fallback dir for cobra bay launcher dir" {
            let expectedDir = "test/dir"
            let settings = parseWithFallback (fun () -> Ok expectedDir) [||]
            Expect.equal settings.CbLauncherDir expectedDir ""
        }
        testProperty "Unknown arg doesn't change any values" <|
            fun (args:string[]) -> parse args = Settings.defaults
    ]

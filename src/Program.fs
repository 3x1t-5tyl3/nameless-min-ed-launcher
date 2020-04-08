﻿open System
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open FileIO
open Steam
open Types
open Settings

let log =
    let write level msg = printfn "[%s] - %s" level msg
    { Debug = fun msg -> write "DBG" msg
      Info = fun msg -> write "INF" msg
      Warn = fun msg -> write "WRN" msg
      Error = fun msg -> write "ERR" msg }
    
let getOsIdent() =
    let platToStr plat =
        if   plat = OSPlatform.Linux   then "Linux"
        elif plat = OSPlatform.Windows then "Win"
        elif plat = OSPlatform.OSX     then "Mac"
        elif plat = OSPlatform.FreeBSD then "FreeBSD"
        else "Unknown"        
    let platform =
        [ OSPlatform.Linux; OSPlatform.Windows; OSPlatform.OSX; OSPlatform.FreeBSD ]
        |> List.pick (fun p -> if RuntimeInformation.IsOSPlatform(p) then Some p else None)
        |> platToStr
    let arch =
        match RuntimeInformation.ProcessArchitecture with
        | Architecture.Arm -> "Arm"
        | Architecture.Arm64 -> "Arm64"
        | Architecture.X64 -> "64"
        | Architecture.X86 -> "32"
        | unknownArch -> unknownArch.ToString()
    
    platform + arch

let getUserDetails = function
    | Dev -> Ok { UserId = 12345UL; SessionToken = "DevToken" }
    | Oculus _ -> Error "Oculus not supported"
    | Frontier -> Error "Frontier not supported"
    | Steam _ ->
        use steam = new Steam(log)
        steam.Login()
        
let getProductsDir fallbackPath hasWriteAccess (forceLocal:ForceLocal) launcherDir =
    let productsPath = "Products"
    let localPath = Path.Combine(launcherDir, productsPath)
    
    if forceLocal then localPath
    elif hasWriteAccess launcherDir then localPath
    else Path.Combine(fallbackPath, productsPath)
    
let getVersion launcherDir =
    let cobraPath = Path.Combine(launcherDir, "CBViewModel.dll")
    
    if not (File.Exists cobraPath) then
        Error <| sprintf "Unable to find CBViewModel.dll in directory %s" launcherDir
    else
        let cobraVersion =
            let version = FileVersionInfo.GetVersionInfo(cobraPath)
            if String.IsNullOrEmpty(version.FileVersion) then version.ProductVersion else version.FileVersion
        let launcherVersion = typeof<Steam>.Assembly.GetName().Version
        
        Ok (cobraVersion, launcherVersion)
    
let printInfo platform user productsDir cobraVersion launcherVersion =
    printfn "Elite: Dangerous Launcher"
    printfn "Platform: %A" platform
    printfn "OS: %s" (getOsIdent())
    printfn "CobraBay Version: %s" cobraVersion
    printfn "Launcher Version: %A" launcherVersion
    
    match user with
    | Error msg -> printfn "User: Error - %s" msg
    | Ok user ->
        printfn "User: %u" user.UserId
        printfn "Session Token: %s" user.SessionToken
    match productsDir with
    | Error msg -> printfn "Products Dir: Unable to access - %s" msg
    | Ok p -> printfn "Products Dir: %s" p
    ()

[<EntryPoint>]
let main argv =
    let settings = parseArgs log Settings.defaults [|"/forcelocal" |] //argv
    let user = getUserDetails settings.Platform
    let launcherDir = AppContext.BaseDirectory// Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName)
    let appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Frontier_Developments")
    let productsDir =
        getProductsDir appDataDir hasWriteAccess settings.ForceLocal launcherDir
        |> ensureDirExists
    let version = getVersion launcherDir
    match version with
    | Error msg ->
        log.Error <| sprintf "Unable to get version: %s" msg
        1
    | Ok (cobraVersion, launcherVersion) ->
        printInfo settings.Platform user productsDir cobraVersion launcherVersion
        
        0

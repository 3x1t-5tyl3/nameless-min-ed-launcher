﻿namespace EdLauncher

module Program =
    open System
    open System.Diagnostics
    open System.IO
    open System.Net.Http
    open System.Reflection
    open System.Resources
    open System.Runtime.InteropServices
    open FileIO
    open Steam
    open Types
    open Settings
    open Rop
        
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

    let getEventLogPaths httpClient = async {
        let! result = EventLog.LocalFile.create "test.txt" // logs/Client.log
        let file =
            match result with
            | Ok file -> Some file
            | Error msg ->
                Log.error msg
                None
        let remote =
            let url = "http://localhost:8080" // https://api.zaonce.net/1.1/
            match httpClient, Uri.TryCreate(url, UriKind.Absolute) with
            | None, _ ->
                Log.info "Remote logging disabled via configuration"
                None
            | Some httpClient, (true, uri) ->
                Some <| EventLog.RemoteLog (httpClient, { Uri = uri
                                                          MachineToken = ""
                                                          AuthToken = ""
                                                          MachineId = ""
                                                          RunningTime = fun () -> 1L })
            | Some _, (false, _) ->
                Log.errorf "EventLog.RemotePath - Invalid URI %s. Disabling" url
                None
        return file, remote
    }
    let writeEventLog httpClient entry = async {
        let! file, remote = getEventLogPaths httpClient
        let! result = EventLog.write file remote entry
        result |> Array.iter (fun e -> match e with
                                       | Error e -> Log.warn e
                                       | _ -> ())
    }

    type LoginResult =
    | Success of User
    | ActionRequired of string
    | Failure of string
    let login serverRequest machineId platform = async {
        match platform with
        | Dev -> return Success { Name = "Dev User"; MachineToken = "DevToken"; EmailAddress = "a@a.com"; SessionToken = "AuthToken" }
        | Oculus _ -> return Failure "Oculus not supported"
        | Frontier -> return Failure "Frontier not supported"
        | Steam _ ->
            use steam = new Steam()
            return! match steam.Login() with
                    | Ok steamUser -> async {
                        let authDetails = Api.Steam (steamUser.SessionToken, machineId)
                        // TODO: event log RequestingSteamAuthentication no params
                        Log.debug "Authenticating via Steam"
                        match! Api.authenticate authDetails serverRequest with
                        | Api.Authorized (authToken, machineToken, name) ->
                            Log.debug "Successfully authenticated"
                            // TODO: event log SteamAuthenticated no params
                            return Success { Name = name
                                             EmailAddress = ""
                                             SessionToken = authToken
                                             MachineToken = machineToken }
                        | Api.RegistrationRequired uri -> return ActionRequired <| sprintf "Registration is required at %A" uri
                        | Api.LinkAvailable uri -> return ActionRequired <| sprintf "Link available at %A" uri
                        | Api.Denied msg -> return Failure msg
                        | Api.Failed msg -> return Failure msg
                        }
                    | Error msg -> async { return Failure msg }
            
    }

    let getLogPath = function
        | Dev -> Ok "logs"
        | Oculus _ -> Error "Oculus not supported"
        | Frontier -> Error "Frontier not supported"
        | Steam _ -> Ok "logs"
            
    let getProductsDir fallbackPath hasWriteAccess (forceLocal:ForceLocal) launcherDir =
        let productsPath = "Products"
        let localPath = Path.Combine(launcherDir, productsPath)
        //let localPath = "/mnt/games/Steam/Linux/steamapps/common/Elite Dangerous/Products"
        if forceLocal then localPath
        elif hasWriteAccess launcherDir then localPath
        else Path.Combine(fallbackPath, productsPath)
        
    let getVersion cbLauncherDir =
        let cobraPath = Path.Combine(cbLauncherDir, "CBViewModel.dll")
        
        if not (File.Exists cobraPath) then
            Error <| sprintf "Unable to find CBViewModel.dll in directory %s" cbLauncherDir
        else
            let cobraVersion =
                let version = FileVersionInfo.GetVersionInfo(cobraPath)
                if String.IsNullOrEmpty(version.FileVersion) then version.ProductVersion else version.FileVersion
            let launcherVersion = typeof<Steam>.Assembly.GetName().Version
            
            Ok (cobraVersion, launcherVersion)
            
    let createZaonceClient baseUri clientName clientVersion osIdent =
        let userAgent = sprintf "%s/%s/%s" clientName clientVersion osIdent
        let httpClient = new HttpClient()
        httpClient.BaseAddress <- baseUri
        httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent) |> ignore
        httpClient.DefaultRequestHeaders.ConnectionClose <- Nullable<bool>(false)
        httpClient
        
    let printInfo platform productsDir cobraVersion launcherVersion remoteTime =
        printfn "Elite: Dangerous Launcher"
        printfn "Platform: %A" platform
        printfn "OS: %s" (getOsIdent())
        printfn "CobraBay Version: %s" cobraVersion
        printfn "Launcher Version: %A" launcherVersion
        printfn "Launcher Name: %A" (System.Reflection.AssemblyName.GetAssemblyName("/mnt/games/Steam/Linux/steamapps/common/Elite Dangerous/EDLaunch.exe").Name)
        printfn "Remote Time: %i" remoteTime
        printfn "Products Dir: %s" productsDir

    type VersionInfoStatus = Found of VersionInfo | NotFound of string | Failed of string
    let readVersionInfo path = 
        let file = Path.Combine(path, "VersionInfo.txt")
        let mode offline = if offline then Offline else Online
        if not (File.Exists(file)) then NotFound file
        else
            let json = (FileIO.openRead file) >>= Json.parseStream >>= Json.rootElement
            let version = json >>= Json.parseProp "Version" >>= Json.asVersion
            let exe = json >>= Json.parseProp "executable" >>= Json.toString
            let name = json >>= Json.parseProp "name" >>= Json.toString
            let wd64 = json >>= Json.parseProp "useWatchDog64" >>= Json.asBool |> Result.defaultValue false
            let steamAware = json >>= Json.parseProp "steamaware" >>= Json.asBool |> Result.defaultValue true
            let offline = json >>= Json.parseProp "offline" >>= Json.asBool |> Result.defaultValue false
            match version, exe, name with
            | Ok version, Ok exe, Ok name ->
                { Name = name
                  Executable = exe
                  UseWatchDog64 = wd64
                  SteamAware = steamAware
                  Version = version
                  Mode = mode offline } |> Found
            | _ -> VersionInfoStatus.Failed "Unexpected VersionInfo json document"
    
    let mapProduct productsDir (product:AuthorizedProduct) =
        let serverArgs = String.Join(" ", [
                if product.TestApi then "/Test"
                if not (String.IsNullOrEmpty(product.ServerArgs)) then product.ServerArgs
            ])
        let filters = product.Filter.Split(',', StringSplitOptions.RemoveEmptyEntries) |> Set.ofArray
        let directory = Path.Combine(productsDir, product.Directory)
        match readVersionInfo (Path.Combine(productsDir, product.Directory)) with
        | Found v ->
            Playable { Sku = product.Sku
                       Name = product.Name
                       Filters = filters
                       Executable = v.Executable
                       UseWatchDog64 = v.UseWatchDog64
                       SteamAware = v.SteamAware
                       Version = v.Version
                       Mode = v.Mode
                       Directory = directory
                       GameArgs = product.GameArgs
                       ServerArgs = serverArgs }
        | NotFound file ->
            Log.infof "Disabling '%s'. Unable to find product at '%s'" product.Name file
            Missing { Sku = product.Sku
                      Name = product.Name
                      Filters = filters
                      Directory = directory }
        | Failed msg ->
            Log.errorf "Unable to parse product %s: %s" product.Name msg
            Unknown product.Name
            
    let getGameLang cbLauncherDir =
        let asm = Assembly.LoadFrom(Path.Combine(cbLauncherDir, "LocalResources.dll"))
        let resManager = ResourceManager("LocalResources.Properties.Resources", asm)
        try
            resManager.GetString("GameLanguage") |> Some
        with
        | e -> None 
    
    let private run proton cbLauncherDir apiUri args = async {
        let settings = parseArgs Settings.defaults args
        let appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Frontier_Developments")
        let productsDir =
            getProductsDir appDataDir hasWriteAccess settings.ForceLocal cbLauncherDir
            |> ensureDirExists
        let version = getVersion cbLauncherDir
        return!
            match productsDir, version with 
            | _, Error msg -> async {
                Log.errorf "Unable to get version: %s" msg
                return 1 }
            | Error msg, _ -> async { 
                Log.errorf "Unable to get products directory: %s" msg
                return 1 }
            | Ok productsDir, Ok (cbVersion, launcherVersion) -> async {
                let cbLauncherName = System.Reflection.AssemblyName.GetAssemblyName(Path.Combine(cbLauncherDir, "EDLaunch.exe")).Name
                use httpClient = createZaonceClient apiUri cbLauncherName cbVersion (getOsIdent())
                let serverRequest = Server.request httpClient 
                let localTime = DateTime.UtcNow
                let getRemoteTime runningTime = async { 
                    match! Api.getTime localTime (serverRequest runningTime) with
                    | Ok timestamp -> return timestamp
                    | Error (localTimestamp, msg) ->
                        Log.warnf "Couldn't get remote time: %s. Using local system time instead" msg
                        return localTimestamp
                    }
                let! remoteTime = getRemoteTime (fun () -> (double)1)
                let runningTime = fun () ->
                        let runningTime = DateTime.UtcNow.Subtract(localTime);
                        ((double)remoteTime + runningTime.TotalSeconds)
                let serverRequest = serverRequest runningTime
                let! machineId =
#if WINDOWS
                    MachineId.getWindowsId()
#else
                    MachineId.getWineId()
#endif
                let remoteLogHttpClient = if settings.RemoteLogging then Some httpClient else None
                let logEvents = writeEventLog remoteLogHttpClient
                do! logEvents [ EventLog.LogStarted; EventLog.ClientVersion ("app", "path", DateTime.Now) ] // TODO: Check if .Now is correct
                // TODO: Check if launcher version is compatible with current ED version
                
                printInfo settings.Platform productsDir cbVersion launcherVersion remoteTime
                
                match machineId with
                | Ok machineId ->
                    match! login serverRequest machineId settings.Platform with
                    | Success user ->
                        // TODO: event log Authenticated user.Name
                        Log.infof "Logged in via %A as: %s (%s)" settings.Platform user.Name user.EmailAddress
                        match! Api.getAuthorizedProducts user.SessionToken None serverRequest with
                        | Ok authorizedProducts ->
                             do! logEvents [ EventLog.AvailableProjects (user.EmailAddress, authorizedProducts |> List.map (fun p -> p.Sku)) ]
                             let! products = authorizedProducts
                                             |> List.map (mapProduct productsDir)
                                             |> Api.checkForUpdates user.SessionToken user.MachineToken machineId serverRequest
                             let availableProducts =
                                 products
                                 |> Result.defaultValue []
                                 |> List.map (fun p -> match p with
                                                       | Playable p -> Some (p.Name, "Up to date")
                                                       | RequiresUpdate p -> Some (p.Name, "Requires Update")
                                                       | Missing _ -> None
                                                       | Unknown _ -> None)
                                 |> List.choose id
                             Log.infof "Available Products:%s\t%s" Environment.NewLine (String.Join(Environment.NewLine + "\t", availableProducts))
                             let selectedProduct =
                                products
                                |> Result.defaultValue []
                                |> List.choose (fun p -> match p with | Playable p -> Some p | _ -> None)
                                |> List.filter (fun p -> settings.ProductWhitelist.Count = 0
                                                         || p.Filters |> Set.union settings.ProductWhitelist |> Set.count > 0)
                                |> List.tryHead
                             
                             match selectedProduct, settings.AutoRun with
                             | Some product, true ->
                                 let gameLanguage = getGameLang cbLauncherDir                                 
                                 let processArgs = Product.createArgString settings.DisplayMode gameLanguage user.MachineToken user.SessionToken machineId (runningTime()) settings.WatchForCrashes settings.Platform SHA1.hashFile product
                                 
                                 match Product.validateForRun cbLauncherDir settings.WatchForCrashes product with
                                 | Ok p ->
                                     match Product.run proton processArgs p with
                                     | Product.RunResult.Ok p ->
                                         Log.infof "Launching %s" product.Name
                                         use p = p
                                         p.WaitForExit()
                                         Log.infof "Shutdown %s" product.Name
                                     | Product.RunResult.AlreadyRunning -> Log.infof "%s is already running" product.Name
                                     | Product.RunResult.Error e -> Log.errorf "Couldn't start selected product: %s" (e.ToString())
                                 | Error msg -> Log.errorf "Couldn't start selected product: %s" msg
                             | None, true -> Log.error "No selected project"
                             | _, _ -> ()
                             
                             if not settings.AutoQuit then
                                 printfn "Press any key to quit..."
                                 Console.ReadKey() |> ignore
                             
                        | Error msg ->
                            Log.errorf "Couldn't get available products: %s" msg
                    | ActionRequired msg ->
                        Log.errorf "Unsupported login action required: %s" msg
                    | Failure msg ->
                        Log.errorf "Couldn't login: %s" msg
                | Error msg ->
                    Log.errorf "Couldn't get machine id: %s" msg
                
                return 0 }
    }    

    [<EntryPoint>]
    let main argv =
        let proton, cbLaunchDir, args =
            if argv.Length > 2 && argv.[0].Contains("steamapps/common/Proton") then
                Some (argv.[0], argv.[1]), Path.GetDirectoryName(argv.[2]), argv.[2..]
            else
                None, "/mnt/games/Steam/Linux/steamapps/common/Elite Dangerous", argv
        let apiUri = Uri("https://api.zaonce.net")
        //let apiUri = Uri("http://localhost:8080")
        async {
            do! Async.SwitchToThreadPool ()
            return! run proton cbLaunchDir apiUri args
        } |> Async.RunSynchronously
        
        

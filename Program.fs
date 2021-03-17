// Learn more about F# at http://docs.microsoft.com/dotnet/fsharp

open System
open Models
open Client

let game (client: Client) = async {
    for x in 0 .. 3500 do
        for y in 0 .. 3500 do
            let area = { oneBlockArea with PosX = x; PosY = y }
            let mutable license = { DigAllowed = 0; DigUsed = 0; Id = None }
            let! result = client.PostExplore(area)
            match result with 
            | Ok exploreResult when exploreResult.Amount > 0 -> 
                Console.WriteLine("explore result is ok: " + exploreResult.ToString())
                let mutable depth = 1
                let mutable left = exploreResult.Amount
                let condition = fun () -> match license.Id with
                                          | Some _ when license.DigUsed < license.DigAllowed -> false
                                          | _ -> true
                while condition() do
                    let! licenseUpdateResult = client.PostLicense Seq.empty<int>
                    match licenseUpdateResult with 
                    | Ok newLicense -> license <- newLicense
                    | _ -> ()

                let dig = { LicenseID = license.Id.Value; PosX = x; PosY = y; Depth = 1 }
                let! treasuresResult = client.PostDig dig
                license <- { license with DigUsed = license.DigUsed + 1 }
                depth <- depth + 1
                match treasuresResult with 
                | Error _ -> ()
                | Ok treasures -> 
                    for treasure in treasures.Treasures do
                        let! res = client.PostCash treasure
                        match res with 
                        | Ok _ -> left <- left - 1
                        | _ -> ()
            | Ok exploreResult -> Console.WriteLine("explore result is ok, but without amount: " + exploreResult.ToString())
            | Error code -> Console.WriteLine("explore result is not ok: " + code.ToString())

    }

[<EntryPoint>]
let main argv =
    Console.WriteLine("start")
    let urlEnv = Environment.GetEnvironmentVariable("ADDRESS")
    Console.WriteLine("host: " + urlEnv)
    let client = new Client.Client("http://" + urlEnv + ":8000/")
    game client |> Async.RunSynchronously |> ignore
    Console.WriteLine("ended")
    0 // return an integer exit code
// Learn more about F# at http://docs.microsoft.com/dotnet/fsharp

open System
open Models
open Client

let game (client: Client) = async {
    for x in 0 .. 3500 do
        for y in 0 .. 3500 do
            let area = { oneBlockArea with PosX = x; PosY = y }
            let! result = client.PostExplore(area)
            match result with 
            | Ok exploreResult when exploreResult.Amount > 0 -> 
                Console.WriteLine("explore result is ok: " + exploreResult.ToString())
                let mutable depth = 1
                let mutable left = exploreResult.Amount
                let condition = fun () -> match client.License.Id with
                                          | Some _ when client.License.DigUsed < client.License.DigAllowed -> false
                                          | _ -> true
                while condition() do
                    client.UpdateLicense() |> Async.RunSynchronously |> ignore

                let dig = { LicenseID = client.License.Id.Value; PosX = x; PosY = y; Depth = 1 }
                let! treasuresResult = client.PostDig dig
                client.License <- { client.License with DigUsed = client.License.DigUsed + 1 }
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
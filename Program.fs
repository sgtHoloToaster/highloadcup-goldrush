// Learn more about F# at http://docs.microsoft.com/dotnet/fsharp

open System
open Models
open Client

type DiggerMessage = {
    PosX: int
    PosY: int
    Depth: int
    Amount: int
}

let digger (client: Client) (inbox: MailboxProcessor<DiggerMessage>) = 
    let mutable license = { DigAllowed = 0; DigUsed = 0; Id = None }

    let rec postCash treasure = async {
        let! res = client.PostCash treasure
        match res with 
        | Ok _ -> ()
        | _ -> return! postCash treasure
    }

    let doDig msg = async {
        let dig = { LicenseID = license.Id.Value; PosX = msg.PosX; PosY = msg.PosY; Depth = msg.Depth }
        let! treasuresResult = client.PostDig dig
        match treasuresResult with 
        | Error _ -> inbox.Post msg // retry
        | Ok treasures -> 
            for treasure in treasures.Treasures do
                postCash treasure |> ignore

            inbox.Post { msg with Depth = msg.Depth + 1; Amount = msg.Amount - 1 }
    }

    let rec messageLoop() = async {
        let! msg = inbox.Receive()
        if msg.Amount = 0 || msg.Depth = 10 then return! messageLoop()

        let condition = fun () -> match license.Id with
                                  | Some _ when license.DigUsed < license.DigAllowed -> false
                                  | _ -> true
        while condition() do
            let! licenseUpdateResult = client.PostLicense Seq.empty<int>
            match licenseUpdateResult with 
            | Ok newLicense -> license <- newLicense
            | _ -> ()

        license <- { license with DigUsed = license.DigUsed + 1 }
        doDig msg |> ignore
        return! messageLoop()
        }

    messageLoop()

let game (client: Client) = async {
    let diggerAgent = MailboxProcessor.Start (digger client)
    for x in 0 .. 3500 do
        for y in 0 .. 3500 do
            let area = { oneBlockArea with PosX = x; PosY = y }
            let! result = client.PostExplore(area)
            match result with 
            | Ok exploreResult when exploreResult.Amount > 0 -> 
                Console.WriteLine("explore result is ok: " + exploreResult.ToString())
                diggerAgent.Post { PosX = exploreResult.Area.PosX; PosY = exploreResult.Area.PosY; Depth = 1; Amount = exploreResult.Amount }
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
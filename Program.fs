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
            Console.WriteLine("dig result: " + treasures.ToString())
            let mutable left = msg.Amount
            for treasure in treasures.Treasures do
                let! result = client.PostCash treasure
                match result with 
                | Ok _ -> left <- left - 1
                | _ -> ()

            inbox.Post { msg with Depth = msg.Depth + 1; Amount = left }
    }

    let rec messageLoop() = async {
        let! msg = inbox.Receive()
        if msg.Depth > 1 then
            Console.WriteLine("received: " + msg.ToString())
        if msg.Amount > 0 && msg.Depth <= 10 then 
            while license.Id.IsNone || license.DigAllowed <= license.DigUsed do
                let! licenseUpdateResult = client.PostLicense Seq.empty<int>
                return match licenseUpdateResult with 
                       | Ok newLicense -> license <- newLicense
                       | Error _ -> ()

            license <- { license with DigUsed = license.DigUsed + 1 }
            doDig msg |> Async.Start

        return! messageLoop()
    }

    messageLoop()

let rec explore (client: Client) (diggerAgent: MailboxProcessor<DiggerMessage>) (area: Area) = async {
    let! result = client.PostExplore(area)
    match result with 
    | Ok exploreResult when exploreResult.Amount > 0 -> 
        diggerAgent.Post { PosX = exploreResult.Area.PosX; PosY = exploreResult.Area.PosY; Depth = 1; Amount = exploreResult.Amount }
    | Ok _ -> ()
    | Error _ -> return! explore client diggerAgent area
}

let game (client: Client) = async {
    let diggerAgents = [|0 .. 9|] |> Seq.map (fun _ -> MailboxProcessor.Start (digger client))
    let mutable diggerAgentsEnumerator = diggerAgents.GetEnumerator()
    for x in 0 .. 3500 do
        for y in 0 .. 3500 do
            let area = { oneBlockArea with PosX = x; PosY = y }
            if not (diggerAgentsEnumerator.MoveNext()) then
                diggerAgentsEnumerator.Dispose()
                diggerAgentsEnumerator <- diggerAgents.GetEnumerator()
                diggerAgentsEnumerator.MoveNext() |> ignore

            let diggerAgent = diggerAgentsEnumerator.Current
            explore client diggerAgent area |> Async.Start
            do! Async.Sleep(100)

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
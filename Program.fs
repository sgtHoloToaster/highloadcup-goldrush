// Learn more about F# at http://docs.microsoft.com/dotnet/fsharp

open System
open Models
open Client
open System.Net.Http
open System.Net

type DiggerMessage = {
    PosX: int
    PosY: int
    Depth: int
    Amount: int
}

let digger (client: Client) (inbox: MailboxProcessor<DiggerMessage>) = 
    let inline doDig licenseId msg = async {
        Console.WriteLine("doDig called. license:" + licenseId.ToString() + "body: " + msg.ToString())
        let dig = { LicenseID = licenseId; PosX = msg.PosX; PosY = msg.PosY; Depth = msg.Depth }
        let! treasuresResult = client.PostDig dig
        match treasuresResult with 
        | Error ex -> 
            Console.WriteLine(ex); 
            match ex with 
            | :? HttpRequestException as ex when ex.StatusCode.HasValue && (ex.StatusCode.Value = HttpStatusCode.NotFound || ex.StatusCode.Value = HttpStatusCode.UnprocessableEntity) -> ()
            | _ -> inbox.Post msg // retry
        | Ok treasures -> 
            Console.WriteLine("dig result: " + treasures.ToString())
            let mutable left = msg.Amount
            for treasure in treasures.Treasures do
                let! result = client.PostCash treasure
                match result with 
                | Ok _ -> left <- left - 1
                | _ -> ()

            let depth = msg.Depth + 1
            inbox.Post ({ msg with Depth = depth; Amount = left })
    }

    let rec messageLoop (license: License) = async {
        if license.Id.IsNone || license.DigAllowed <= license.DigUsed then
            let! licenseUpdateResult = client.PostLicense Seq.empty<int>
            match licenseUpdateResult with 
                  | Ok newLicense -> Console.WriteLine("new license: " + newLicense.ToString()); return! messageLoop newLicense
                  | Error _ -> return! messageLoop license
        else
            let! msg = inbox.Receive()
            Console.WriteLine("received: " + msg.ToString())
            if msg.Amount > 0 && msg.Depth <= 10 then
                doDig license.Id.Value msg |> Async.Start
                return! messageLoop { license with DigUsed = license.DigUsed + 1 }
            else
                return! messageLoop license
    }

    messageLoop { Id = None; DigAllowed = 0; DigUsed = 0 }

let rec explore (client: Client) (diggerAgent: MailboxProcessor<DiggerMessage>) (area: Area) = async {
    let! result = client.PostExplore(area)
    match result with 
    | Ok exploreResult when exploreResult.Amount > 0 -> 
        diggerAgent.Post { PosX = exploreResult.Area.PosX; PosY = exploreResult.Area.PosY; Depth = 1; Amount = exploreResult.Amount }
    | Ok _ -> ()
    | Error _ -> return! explore client diggerAgent area
}

let game (client: Client) = async {
    let diggerAgents = seq { 
        for _ in 1 .. 10 do
            MailboxProcessor.Start (digger client)
    }

    Console.WriteLine("diggers: " + (diggerAgents |> Seq.length).ToString())
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
            do! Async.Sleep(5)

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
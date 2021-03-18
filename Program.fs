// Learn more about F# at http://docs.microsoft.com/dotnet/fsharp

open System
open Models
open Client
open System.Net.Http
open System.Net

type Coordinates = {
    PosX: int
    PosY: int
}

type DiggerMessage = {
    PosX: int
    PosY: int
    Depth: int
    Amount: int
}

let digger (client: Client) (inbox: MailboxProcessor<DiggerMessage>) = 
    let doDig licenseId msg = async {
        let dig = { LicenseID = licenseId; PosX = msg.PosX; PosY = msg.PosY; Depth = msg.Depth }
        let! treasuresResult = client.PostDig dig
        match treasuresResult with 
        | Error ex -> 
            match ex with 
            | :? HttpRequestException as ex when ex.StatusCode.HasValue ->
                match ex.StatusCode.Value with 
                | HttpStatusCode.NotFound -> inbox.Post { msg with Depth = msg.Depth + 1 }
                | HttpStatusCode.UnprocessableEntity -> ()
                | _ -> inbox.Post msg // retry
            | _ -> inbox.Post msg // retry
        | Ok treasures -> 
            let digged = 
                treasures.Treasures 
                |> Seq.map (
                    fun treasure -> async {
                        let! result = client.PostCash treasure
                        return match result with 
                                | Ok _ -> 1
                                | _ -> 0
                })
                |> Async.Parallel
                |> Async.RunSynchronously
                |> Seq.sum

            inbox.Post ({ msg with Depth = msg.Depth + 1; Amount = msg.Amount - digged })
    }

    let rec messageLoop (license: License) = async {
        let! newLicense = async {
            if license.Id.IsSome && license.DigAllowed > license.DigUsed then
                let! msg = inbox.Receive()
                if msg.Amount > 0 && msg.Depth <= 10 then
                    doDig license.Id.Value msg |> Async.Start
                    return { license with DigUsed = license.DigUsed + 1 }
                else
                    return license
            else 
                let! licenseUpdateResult = client.PostLicense Seq.empty<int>
                return match licenseUpdateResult with 
                       | Ok newLicense -> newLicense
                       | Error ex -> license
        }

        return! messageLoop newLicense
    }

    messageLoop { Id = None; DigAllowed = 0; DigUsed = 0 }

let rec exploreAndDig (client: Client) (diggerAgent: MailboxProcessor<DiggerMessage>) (coordinates: Coordinates) = async {
    let area = { oneBlockArea with PosX = coordinates.PosX; PosY = coordinates.PosY }
    let! result = client.PostExplore(area)
    match result with 
    | Ok exploreResult when exploreResult.Amount > 0 -> 
        diggerAgent.Post { PosX = exploreResult.Area.PosX; PosY = exploreResult.Area.PosY; Depth = 1; Amount = exploreResult.Amount }
    | Ok _ -> ()
    | Error _ -> return! exploreAndDig client diggerAgent coordinates
}

let createDiggerAgentsPool client diggerAgentsCount = 
    let diggerAgents = 
        [| 1 .. diggerAgentsCount|] 
        |> Seq.map (fun _ -> MailboxProcessor.Start (digger client))
        |> Seq.toArray

    let rec next () = 
        seq {
            for digger in diggerAgents do
                yield digger
            yield! next()
        }

    let enumerator = next().GetEnumerator()
    fun () -> 
        match enumerator.MoveNext() with
        | true -> enumerator.Current
        | false -> raise (InvalidOperationException("Infinite enumerator is not infinite"))

let game (client: Client) = async {
    let diggersCount = 8
    let diggerAgentsPool = createDiggerAgentsPool client diggersCount
    Console.WriteLine("diggers: " + diggersCount.ToString())

    let! licensesResult = client.GetLicenses()
    match licensesResult with
    | Ok licenses -> Console.WriteLine(licenses)
    | Error ex -> Console.WriteLine("Error loading licenses: " + ex.ToString())

    for x in 0 .. 3500 do
        for y in 0 .. 3500 do
            let coordinates = { PosX = x; PosY = y }
            let diggerAgent = diggerAgentsPool()
            exploreAndDig client diggerAgent coordinates |> Async.Start
            do! Async.Sleep(1)

    }

[<EntryPoint>]
let main argv =
    Console.WriteLine("start")
    let urlEnv = Environment.GetEnvironmentVariable("ADDRESS")
    Console.WriteLine("host: " + urlEnv)
    let client = new Client.Client("http://" + urlEnv + ":8000/")
    game client |> Async.RunSynchronously |> ignore
    0 // return an integer exit code
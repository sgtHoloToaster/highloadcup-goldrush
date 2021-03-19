// Learn more about F# at http://docs.microsoft.com/dotnet/fsharp

open System
open Models
open Client
open System.Net.Http
open System.Net

type AreaSize = {
    SizeX: int
    SizeY: int
}

type Coordinates = {
    PosX: int
    PosY: int
}

type ExplorerMessage = {
    PosX: int
    PosY: int
    SizeX: int
    SizeY: int
    Amount: Option<int>
    Retry: int
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

let explorer (client: Client) (diggerAgentsPool: unit -> MailboxProcessor<DiggerMessage>) (digAreaSize: AreaSize) (inbox: MailboxProcessor<ExplorerMessage>) =
    //let rec exploreAndDig (coordinates: Coordinates) = async {
    //    let area = { oneBlockArea with PosX = coordinates.PosX; PosY = coordinates.PosY }
    //    let! result = client.PostExplore(area)
    //    match result with 
    //    | Ok exploreResult when exploreResult.Amount > 0 -> 
    //        diggerAgentsPool().Post { PosX = exploreResult.Area.PosX; PosY = exploreResult.Area.PosY; Depth = 1; Amount = exploreResult.Amount }
    //    | Ok _ -> ()
    //    | Error _ -> return! exploreAndDig coordinates
    //}

    let rec exploreArea (area: Area) = async {
        let! exploreResult = client.PostExplore(area)
        match exploreResult with 
        | Ok _ -> return  exploreResult
        | Error _ -> return! exploreArea area
    }

    let rec exploreOneBlock (coordinates: Coordinates) = 
        exploreArea { oneBlockArea with PosX = coordinates.PosX; PosY = coordinates.PosY }

    let rec exploreAndDigArea (area: Area) (amount: int) (currentCoordinates: Coordinates) = async {
        if amount = 0 then 
            return ()

        Console.WriteLine("explore and dig area: " + currentCoordinates.ToString())
        let! result = exploreOneBlock currentCoordinates
        let left = 
            match result with
            | Ok exploreResult when exploreResult.Amount > 0 -> 
                diggerAgentsPool().Post { PosX = exploreResult.Area.PosX; PosY = exploreResult.Area.PosY; Amount = exploreResult.Amount; Depth = 1 }
                amount - exploreResult.Amount
            | _ -> amount
            
        if left > 0 then
            let maxPosX = area.PosX + area.SizeX
            let maxPosY = area.PosY + area.SizeY
            let newCoordinates = 
                match currentCoordinates.PosX, currentCoordinates.PosY with
                | x, y when x = maxPosX && y = maxPosY -> 
                    Console.WriteLine("Treasures were not found after exploring the entire area")
                    { PosX = area.PosX; PosY = area.PosY }
                | x, y when x = maxPosX -> { PosX = area.PosX; PosY = y + 1 }
                | x, y -> { PosX = x + 1; PosY = y }

            Console.WriteLine("treasures left: " + left.ToString() + " c: " + newCoordinates.ToString())
            return! exploreAndDigArea area left newCoordinates
    }
        

    let rec messageLoop() = async {
        let! msg = inbox.Receive()
        Console.WriteLine("received: " + msg.ToString())
        match msg.SizeX, msg.SizeY, msg.Amount with 
        | _, _, Some 0 -> ()
        | sizeX, sizeY, Some amount when sizeX > digAreaSize.SizeX || sizeY > digAreaSize.SizeY ->
            let maxPosX = msg.PosX + sizeX;
            let maxPosY = msg.PosY + sizeY;
            let stepX = digAreaSize.SizeX
            let stepY = digAreaSize.SizeY
            for x in msg.PosX .. stepX .. maxPosX do
                for y in msg.PosY .. stepY .. maxPosY do
                    let newMsg = { 
                        PosX = x
                        PosY = y 
                        SizeX = Math.Min(stepX, maxPosX - x)
                        SizeY = Math.Min(stepY, maxPosY - y) 
                        Amount = Some amount
                        Retry = 0
                    }
                    inbox.Post newMsg
        | _, _, Some amount ->
            do! exploreAndDigArea { PosX = msg.PosX; PosY = msg.PosY; SizeX = msg.SizeX; SizeY = msg.SizeY } amount { PosX = msg.PosX; PosY = msg.PosY }
        | _, _, None ->
            let! exploreResult = exploreArea { PosX = msg.PosX; PosY = msg.PosY; SizeX = msg.SizeX; SizeY = msg.SizeY }
            match exploreResult with
            | Ok result when result.Amount > 0 -> inbox.Post { msg with Amount = Some result.Amount }
            | Error _ when msg.Retry < 3 -> inbox.Post { msg with Retry = msg.Retry + 1 }
            | _ -> ()
            

        return! messageLoop()
    }

    messageLoop()

let createAgentsPool<'Msg> (body: MailboxProcessor<'Msg> -> Async<unit>) agentsCount = 
    let agents = 
        [| 1 .. agentsCount|] 
        |> Seq.map (fun _ -> MailboxProcessor.Start body)
        |> Seq.toArray

    let rec next () = 
        seq {
            for agent in agents do
                yield agent
            yield! next()
        }

    let enumerator = next().GetEnumerator()
    fun () -> 
        match enumerator.MoveNext() with
        | true -> enumerator.Current
        | false -> raise (InvalidOperationException("Infinite enumerator is not infinite"))

let game (client: Client) = async {
    let diggersCount = 8
    let explorersCount = 1000
    let diggerAgentsPool = createAgentsPool (digger client) diggersCount
    Console.WriteLine("diggers: " + diggersCount.ToString())

    let! licensesResult = client.GetLicenses()
    match licensesResult with
    | Ok licenses -> Console.WriteLine(licenses)
    | Error ex -> Console.WriteLine("Error loading licenses: " + ex.ToString())

    let digAreaSize = { SizeX = 5; SizeY = 1 }
    let explorerAgentsPool = createAgentsPool (explorer client diggerAgentsPool digAreaSize) explorersCount
    for x in 0 .. digAreaSize.SizeX * 3 .. 3500 do
        for y in 0 .. digAreaSize.SizeY * 3 .. 3500 do
            let msg = { PosX = x; PosY = y; SizeX = digAreaSize.SizeX; SizeY = digAreaSize.SizeY; Amount = None; Retry = 0 }
            explorerAgentsPool().Post msg
            do! Async.Sleep(10)

    }

[<EntryPoint>]
let main argv =
    Console.WriteLine("start")
    let urlEnv = Environment.GetEnvironmentVariable("ADDRESS")
    Console.WriteLine("host: " + urlEnv)
    let client = new Client.Client("http://" + urlEnv + ":8000/")
    game client |> Async.RunSynchronously |> ignore
    0 // return an integer exit code
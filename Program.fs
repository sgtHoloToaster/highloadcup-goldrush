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

type TreasureRetryMessage = {
    Treasure: string
    Retry: int
}

let digger (client: Client) (treasureResender: MailboxProcessor<TreasureRetryMessage>) (inbox: MailboxProcessor<DiggerMessage>) = 
    let inline doDig licenseId msg = async {
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
                               | _ -> treasureResender.Post { Treasure = treasure; Retry = 0 }; 1
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

            return! exploreAndDigArea area left newCoordinates
    }
        
    let inline exploreMessageArea (msg: ExplorerMessage) = async {
        let! exploreResult = exploreArea { PosX = msg.PosX; PosY = msg.PosY; SizeX = msg.SizeX; SizeY = msg.SizeY }
        match exploreResult with
        | Ok result when result.Amount > 0 -> inbox.Post { msg with Amount = Some result.Amount }
        | Error _ when msg.Retry < 3 -> inbox.Post { msg with Retry = msg.Retry + 1 }
        | _ -> ()
    }

    let inline processMessage (msg: ExplorerMessage) = async {
        match msg.SizeX, msg.SizeY, msg.Amount with 
        | _, _, Some 0 -> ()
        | sizeX, sizeY, Some _ when sizeX > digAreaSize.SizeX || sizeY > digAreaSize.SizeY ->
            let maxPosX = msg.PosX + sizeX;
            let maxPosY = msg.PosY + sizeY;
            let stepX = digAreaSize.SizeX
            let stepY = digAreaSize.SizeY
            let maxIterX = maxPosX - stepX
            let maxIterY = maxPosY - stepY
            for x in msg.PosX .. stepX .. maxIterX do
                for y in msg.PosY .. stepY .. maxIterY do
                    let newMsg = { 
                        PosX = x
                        PosY = y 
                        SizeX = Math.Min(stepX, maxPosX - x)
                        SizeY = Math.Min(stepY, maxPosY - y) 
                        Amount = None
                        Retry = 0
                    }
                    inbox.Post newMsg
        | _, _, Some amount ->
            exploreAndDigArea { PosX = msg.PosX; PosY = msg.PosY; SizeX = msg.SizeX; SizeY = msg.SizeY } amount { PosX = msg.PosX; PosY = msg.PosY } |> Async.Start
        | _, _, None ->
            exploreMessageArea msg |> Async.Start
    }

    let rec messageLoop() = async {
        let! priorityMessage = inbox.TryScan((fun msg ->
            match msg.Amount with
            | Some _ -> (Some (async { return msg }))
            | None -> None), 0)

        let! msg = async {
            match priorityMessage with
            | Some prMsg -> return prMsg
            | _ -> return! inbox.Receive()
        }

        Console.WriteLine("received: " + msg.ToString())
        do! processMessage msg
            
        return! messageLoop()
    }

    messageLoop()

let treasureResender (client: Client) (inbox: MailboxProcessor<TreasureRetryMessage>) =
    let rec messageLoop() = async {
        let! msg = inbox.Receive()
        let! result = client.PostCash msg.Treasure
        match result, msg.Retry with
        | Ok _, _ -> ()
        | Error _, 3 -> ()
        | _ -> inbox.Post { msg with Retry = msg.Retry + 1 }

        return! messageLoop()
    }
        
    messageLoop()

let inline infiniteEnumerator elements = 
    let rec next () = 
        seq {
            for element in elements do
                yield element
            yield! next()
        }

    let enumerator = next().GetEnumerator()
    fun () -> 
        match enumerator.MoveNext() with
        | true -> enumerator.Current
        | false -> raise (InvalidOperationException("Infinite enumerator is not infinite"))

let inline createAgentsPool<'Msg> (body: MailboxProcessor<'Msg> -> Async<unit>) agentsCount = 
    let agents = 
        [| 1 .. agentsCount|] 
        |> Seq.map (fun _ -> MailboxProcessor.Start body)
        |> Seq.toArray

    infiniteEnumerator agents

let inline generateRange (startNumber: int) (increasePattern: int seq) (endNumber: int) = 
    let enumerator = infiniteEnumerator increasePattern
    let rec increase current = seq {
        let step = enumerator()
        let newCurrent = current + step
        if newCurrent > endNumber then
            yield (current, endNumber - current)
        else
            yield (newCurrent, step)
            if newCurrent <> endNumber then
                yield! increase newCurrent
    }
        
    increase startNumber

let inline exploreField (explorerAgentsPool: (unit -> MailboxProcessor<ExplorerMessage>)) (timeout: int) (startCoordinates: Coordinates) (endCoordinates: Coordinates) (stepX: int) (stepY: int) = async {
    let maxPosX = endCoordinates.PosX - stepX
    let maxPosY = endCoordinates.PosY - stepY
    for x in startCoordinates.PosX .. stepX .. maxPosX do
        for y in startCoordinates.PosY .. stepY .. maxPosY do
            let msg = { PosX = x; PosY = y; SizeX = stepX; SizeY = stepY; Amount = None; Retry = 0 }
            explorerAgentsPool().Post msg
            do! Async.Sleep(timeout)
    }

let inline game (client: Client) = async {
    let diggersCount = 8
    let explorersCount = 1000
    let treasureResenderAgent = MailboxProcessor.Start (treasureResender client)
    let diggerAgentsPool = createAgentsPool (digger client treasureResenderAgent) diggersCount
    Console.WriteLine("diggers: " + diggersCount.ToString())

    let digAreaSize = { SizeX = 5; SizeY = 1 }
    let explorerAgentsPool = createAgentsPool (explorer client diggerAgentsPool digAreaSize) explorersCount
    do! exploreField explorerAgentsPool 10 { PosX = 0; PosY = 0 } { PosX = 3500; PosY = 3500 } 5 1
    do! Async.Sleep(Int32.MaxValue)
}

[<EntryPoint>]
let main argv =
    Console.WriteLine("start")
    let urlEnv = Environment.GetEnvironmentVariable("ADDRESS")
    Console.WriteLine("host: " + urlEnv)
    let client = new Client.Client("http://" + urlEnv + ":8000/")
    game client |> Async.RunSynchronously |> ignore
    0 // return an integer exit code
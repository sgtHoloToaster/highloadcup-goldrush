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

type TreasureReportMessage = {
    Depth: int
    Coins: int
}


type ExplorerMessage = {
    PosX: int
    PosY: int
    SizeX: int
    SizeY: int
    Retry: int
}

type DiggerDigMessage = {
    PosX: int
    PosY: int
    Depth: int
    Amount: int
}

type DiggerMessage = DiggerDigMessage of DiggerDigMessage | DiggerOptimalDepthMessage of int


type DiggingDepthOptimizerMessage = TreasureReport of TreasureReportMessage | DiggerRegistration of MailboxProcessor<DiggerMessage>

type TreasureRetryMessage = {
    Treasure: string
    Retry: int
}

let digger (client: Client) (treasureResender: MailboxProcessor<TreasureRetryMessage>) (diggingDepthOptimizer: MailboxProcessor<DiggingDepthOptimizerMessage>) (inbox: MailboxProcessor<DiggerMessage>) = 
    let inline doDig licenseId msg = async {
        let dig = { LicenseID = licenseId; PosX = msg.PosX; PosY = msg.PosY; Depth = msg.Depth }
        let! treasuresResult = client.PostDig dig
        match treasuresResult with 
        | Error ex -> 
            match ex with 
            | :? HttpRequestException as ex when ex.StatusCode.HasValue ->
                match ex.StatusCode.Value with 
                | HttpStatusCode.NotFound -> inbox.Post (DiggerMessage.DiggerDigMessage ({ msg with Depth = msg.Depth + 1 }))
                | HttpStatusCode.UnprocessableEntity -> ()
                | _ -> inbox.Post (DiggerMessage.DiggerDigMessage (msg)) // retry
            | _ -> inbox.Post (DiggerMessage.DiggerDigMessage (msg)) // retry
        | Ok treasures -> 
            let digged = 
                treasures.Treasures 
                |> Seq.map (
                    fun treasure -> async {
                        let! result = client.PostCash treasure
                        return match result with 
                               | Ok coins -> diggingDepthOptimizer.Post (DiggingDepthOptimizerMessage.TreasureReport { Depth = msg.Depth; Coins = coins |> Seq.length }); 1
                               | _ -> treasureResender.Post { Treasure = treasure; Retry = 0 }; 1
                    })
                |> Async.Parallel
                |> Async.RunSynchronously
                |> Seq.sum

            inbox.Post (DiggerMessage.DiggerDigMessage (({ msg with Depth = msg.Depth + 1; Amount = msg.Amount - digged })))
    }

    let rec messageLoop (license: License) (optimalDepth: int option): Async<unit> = async {
        let! (newLicense, optimalDepth) = async {
            if license.Id.IsSome && license.DigAllowed > license.DigUsed then
                let! priorityMessage = async {
                    if optimalDepth.IsSome then 
                        return! inbox.TryScan((fun msg ->
                            match msg with
                            | DiggerMessage.DiggerDigMessage digMsg when digMsg.Depth <= optimalDepth.Value -> (Some (async { return msg }))
                            | _ -> None), 0)
                    else return None
                }

                let! msg = async {
                    match priorityMessage with
                    | Some priorityMessage -> return priorityMessage
                    | _ -> return! inbox.Receive()
                }

                match msg with
                | DiggerDigMessage digMsg ->
                    if digMsg.Amount > 0 && digMsg.Depth <= 10 then
                        doDig license.Id.Value digMsg |> Async.Start
                        return { license with DigUsed = license.DigUsed + 1 }, optimalDepth
                    else
                        return license, optimalDepth
                | DiggerOptimalDepthMessage optimalDepth ->
                    return license, (Some optimalDepth)
            else 
                let! licenseUpdateResult = client.PostLicense Seq.empty<int>
                return match licenseUpdateResult with 
                       | Ok newLicense -> newLicense, optimalDepth
                       | Error ex -> license, optimalDepth
        }

        return! messageLoop newLicense optimalDepth
    }

    diggingDepthOptimizer.Post (DiggingDepthOptimizerMessage.DiggerRegistration inbox)
    messageLoop { Id = None; DigAllowed = 0; DigUsed = 0 } None

let explorer (client: Client) (diggerAgentsPool: unit -> MailboxProcessor<DiggerMessage>) (digAreaSize: AreaSize) (inbox: MailboxProcessor<ExplorerMessage>) =
    let rec exploreArea (area: Area) = async {
        let! exploreResult = client.PostExplore(area)
        match exploreResult with 
        | Ok _ -> return  exploreResult
        | Error _ -> return! exploreArea area
    }

    let rec exploreOneBlock (coordinates: Coordinates) = 
        exploreArea { oneBlockArea with PosX = coordinates.PosX; PosY = coordinates.PosY }

    let rec exploreAndDigAreaByBlocks (area: Area) (amount: int) (currentCoordinates: Coordinates): Async<int> = async {
        Console.WriteLine("explore and dig by blocks: " + currentCoordinates.ToString())
        let! result = exploreOneBlock currentCoordinates
        let left = 
            match result with
            | Ok exploreResult when exploreResult.Amount > 0 -> 
                diggerAgentsPool().Post (DiggerMessage.DiggerDigMessage ({ PosX = exploreResult.Area.PosX; PosY = exploreResult.Area.PosY; Amount = exploreResult.Amount; Depth = 1 }))
                amount - exploreResult.Amount
            | _ -> amount
            
        if left = 0 then
            return amount
        else
            let maxPosX = area.PosX + area.SizeX
            let maxPosY = area.PosY + area.SizeY
            let newCoordinates = 
                match currentCoordinates.PosX, currentCoordinates.PosY with
                | x, y when x = maxPosX && y = maxPosY -> None
                | x, y when x = maxPosX -> Some { PosX = area.PosX; PosY = y + 1 }
                | x, y -> Some { PosX = x + 1; PosY = y }

            match newCoordinates with
            | None -> return amount - left
            | Some newCoordinates -> 
                return 1 + (exploreAndDigAreaByBlocks area left newCoordinates |> Async.RunSynchronously)
    }

    let rec exploreAndDigArea (area: Area): Async<int> = async {
        let! result = exploreArea area
        match result with
        | Ok exploreResult -> 
            match exploreResult.Amount, area.SizeX, area.SizeY with
            | 0, _, _ -> return 0
            | amount, x, _ when x > digAreaSize.SizeX ->
                let firstArea = { 
                    area with SizeX = (Math.Floor((area.SizeX |> double) / 2.0) |> int) 
                }
                let secondArea = { 
                    area with SizeX = (Math.Ceiling((area.SizeX |> double) / 2.0) |> int) 
                              PosX = firstArea.PosX + firstArea.SizeX
                }
                let! firstResult = exploreAndDigArea firstArea
                let! secondResult = async {
                    if firstResult = amount then return 0
                    else return! exploreAndDigArea secondArea
                }
                return firstResult + secondResult
            | amount, _, y when y > digAreaSize.SizeY ->
                let firstArea = { 
                    area with SizeY = (Math.Floor((area.SizeY |> double) / 2.0) |> int) 
                }
                let secondArea = { 
                    area with SizeY = (Math.Ceiling((area.SizeY |> double) / 2.0) |> int) 
                              PosY = firstArea.PosY + firstArea.SizeY
                }
                let! firstResult = exploreAndDigArea firstArea
                let! secondResult = async {
                    if firstResult = amount then return 0
                    else return! exploreAndDigArea secondArea
                }
                return firstResult + secondResult
            | amount, _, _ ->
                return! exploreAndDigAreaByBlocks area amount { PosX = area.PosX; PosY = area.PosY }
        | Error _ -> return! exploreAndDigArea area
    }
        
    let rec messageLoop() = async {
        let! msg = inbox.Receive()
        let area = { PosX = msg.PosX; PosY = msg.PosY; SizeX = msg.SizeX; SizeY = msg.SizeY }
        do! exploreAndDigArea area |> Async.Ignore
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

let diggingDepthOptimizer (inbox: MailboxProcessor<DiggingDepthOptimizerMessage>) =
    let rec messageLoop (diggers: MailboxProcessor<DiggerMessage> seq) (treasuresCost: Map<int, int>) = async {
        if treasuresCost.Count = 10 then
            Console.WriteLine("diggers: " + (diggers |> Seq.length).ToString() + " time: " + DateTime.UtcNow.ToString())
            let optimalDepth = treasuresCost |> Seq.sortBy (fun kv -> kv.Key) 
                                             |> Seq.last
            for digger in diggers do
                digger.Post (DiggerMessage.DiggerOptimalDepthMessage optimalDepth.Value)
            return ()

        let! msg = inbox.Receive()
        let newDiggers, newTreasuresCost = 
            match msg with 
            | DiggingDepthOptimizerMessage.TreasureReport treasuresMsg ->
                let newTreasuresCost = 
                    if treasuresCost.ContainsKey treasuresMsg.Depth then treasuresCost
                    else treasuresCost.Add(treasuresMsg.Depth, treasuresMsg.Coins)
                diggers, newTreasuresCost
            | DiggingDepthOptimizerMessage.DiggerRegistration digger ->
                 diggers |> Seq.append [digger], treasuresCost

        return! messageLoop newDiggers newTreasuresCost
    }
        
    messageLoop Seq.empty Map.empty

let inline exploreField (explorerAgentsPool: (unit -> MailboxProcessor<ExplorerMessage>)) (timeout: int) (startCoordinates: Coordinates) (endCoordinates: Coordinates) (stepX: int) (stepY: int) = async {
    let maxPosX = endCoordinates.PosX - stepX
    let maxPosY = endCoordinates.PosY - stepY
    for x in startCoordinates.PosX .. stepX .. maxPosX do
        for y in startCoordinates.PosY .. stepY .. maxPosY do
            let msg = { PosX = x; PosY = y; SizeX = stepX; SizeY = stepY; Retry = 0 }
            explorerAgentsPool().Post msg
            do! Async.Sleep(timeout)
    }

let inline game (client: Client) = async {
    let diggersCount = 8
    let explorersCount = 1000
    let treasureResenderAgent = MailboxProcessor.Start (treasureResender client)
    let diggingDepthOptimizerAgent = MailboxProcessor.Start (diggingDepthOptimizer)
    let diggerAgentsPool = createAgentsPool (digger client treasureResenderAgent diggingDepthOptimizerAgent) diggersCount
    Console.WriteLine("diggers: " + diggersCount.ToString())

    let digAreaSize = { SizeX = 2; SizeY = 2 }
    let explorerAgentsPool = createAgentsPool (explorer client diggerAgentsPool digAreaSize) explorersCount
    do! exploreField explorerAgentsPool 10 { PosX = 0; PosY = 0 } { PosX = 3500; PosY = 3500 } 4 4
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
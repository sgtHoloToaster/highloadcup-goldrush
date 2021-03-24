// Learn more about F# at http://docs.microsoft.com/dotnet/fsharp

open System
open Client
open System.Net.Http
open System.Net
open FSharp.Control.Tasks.V2

[<Struct>]
type Coordinates = {
    PosX: int
    PosY: int
}

[<Struct>]
type TreasureReportMessage = {
    Depth: int
    Coins: int
}

[<Struct>]
type DiggerDigMessage = {
    PosX: int
    PosY: int
    Depth: int
    Amount: int
}

[<Struct>]
type License = {
    Id: int
    DigUsed: int
    DigAllowed: int
}

[<Struct>]
type DiggerMessage = 
    DiggerDigMessage of DiggerDigMessage 
    | DiggerOptimalDepthMessage of depth: int 
    | AddCoinsToBuyLicense of int * int seq * bool
    | SetTreasureReport of treasure: bool

[<Struct>]
type DiggingDepthOptimizerMessage = TreasureReport of report: TreasureReportMessage | DiggerRegistration of MailboxProcessor<DiggerMessage>

[<Struct>]
type DiggingLicenseCostOptimizerMessage = GetCoins of MailboxProcessor<DiggerMessage> | LicenseIsBought of int * LicenseDto

[<Struct>]
type TreasureRetryMessage = {
    Treasure: string
    Retry: int
}

[<Struct>]
type DiggerState = {
    License: License option
    OptimalDepth: int option
    Coins: int seq
    OptimalLicenseCost: int
    ReportTreasure: bool
    ReportLicense: bool
}

let mutable isStarted = false

let persistentClient = new HttpClient(Timeout=TimeSpan.FromSeconds(300.0))
let nonPersistentClient = new HttpClient(Timeout=TimeSpan.FromSeconds(30.0))
let absolutelyNonPersistentClient = new HttpClient(Timeout=TimeSpan.FromSeconds(0.5))
let postCash = Client.postCash persistentClient
let postDig = Client.postDig persistentClient
let postLicense = Client.postLicense persistentClient
let postExplore = Client.postExplore absolutelyNonPersistentClient
let getBalance() = Client.getBalance nonPersistentClient

let digger (treasureResender: MailboxProcessor<TreasureRetryMessage>) 
        (diggingDepthOptimizer: MailboxProcessor<DiggingDepthOptimizerMessage>) 
        (diggingLicenseCostOptimizer: MailboxProcessor<DiggingLicenseCostOptimizerMessage>) 
        (inbox: MailboxProcessor<DiggerMessage>) = 
    let inline postTreasure depth reportTreasure treasure = async {
        let! result = postCash treasure |> Async.AwaitTask
        return match result with 
                | Ok coins -> 
                    if reportTreasure then
                        diggingDepthOptimizer.Post (TreasureReport { Depth = depth; Coins = coins |> Seq.length })
                | _ -> treasureResender.Post { Treasure = treasure; Retry = 0 }
    }

    let inline doDig (license: License) reportTreasure msg = async {
        let dig = { licenseID = license.Id; posX = msg.PosX; posY = msg.PosY; depth = msg.Depth }
        let! treasuresResult = postDig dig |> Async.AwaitTask
        match treasuresResult with 
        | Error ex -> 
            match ex with 
            | :? HttpRequestException as ex when ex.StatusCode.HasValue ->
                match ex.StatusCode.Value with 
                | HttpStatusCode.NotFound -> 
                    if msg.Depth < 10 then
                        inbox.Post (DiggerMessage.DiggerDigMessage ({ msg with Depth = msg.Depth + 1 }))
                | HttpStatusCode.UnprocessableEntity -> Console.WriteLine("unprocessable: " + msg.ToString())
                | HttpStatusCode.Forbidden -> Console.WriteLine("Forbidden for: " + license.ToString())
                | _ -> inbox.Post (DiggerDigMessage (msg)) // retry
            | _ -> inbox.Post (DiggerDigMessage (msg)) // retry
        | Ok treasures -> 
            treasures.treasures 
            |> Seq.map (postTreasure msg.Depth reportTreasure)
            |> Async.Parallel
            |> Async.Ignore
            |> Async.StartImmediate
            let digged = treasures.treasures |> Seq.length
            if digged < msg.Amount then
                inbox.Post (DiggerDigMessage (({ msg with Depth = msg.Depth + 1; Amount = msg.Amount - digged }))) 
    }

    let inline exactDepthMessage depth msg =
        match msg with
        | DiggerDigMessage digMsg when digMsg.Depth = depth -> (Some (async { return msg }))
        | _ -> None

    let inline lessThanDepthMessage depth msg = 
        match msg with
        | DiggerDigMessage digMsg when digMsg.Depth <= depth -> (Some (async { return msg }))
        | _ -> None

    let inline tryGetPriorityMessage (optimalDepth: int option) = async {
        if optimalDepth.IsSome then 
            return! inbox.TryScan(lessThanDepthMessage optimalDepth.Value, 0)
        else return None
    }

    let inline receiveMessage optimalDepth = async {
        let! priorityMessage = tryGetPriorityMessage optimalDepth

        return! async {
            match priorityMessage with
            | Some priorityMessage -> return priorityMessage
            | _ -> return! inbox.Receive()
        }
    }

    let inline addCoinsMessage msg =
        match msg with
        | AddCoinsToBuyLicense (optimalLicenseCost, coins, setLicenseReport) -> Some (async { return optimalLicenseCost, coins, setLicenseReport })
        | _ -> None

    let rec messageLoop (state: DiggerState): Async<unit> = async {
        let! newState = async {
            if state.License.IsSome && state.License.Value.DigAllowed > state.License.Value.DigUsed then
                let! msg = receiveMessage state.OptimalDepth
                match msg with
                | DiggerDigMessage digMsg ->
                    if digMsg.Amount > 0 && digMsg.Depth <= 10 then
                        doDig state.License.Value state.ReportTreasure digMsg |> Async.StartImmediate
                        return { state with License = Some { state.License.Value with DigUsed = state.License.Value.DigUsed + 1 } }
                    else
                        return state
                | DiggerOptimalDepthMessage optimalDepth ->
                    return { state with OptimalDepth = (Some optimalDepth); ReportTreasure = false }
                | AddCoinsToBuyLicense (optimalLicenseCost, newCoins, setLicenseReport) ->
                    return { state with Coins = state.Coins |> Seq.append newCoins; OptimalLicenseCost = optimalLicenseCost; ReportLicense = setLicenseReport }
                | SetTreasureReport value -> return { state with ReportTreasure = value }
            else
                let! (optimalLicenseCost, coins, setLicenseReport) = async {
                    if not(Seq.isEmpty state.Coins) then return state.OptimalLicenseCost, state.Coins, state.ReportLicense
                    else 
                        let! result = inbox.TryScan(addCoinsMessage, 0)
                        return 
                            match result with
                            | Some (optimalLicenseCost, newCoins, setLicenseReport) -> optimalLicenseCost, newCoins, setLicenseReport
                            | _ -> state.OptimalLicenseCost, Seq.empty, state.ReportLicense
                }

                let coinsToBuyLicense = coins |> Seq.truncate optimalLicenseCost
                let coinsToBuyLicenseCount = coinsToBuyLicense |> Seq.length
                let! licenseUpdateResult = postLicense coinsToBuyLicense |> Async.AwaitTask
                let coinsLeft = coins |> Seq.skip coinsToBuyLicenseCount
                return match licenseUpdateResult with 
                       | Ok newLicense -> 
                            if state.ReportLicense && coinsToBuyLicenseCount > 0 then
                                diggingLicenseCostOptimizer.Post (LicenseIsBought(coinsToBuyLicenseCount, newLicense))
                            
                            if isStarted && coinsLeft |> Seq.isEmpty then
                                diggingLicenseCostOptimizer.Post (GetCoins inbox)
                            { state with License = Some { Id = newLicense.id; DigAllowed = newLicense.digAllowed; DigUsed = 0 }
                                         OptimalLicenseCost = optimalLicenseCost; Coins = coinsLeft; ReportLicense = setLicenseReport }
                       | Error _ -> { state with OptimalLicenseCost = optimalLicenseCost; Coins = coinsLeft; ReportLicense = setLicenseReport }
        }

        return! messageLoop newState
    }

    diggingDepthOptimizer.Post (DiggingDepthOptimizerMessage.DiggerRegistration inbox)
    messageLoop { License = None; OptimalDepth = None; Coins = Seq.empty; OptimalLicenseCost = 1; ReportTreasure = true; ReportLicense = true }

let oneBlockArea = { posX = 0; posY = 0; sizeX = 1; sizeY = 1 }
let inline explore (diggersManager: MailboxProcessor<DiggerMessage>) (defaultErrorTimeout: int) (area: AreaDto) =
    let rec exploreArea (area: AreaDto) = async {
        let! exploreResult = postExplore(area) |> Async.AwaitTask
        match exploreResult with 
        | Ok _ -> return  exploreResult
        | Error _ -> return! exploreArea area
    }

    let exploreOneBlock (coordinates: Coordinates) = 
        exploreArea { oneBlockArea with posX = coordinates.PosX; posY = coordinates.PosY }

    let rec exploreAndDigAreaByBlocks (area: AreaDto) (amount: int) (currentCoordinates: Coordinates): Async<int> = async {
        let! result = exploreOneBlock currentCoordinates
        let left = 
            match result with
            | Ok exploreResult when exploreResult.amount > 0 -> 
                diggersManager.Post (DiggerMessage.DiggerDigMessage ({ PosX = currentCoordinates.PosX; PosY = currentCoordinates.PosY; Amount = exploreResult.amount; Depth = 1 }))
                amount - exploreResult.amount
            | _ -> amount
            
        if left = 0 then
            return amount
        else
            let maxPosX = area.posX + area.sizeX
            let newCoordinates = 
                if currentCoordinates.PosX = maxPosX then None
                else Some { currentCoordinates with PosX = currentCoordinates.PosX + 1 }

            let digged = amount - left
            match newCoordinates with
            | None -> return digged
            | Some newCoordinates -> 
                return digged + (exploreAndDigAreaByBlocks area left newCoordinates |> Async.RunSynchronously)
    }

    let rec exploreAndDigArea  (errorTimeout: int) (area: AreaDto): Async<int> = async {
        let! result = exploreArea area
        match result with
        | Ok exploreResult -> 
            if exploreResult.amount = 0 then return 0
            else return! exploreAndDigAreaByBlocks area exploreResult.amount { PosX = area.posX; PosY = area.posY }
        | Error _ -> 
            do! Async.Sleep errorTimeout
            return! exploreAndDigArea (int(Math.Pow(float errorTimeout, 2.0))) area
    }
    
    exploreAndDigArea defaultErrorTimeout area

let treasureResender (inbox: MailboxProcessor<TreasureRetryMessage>) =
    let rec messageLoop() = async {
        let! msg = inbox.Receive()
        let! result = postCash msg.Treasure |> Async.AwaitTask
        match result, msg.Retry with
        | Ok _, _ -> ()
        | Error _, 1 -> ()
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

let inline createAgents (body: MailboxProcessor<'Msg> -> Async<unit>) agentsCount =
    [| 1 .. agentsCount|] 
    |> Seq.map (fun _ -> MailboxProcessor.Start body)
    |> Seq.toArray

let inline createAgentsPool<'Msg> (body: MailboxProcessor<'Msg> -> Async<unit>) agentsCount = 
    createAgents body agentsCount |> infiniteEnumerator

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

[<Struct>]
type LicensesCostOptimizerState = {
    LicensesCost: Map<int, int>
    ExploreCost: int
    OptimalCost: int option
    Wallet: int seq
}

let diggingLicensesCostOptimizer (maxExploreCost: int) (inbox: MailboxProcessor<DiggingLicenseCostOptimizerMessage>) =
    let rec getBalanceFromServer() = async {
        let! result = getBalance() |> Async.AwaitTask
        match result with
        | Ok balance -> return balance
        | Error _ -> return! getBalanceFromServer()
    }
    
    let inline getWallet state coinsNeeded = async {
        if state.Wallet |> Seq.length >= coinsNeeded then return state.Wallet
        else return! async {
            let! balance = getBalanceFromServer()
            return balance.wallet
        }
    }
    
    let inline sendCoins state (digger: MailboxProcessor<DiggerMessage>) = async {
        let licenseCost = if state.OptimalCost.IsSome then state.OptimalCost.Value else state.ExploreCost
        let! wallet = getWallet state licenseCost
        let coinsCount = wallet |> Seq.length
        if coinsCount >= licenseCost then
            return 
                if state.OptimalCost.IsNone then
                    digger.Post (AddCoinsToBuyLicense (licenseCost, (wallet |> Seq.truncate licenseCost), true))
                    { state with ExploreCost = state.ExploreCost + 1; Wallet = wallet |> Seq.skip licenseCost }
                else
                    let minCoins = Math.Min(coinsCount, 250)
                    let maxCoins = Math.Max(coinsCount / 2, minCoins)
                    digger.Post (AddCoinsToBuyLicense (state.OptimalCost.Value, wallet |> Seq.truncate maxCoins, false))
                    { state with Wallet = wallet |> Seq.skip maxCoins }
        else return state
    }    
    
    let inline processMessage state msg = async {
        match msg with
        | GetCoins digger -> return! sendCoins state digger
        | LicenseIsBought (licenseCost, license) -> 
            return 
                if state.OptimalCost.IsSome then Console.WriteLine("license: " + DateTime.Now.ToString()); state
                else
                    let newState = if state.LicensesCost.ContainsKey licenseCost then state
                                    else { state with LicensesCost = (state.LicensesCost.Add (licenseCost, license.digAllowed)) }
                    if newState.LicensesCost |> Map.count >= maxExploreCost then
                        let (optimalCost, _) = 
                            newState.LicensesCost 
                            |> Map.toSeq 
                            |> Seq.map (fun (cost, digAllowed) -> cost, (float cost / (float digAllowed)))
                            |> Seq.sortBy (fun (cost, costPerDig) -> costPerDig, cost)
                            |> Seq.find (fun _ -> true)
                        Console.WriteLine("optimal cost is " + optimalCost.ToString() + " time: " + DateTime.Now.ToString())
                        { newState with OptimalCost = Some optimalCost }
                    else newState
    }

    let rec messageLoop (state: LicensesCostOptimizerState) = async {
        let! msg = inbox.Receive()
        Console.WriteLine("license: " + DateTime.Now.ToString() + " msg: " + msg.ToString())
        let! newState = processMessage state msg
        do! Async.Sleep 10
        return! messageLoop newState
    }
        
    messageLoop { LicensesCost = Map.empty; ExploreCost = 1; OptimalCost = None; Wallet = Seq.empty }

let depthCoefs = Map[1, 1.0; 2, 0.97; 3, 0.95; 4, 0.92; 5, 0.89; 6, 0.83; 7, 0.78; 8, 0.73; 9, 0.68; 10, 0.6]
let diggingDepthOptimizer (inbox: MailboxProcessor<DiggingDepthOptimizerMessage>) =
    let rec messageLoop (diggers: MailboxProcessor<DiggerMessage> seq) (treasuresCost: Map<int, int>) = async {
        let! msg = inbox.Receive()
        let! newDiggers, newTreasuresCost = async {
            match msg with 
            | TreasureReport treasuresMsg ->
                let newTreasuresCost = 
                    if treasuresCost.ContainsKey treasuresMsg.Depth then treasuresCost
                    else treasuresCost.Add(treasuresMsg.Depth, treasuresMsg.Coins)
                if treasuresCost.Count = 10 then
                    let sorted = treasuresCost |> Seq.mapFold (fun state kv -> ((kv.Key, state + kv.Value), state + kv.Value)) 0
                                                |> (fun (agg, _) -> agg |> Seq.sortBy (fun (key, value) -> ((float value) / (float key)) * depthCoefs.[key]))
                    let log = String.Join(",", (sorted |> Seq.toArray))
                    let (optimalDepth, _) = sorted |> Seq.last
                    Console.WriteLine(log)
                    Console.WriteLine("optimal is: " + optimalDepth.ToString())
                    for digger in diggers do
                        digger.Post (DiggerOptimalDepthMessage optimalDepth)

                    do! Async.Sleep 60000
                    for digger in diggers do digger.Post (SetTreasureReport true)
                    return diggers, Map.empty
                else return diggers, newTreasuresCost
            | DiggingDepthOptimizerMessage.DiggerRegistration digger ->
                return diggers |> Seq.append [digger], treasuresCost
        }

        return! messageLoop newDiggers newTreasuresCost
    }
        
    messageLoop Seq.empty Map.empty

let diggersManager (diggerAgentsPool: unit -> MailboxProcessor<DiggerMessage>) (inbox: MailboxProcessor<DiggerMessage>) =
    let rec messageLoop() = async {
        let! msg = inbox.Receive()
        diggerAgentsPool().Post msg
        return! messageLoop()
    }

    messageLoop()

let inline exploreField (explorer: AreaDto -> Async<int>) (timeout: int) (startCoordinates: Coordinates) (endCoordinates: Coordinates) (stepX: int) (stepY: int) = task {
    let maxPosX = endCoordinates.PosX - stepX
    let maxPosY = endCoordinates.PosY - stepY
    let areas = seq {
        for x in startCoordinates.PosX .. stepX .. maxPosX do
            for y in startCoordinates.PosY .. stepY .. maxPosY do
                yield { posX = x; posY = y; sizeX = stepX; sizeY = stepY }
    }

    for area in areas do
        do! explorer area |> Async.Ignore
        //let timeout = timeout + int (Math.Pow(float(area.posX - startCoordinates.PosX), 2.0)) / 3000
        //Console.WriteLine("timeout: " + timeout.ToString())
        //do! Async.Sleep(timeout)
    return [1]
}

let inline game() = task {
    let diggersCount = 10
    let treasureResenderAgent = MailboxProcessor.Start treasureResender
    let diggingDepthOptimizerAgent = MailboxProcessor.Start (diggingDepthOptimizer)
    let diggingLicenseCostOptimizerAgent = MailboxProcessor.Start (diggingLicensesCostOptimizer 50)
    let diggerAgentsPool = createAgentsPool (digger treasureResenderAgent diggingDepthOptimizerAgent diggingLicenseCostOptimizerAgent) diggersCount
    let diggersManager = MailboxProcessor.Start (diggersManager diggerAgentsPool)
    Console.WriteLine("diggers: " + diggersCount.ToString())

    let explorer = explore diggersManager 3
    async {
        do! Async.Sleep 5000
        isStarted <- true
    } |> Async.Start

    let tasks = [
        exploreField explorer 14 { PosX = 3001; PosY = 0 } { PosX = 3500; PosY = 3500 } 5 1
        exploreField explorer 14 { PosX = 2501; PosY = 0 } { PosX = 3000; PosY = 3500 } 5 1
        exploreField explorer 14 { PosX = 1531; PosY = 0 } { PosX = 2500; PosY = 3500 } 5 1
        exploreField explorer 14 { PosX = 1511; PosY = 0 } { PosX = 1530; PosY = 3500 } 5 1
        exploreField explorer 8 { PosX = 1501; PosY = 0 } { PosX = 1510; PosY = 3500 } 5 1
        exploreField explorer 5 { PosX = 0; PosY = 0 } { PosX = 1500; PosY = 3500 } 5 1
    ]

    let! _ = (tasks |> System.Threading.Tasks.Task.WhenAll)
    do! Async.Sleep(Int32.MaxValue)
}

[<EntryPoint>]
let main argv =
  task {
    //do! Async.SwitchToThreadPool ()
    return! game()
  } |> Async.AwaitTask |> Async.RunSynchronously
  0
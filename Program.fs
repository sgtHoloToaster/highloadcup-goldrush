// Learn more about F# at http://docs.microsoft.com/dotnet/fsharp

open System
open Client
open System.Net.Http
open System.Net

type AreaSize = {
    SizeX: int
    SizeY: int
}

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

type DiggerMessage = DiggerDigMessage of DiggerDigMessage | DiggerOptimalDepthMessage of int | AddCoinsToBuyLicense of int * int seq
type DiggingDepthOptimizerMessage = TreasureReport of TreasureReportMessage | DiggerRegistration of MailboxProcessor<DiggerMessage>
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
}

let digger (client: Client) 
        (treasureResender: MailboxProcessor<TreasureRetryMessage>) 
        (diggingDepthOptimizer: MailboxProcessor<DiggingDepthOptimizerMessage>) 
        (diggingLicenseCostOptimizer: MailboxProcessor<DiggingLicenseCostOptimizerMessage>) 
        (inbox: MailboxProcessor<DiggerMessage>) = 
    let inline doDig (license: License) msg = async {
        let dig = { licenseID = license.Id; posX = msg.PosX; posY = msg.PosY; depth = msg.Depth }
        let! treasuresResult = client.PostDig dig
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
            |> Seq.map (
                fun treasure -> async {
                    let! result = client.PostCash treasure
                    return match result with 
                            | Ok coins -> 
                                diggingDepthOptimizer.Post (TreasureReport { Depth = msg.Depth; Coins = coins |> Seq.length })
                            | _ -> treasureResender.Post { Treasure = treasure; Retry = 0 }
                })
            |> Async.Parallel
            |> Async.Ignore
            |> Async.StartImmediate
            let digged = treasures.treasures |> Seq.length
            if digged < msg.Amount then
                inbox.Post (DiggerDigMessage (({ msg with Depth = msg.Depth + 1; Amount = msg.Amount - digged }))) 
    }

    let rec messageLoop (state: DiggerState): Async<unit> = async {
        let! newState = async {
            if state.License.IsSome && state.License.Value.DigAllowed > state.License.Value.DigUsed then
                let! msg = async {
                    let! priorityMessage = async {
                        if state.OptimalDepth.IsSome then 
                            let! firstPriority = inbox.TryScan((fun msg ->
                                match msg with
                                | DiggerDigMessage digMsg when digMsg.Depth = state.OptimalDepth.Value -> (Some (async { return msg }))
                                | _ -> None), 0)

                            return! async {
                                match firstPriority with
                                | Some msg -> return Some msg
                                | None -> return! inbox.TryScan((fun msg ->
                                    match msg with
                                    | DiggerDigMessage digMsg when digMsg.Depth <= state.OptimalDepth.Value -> (Some (async { return msg }))
                                    | _ -> None), 0)
                            }
                        else return None
                    }

                    return! async {
                        match priorityMessage with
                        | Some priorityMessage -> return priorityMessage
                        | _ -> return! inbox.Receive()
                    }
                }

                match msg with
                | DiggerDigMessage digMsg ->
                    if digMsg.Amount > 0 && digMsg.Depth <= 10 then
                        doDig state.License.Value digMsg |> Async.StartImmediate
                        return { state with License = Some { state.License.Value with DigUsed = state.License.Value.DigUsed + 1 } }
                    else
                        return state
                | DiggerOptimalDepthMessage optimalDepth ->
                    return { state with OptimalDepth = (Some optimalDepth) }
                | AddCoinsToBuyLicense (optimalLicenseCost, newCoins) ->
                    return { state with Coins = newCoins; OptimalLicenseCost = optimalLicenseCost }
            else
                let! (optimalLicenseCost, coins) = async {
                    if not(Seq.isEmpty state.Coins) then return state.OptimalLicenseCost, state.Coins
                    else 
                        let! result = inbox.TryScan((fun msg -> 
                            match msg with
                            | AddCoinsToBuyLicense (optimalLicenseCost, coins) -> Some (async { return optimalLicenseCost, coins })
                            | _ -> None), 0)
                        return 
                            match result with
                            | Some (optimalLicenseCost, newCoins) -> optimalLicenseCost, newCoins
                            | _ -> state.OptimalLicenseCost, Seq.empty
                }

                let coinsToBuyLicense = coins |> Seq.truncate optimalLicenseCost
                let coinsToBuyLicenseCount = coinsToBuyLicense |> Seq.length
                let! licenseUpdateResult = client.PostLicense coinsToBuyLicense
                let coinsLeft = coins |> Seq.skip coinsToBuyLicenseCount
                return match licenseUpdateResult with 
                       | Ok newLicense -> 
                            if coinsToBuyLicenseCount > 0 then
                                //Console.WriteLine("for coins: " + coinsToBuyLicenseCount.ToString() + " " + DateTime.Now.ToString() + "\n license is bought: " + newLicense.ToString())
                                diggingLicenseCostOptimizer.Post (LicenseIsBought(coinsToBuyLicenseCount, newLicense))
                            
                            if coinsLeft |> Seq.isEmpty then
                                diggingLicenseCostOptimizer.Post (GetCoins inbox)
                            { state with License = Some { Id = newLicense.id; DigAllowed = newLicense.digAllowed; DigUsed = 0 }
                                         OptimalLicenseCost = optimalLicenseCost; Coins = coinsLeft }
                       | Error _ -> { state with OptimalLicenseCost = optimalLicenseCost; Coins = coinsLeft }
        }

        return! messageLoop newState
    }

    diggingDepthOptimizer.Post (DiggingDepthOptimizerMessage.DiggerRegistration inbox)
    messageLoop { License = None; OptimalDepth = None; Coins = Seq.empty; OptimalLicenseCost = 1 }

let oneBlockArea = { posX = 0; posY = 0; sizeX = 1; sizeY = 1 }
let inline explore (client: Client) (diggerAgentsPool: unit -> MailboxProcessor<DiggerMessage>) (digAreaSize: AreaSize) (area: AreaDto) =
    let rec exploreArea (area: AreaDto) = async {
        let! exploreResult = client.PostExplore(area)
        match exploreResult with 
        | Ok _ -> return  exploreResult
        | Error _ -> return! exploreArea area
    }

    let rec exploreOneBlock (coordinates: Coordinates) = 
        exploreArea { oneBlockArea with posX = coordinates.PosX; posY = coordinates.PosY }

    let rec exploreAndDigAreaByBlocks (area: AreaDto) (amount: int) (currentCoordinates: Coordinates): Async<int> = async {
        let! result = exploreOneBlock currentCoordinates
        let left = 
            match result with
            | Ok exploreResult when exploreResult.amount > 0 -> 
                diggerAgentsPool().Post (DiggerMessage.DiggerDigMessage ({ PosX = currentCoordinates.PosX; PosY = currentCoordinates.PosY; Amount = exploreResult.amount; Depth = 1 }))
                amount - exploreResult.amount
            | _ -> amount
            
        if left = 0 then
            return amount
        else
            let maxPosX = area.posX + area.sizeX
            let newCoordinates = 
                if currentCoordinates.PosX = maxPosX then None
                else Some { currentCoordinates with PosX = currentCoordinates.PosX + 1 }

            match newCoordinates with
            | None -> return amount - left
            | Some newCoordinates -> 
                return 1 + (exploreAndDigAreaByBlocks area left newCoordinates |> Async.RunSynchronously)
    }

    let rec exploreAndDigArea (area: AreaDto): Async<int> = async {
        let! result = exploreArea area
        match result with
        | Ok exploreResult -> 
            if exploreResult.amount = 0 then return 0
            else return! exploreAndDigAreaByBlocks area exploreResult.amount { PosX = area.posX; PosY = area.posY }
        | Error _ -> return! exploreAndDigArea area
    }
    
    exploreAndDigArea area

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

let diggingLicensesCostOptimizer (client: Client) (maxExploreCost: int) (inbox: MailboxProcessor<DiggingLicenseCostOptimizerMessage>) =
    let rec getBalanceFromServer() = async {
        let! result = client.GetBalance()
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
                    digger.Post (AddCoinsToBuyLicense (licenseCost, (wallet |> Seq.truncate licenseCost)))
                    { state with ExploreCost = state.ExploreCost + 1; Wallet = wallet |> Seq.skip licenseCost }
                else
                    digger.Post (AddCoinsToBuyLicense (state.OptimalCost.Value, wallet))
                    { state with Wallet = Seq.empty }
        else return { state with Wallet = wallet }
    }    
    
    let inline processMessage state msg = async {
        match msg with
        | GetCoins digger -> return! sendCoins state digger
        | LicenseIsBought (licenseCost, license) -> 
            return 
                if state.OptimalCost.IsSome then state
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
        let! newState = processMessage state msg
        return! messageLoop newState
    }
         
    messageLoop { LicensesCost = Map.empty; ExploreCost = 1; OptimalCost = None; Wallet = Seq.empty }

let diggingDepthOptimizer (inbox: MailboxProcessor<DiggingDepthOptimizerMessage>) =
    let rec messageLoop (diggers: MailboxProcessor<DiggerMessage> seq) (treasuresCost: Map<int, int>) = async {
        if treasuresCost.Count = 10 then
            let optimalDepth = treasuresCost |> Seq.sortBy (fun kv -> kv.Key) 
                                             |> Seq.last
            for digger in diggers do
                digger.Post (DiggerMessage.DiggerOptimalDepthMessage optimalDepth.Value)
            return ()
        else
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

let inline exploreField (explorer: AreaDto -> Async<int>) (timeout: int) (startCoordinates: Coordinates) (endCoordinates: Coordinates) (stepX: int) (stepY: int) = async {
    let maxPosX = endCoordinates.PosX - stepX
    let maxPosY = endCoordinates.PosY - stepY
    let areas = seq {
        for x in startCoordinates.PosX .. stepX .. maxPosX do
            for y in startCoordinates.PosY .. stepY .. maxPosY do
                yield { posX = x; posY = y; sizeX = stepX; sizeY = stepY }
    }

    for area in areas do
        explorer area |> Async.Ignore |> Async.Start
        let timeout = timeout + int (Math.Pow(float(area.posX - startCoordinates.PosX), 2.0)) / 5000
        //Console.WriteLine("timeout: " + timeout.ToString())
        do! Async.Sleep(timeout)
    }

let inline game (client: Client) = async {
    let diggersCount = 10
    let treasureResenderAgent = MailboxProcessor.Start (treasureResender client)
    let diggingDepthOptimizerAgent = MailboxProcessor.Start (diggingDepthOptimizer)
    let diggingLicenseCostOptimizerAgent = MailboxProcessor.Start (diggingLicensesCostOptimizer client 50)
    let diggerAgentsPool = createAgentsPool (digger client treasureResenderAgent diggingDepthOptimizerAgent diggingLicenseCostOptimizerAgent) diggersCount
    Console.WriteLine("diggers: " + diggersCount.ToString())

    let digAreaSize = { SizeX = 5; SizeY = 1 }
    let explorer = explore client diggerAgentsPool digAreaSize
    seq {
        exploreField explorer 14 { PosX = 1501; PosY = 0 } { PosX = 3500; PosY = 3500 } 5 1
        exploreField explorer 5 { PosX = 0; PosY = 0 } { PosX = 1500; PosY = 3500 } 5 1
    } |> Async.Parallel |> Async.Ignore |> Async.RunSynchronously
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
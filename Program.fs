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

type DiggerMessage = DiggerDigMessage of DiggerDigMessage | DiggerOptimalDepthMessage of int | AddCoinsToBuyLicense of int seq
type DiggingDepthOptimizerMessage = TreasureReport of TreasureReportMessage | DiggerRegistration of MailboxProcessor<DiggerMessage>
type DiggingLicenseCostOptimizerMessage = GetCoins of MailboxProcessor<DiggerMessage> | LicenseIsBought of int * License 

type TreasureRetryMessage = {
    Treasure: string
    Retry: int
}

let digger (client: Client) 
        (treasureResender: MailboxProcessor<TreasureRetryMessage>) 
        (diggingDepthOptimizer: MailboxProcessor<DiggingDepthOptimizerMessage>) 
        (diggingLicenseCostOptimizer: MailboxProcessor<DiggingLicenseCostOptimizerMessage>) 
        (inbox: MailboxProcessor<DiggerMessage>) = 
    let inline doDig license msg = async {
        let dig = { LicenseID = license.Id.Value; PosX = msg.PosX; PosY = msg.PosY; Depth = msg.Depth }
        let! treasuresResult = client.PostDig dig
        match treasuresResult with 
        | Error ex -> 
            match ex with 
            | :? HttpRequestException as ex when ex.StatusCode.HasValue ->
                match ex.StatusCode.Value with 
                | HttpStatusCode.NotFound -> 
                    if msg.Depth < 10 then
                        inbox.Post (DiggerMessage.DiggerDigMessage ({ msg with Depth = msg.Depth + 1 }))
                | HttpStatusCode.UnprocessableEntity -> ()
                | HttpStatusCode.Forbidden -> Console.WriteLine("Forbidden for: " + license.ToString())
                | _ -> inbox.Post (DiggerDigMessage (msg)) // retry
            | _ -> inbox.Post (DiggerDigMessage (msg)) // retry
        | Ok treasures -> 
            treasures.Treasures 
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
            |> Async.RunSynchronously
            let digged = treasures.Treasures |> Seq.length
            if digged < msg.Amount then
                inbox.Post (DiggerDigMessage (({ msg with Depth = msg.Depth + 1; Amount = msg.Amount - digged }))) 
    }

    let rec messageLoop (license: License) (optimalDepth: int option) (coins: int seq): Async<unit> = async {
        let! (newLicense, optimalDepth, newCoins) = async {
            if license.Id.IsSome && license.DigAllowed > license.DigUsed then
                let! msg = async {
                    let! priorityMessage = async {
                        if optimalDepth.IsSome then 
                            let! firstPriority = inbox.TryScan((fun msg ->
                                match msg with
                                | DiggerDigMessage digMsg when digMsg.Depth = optimalDepth.Value -> (Some (async { return msg }))
                                | _ -> None), 0)

                            return! async {
                                match firstPriority with
                                | Some msg -> return Some msg
                                | None -> return! inbox.TryScan((fun msg ->
                                    match msg with
                                    | DiggerDigMessage digMsg when digMsg.Depth <= optimalDepth.Value -> (Some (async { return msg }))
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
                        doDig license digMsg |> Async.Start
                        return { license with DigUsed = license.DigUsed + 1 }, optimalDepth, coins
                    else
                        return license, optimalDepth, coins
                | DiggerOptimalDepthMessage optimalDepth ->
                    return license, (Some optimalDepth), coins
                | AddCoinsToBuyLicense newCoins ->
                    return license, optimalDepth, newCoins
            else
                diggingLicenseCostOptimizer.Post (GetCoins inbox)
                let! coinsToBuyLicense = async {
                    if not (Seq.isEmpty coins) then return coins
                    else 
                        let! coins = inbox.TryScan((fun msg -> 
                            match msg with
                            | AddCoinsToBuyLicense coins -> Some (async { return coins })
                            | _ -> None), 0)
                        return 
                            match coins with
                            | Some newCoins -> newCoins
                            | _ -> Seq.empty
                }

                let coinsCount = coinsToBuyLicense |> Seq.length
                let! licenseUpdateResult = client.PostLicense coinsToBuyLicense
                return match licenseUpdateResult with 
                       | Ok newLicense -> 
                            if coinsCount > 0 then
                                Console.WriteLine("for coins: " + coinsCount.ToString() + " " + DateTime.Now.ToString())
                                Console.WriteLine("license is bought: " + newLicense.ToString())
                                diggingLicenseCostOptimizer.Post (LicenseIsBought(coinsCount, newLicense))
                            newLicense, optimalDepth, Seq.empty
                       | Error _ -> license, optimalDepth, Seq.empty
        }

        return! messageLoop newLicense optimalDepth newCoins
    }

    diggingDepthOptimizer.Post (DiggingDepthOptimizerMessage.DiggerRegistration inbox)
    messageLoop { Id = None; DigAllowed = 0; DigUsed = 0 } None Seq.empty

let explore (client: Client) (diggerAgentsPool: unit -> MailboxProcessor<DiggerMessage>) (digAreaSize: AreaSize) (area: Area) =
    let rec exploreArea (area: Area) = async {
        let! exploreResult = client.PostExplore(area)
        match exploreResult with 
        | Ok _ -> return  exploreResult
        | Error _ -> return! exploreArea area
    }

    let rec exploreOneBlock (coordinates: Coordinates) = 
        exploreArea { oneBlockArea with PosX = coordinates.PosX; PosY = coordinates.PosY }

    let rec exploreAndDigAreaByBlocks (area: Area) (amount: int) (currentCoordinates: Coordinates): Async<int> = async {
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

type LicensesCostOptimizerState = {
    LicensesCost: Map<int, int>
    ExploreCost: int
    OptimalCost: int option
    Spend: int
    Wallet: int seq
    Balance: int
}

let diggingLicensesCostOptimizer (client: Client) (spendLimit: float) (maxExploreCost: int) (inbox: MailboxProcessor<DiggingLicenseCostOptimizerMessage>) =
    let rec getBalanceFromServer() = async {
        let! result = client.GetBalance()
        match result with
        | Ok balance -> return balance
        | Error _ -> return! getBalanceFromServer()
    }
    
    let getBalance state coinsNeeded = async {
        if state.Wallet |> Seq.length >= coinsNeeded then return { Wallet = state.Wallet; Balance = state.Balance }
        else return! getBalanceFromServer()
    }
    
    let rec sendCoins state (digger: MailboxProcessor<DiggerMessage>) = async {
        let coinsNeeded = if state.OptimalCost.IsSome then state.OptimalCost.Value else state.ExploreCost
        let! balance = getBalance state coinsNeeded
        let coinsCount = balance.Wallet |> Seq.length
        let newState = { state with Wallet = balance.Wallet; Balance = balance.Balance }
        if coinsCount >= coinsNeeded then
            return 
                if state.OptimalCost.IsNone then
                    digger.Post (AddCoinsToBuyLicense (balance.Wallet |> Seq.take coinsNeeded))
                    { newState with ExploreCost = state.ExploreCost + 1; Spend = state.Spend + state.ExploreCost; Wallet = balance.Wallet |> Seq.skip coinsNeeded }
                else if state.Spend < int32((float balance.Balance * spendLimit)) then
                    digger.Post (AddCoinsToBuyLicense (balance.Wallet |> Seq.take coinsNeeded))
                    { newState with Spend = state.Spend + state.OptimalCost.Value;  Wallet = balance.Wallet |> Seq.skip coinsNeeded }
                else newState
        else return newState
    }    
    
    let processMessage state msg = async {
        match msg with
        | GetCoins digger -> return! sendCoins state digger
        | LicenseIsBought (licenseCost, license) -> 
            return 
                if state.OptimalCost.IsSome then state
                else
                    let newState = if state.LicensesCost.ContainsKey licenseCost then state
                                    else { state with LicensesCost = (state.LicensesCost.Add (licenseCost, license.DigAllowed)) }
                    if newState.LicensesCost |> Map.count >= maxExploreCost then
                        let (optimalCost, _) = 
                            newState.LicensesCost 
                            |> Map.toSeq 
                            |> Seq.map (fun (cost, digAllowed) -> cost, (float cost / (float digAllowed - 3.0)))
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
         
    messageLoop { LicensesCost = Map.empty; ExploreCost = 1; OptimalCost = None; Spend = 0; Wallet = Seq.empty; Balance = 0 }

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

let inline exploreField (explorer: Area -> Async<int>) (timeout: int) (startCoordinates: Coordinates) (endCoordinates: Coordinates) (stepX: int) (stepY: int) = async {
    let maxPosX = endCoordinates.PosX - stepX
    let maxPosY = endCoordinates.PosY - stepY
    for x in startCoordinates.PosX .. stepX .. maxPosX do
        for y in startCoordinates.PosY .. stepY .. maxPosY do
            let area = { PosX = x; PosY = y; SizeX = stepX; SizeY = stepY }
            explorer area |> Async.Ignore |> Async.Start
            do! Async.Sleep(timeout)
    }

let inline game (client: Client) = async {
    let diggersCount = 10
    let treasureResenderAgent = MailboxProcessor.Start (treasureResender client)
    let diggingDepthOptimizerAgent = MailboxProcessor.Start (diggingDepthOptimizer)
    let diggingLicenseCostOptimizerAgent = MailboxProcessor.Start (diggingLicensesCostOptimizer client 0.7 50)
    let diggerAgentsPool = createAgentsPool (digger client treasureResenderAgent diggingDepthOptimizerAgent diggingLicenseCostOptimizerAgent) diggersCount
    Console.WriteLine("diggers: " + diggersCount.ToString())

    let digAreaSize = { SizeX = 5; SizeY = 1 }
    let explorer = explore client diggerAgentsPool digAreaSize
    seq {
        exploreField explorer 100 { PosX = 1501; PosY = 0 } { PosX = 3500; PosY = 3500 } 10 2
        exploreField explorer 10 { PosX = 0; PosY = 0 } { PosX = 1500; PosY = 3500 } 5 1
        //exploreField explorer 30 { PosX = 501; PosY = 0 } { PosX = 1000; PosY = 3500 } 5 1
        //exploreField explorer 30 { PosX = 1001; PosY = 0 } { PosX = 1500; PosY = 3500 } 5 1
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
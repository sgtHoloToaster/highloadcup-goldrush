// Learn more about F# at http://docs.microsoft.com/dotnet/fsharp

open System
open Client
open System.Net.Http
open System.Net
open Hopac

let systemStarted = Environment.TickCount

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
    | AddCoinsToBuyLicense of coins: int seq
    
type DigManCh = { reqCh: Ch<DiggerMessage>; replyCh: Ch<DiggerMessage> }
type DiggerCh = { reqCh: Ch<DiggerMessage>; prReqCh: Ch<DiggerMessage>; coinsCh: Ch<int seq>;  replyCh: Ch<DiggerMessage> }

[<Struct>]
type DiggingDepthOptimizerMessage = TreasureReport of report: TreasureReportMessage

[<Struct>]
type DiggingLicenseCostOptimizerMessage = GetCoins of DiggerCh

[<Struct>]
type TreasureRetryMessage = {
    Depth: int
    Treasure: string
    Retry: int
}

[<Struct>]
type TreasureSenderMessage = SendTreasureMessage of TreasureRetryMessage | SetTreasureReport of treasure: bool

[<Struct>]
type DiggerState = {
    License: License option
    OptimalDepth: int option
    Coins: int seq
}

type DepthOptimizerCh = { reqCh: Ch<DiggingDepthOptimizerMessage>; replyCh: Ch<DiggingDepthOptimizerMessage> }
type LicenseOptimizerCh = { reqCh: Ch<DiggingLicenseCostOptimizerMessage>; replyCh: Ch<DiggingLicenseCostOptimizerMessage> }
type TreasureSenderCh = { reqCh: Ch<TreasureSenderMessage>; }

let persistentClient = new HttpClient(Timeout=TimeSpan.FromSeconds(300.0))
let nonPersistentClient = new HttpClient(Timeout=TimeSpan.FromSeconds(30.0))
let absolutelyNonPersistentClient = new HttpClient(Timeout=TimeSpan.FromSeconds(0.15))
let postCash = Client.postCash persistentClient
let postDig = Client.postDig persistentClient
let postLicense = Client.postLicense persistentClient
let postExplore = Client.postExplore absolutelyNonPersistentClient
let getBalance() = Client.getBalance nonPersistentClient

let digger (digManCh: DigManCh) (treasureSender: TreasureSenderCh) (diggingLicenseCostOptimizer: LicenseOptimizerCh) = job {
    let c = { replyCh = Ch(); reqCh = Ch(); prReqCh = Ch(); coinsCh = Ch() }

    let inline doDig (license: License) msg = job {
        let dig = { licenseID = license.Id; posX = msg.PosX; posY = msg.PosY; depth = msg.Depth }
        let! treasuresResult = postDig dig
        match treasuresResult with 
        | Error ex -> 
            match ex with 
            | :? HttpRequestException as ex when ex.StatusCode.HasValue ->
                match ex.StatusCode.Value with 
                | HttpStatusCode.NotFound -> 
                    if msg.Depth < 10 then
                        do! Ch.send digManCh.reqCh (DiggerMessage.DiggerDigMessage ({ msg with Depth = msg.Depth + 1 }))
                | HttpStatusCode.UnprocessableEntity -> Console.WriteLine("unprocessable: " + msg.ToString())
                | HttpStatusCode.Forbidden -> Console.WriteLine("Forbidden for: " + license.ToString())
                | _ -> do! Ch.send digManCh.reqCh (DiggerDigMessage (msg)) // retry
            | _ -> do! Ch.send digManCh.reqCh (DiggerDigMessage (msg)) // retry
        | Ok treasures -> 
            for treasure in treasures.treasures do
                do! Ch.send treasureSender.reqCh (SendTreasureMessage { Treasure = treasure; Retry = 0; Depth = msg.Depth })
            let digged = treasures.treasures |> Seq.length
            if digged < msg.Amount then
                do! Ch.send digManCh.reqCh (DiggerDigMessage (({ msg with Depth = msg.Depth + 1; Amount = msg.Amount - digged }))) 
    }

    let inline receiveMessage() =
        Alt.choose([| Ch.take c.prReqCh; Ch.take c.reqCh |])

    let rec messageLoop (state: DiggerState) = job {
        let! newState = job {
            if state.License.IsSome && state.License.Value.DigAllowed > state.License.Value.DigUsed then
                let! msg = receiveMessage()
                match msg with
                | DiggerDigMessage digMsg ->
                    if digMsg.Amount > 0 && digMsg.Depth <= 10 then
                        doDig state.License.Value digMsg |> startIgnore
                        return { state with License = Some { state.License.Value with DigUsed = state.License.Value.DigUsed + 1 } }
                    else
                        return state
                | DiggerOptimalDepthMessage optimalDepth ->
                    return { state with OptimalDepth = (Some optimalDepth) }
                | AddCoinsToBuyLicense (newCoins) ->
                    return { state with Coins = state.Coins |> Seq.append newCoins }
            else
                let! (coins) = job {
                    if not(Seq.isEmpty state.Coins) then return state.Coins
                    else 
                        let! result = Alt.choose [
                            Ch.take c.coinsCh |> Alt.afterFun(fun coinsMsg -> (Some coinsMsg))
                            timeOutMillis 1 |> Alt.afterFun(fun _ -> None) ]
                        return 
                            match result with
                            | Some (newCoins) -> newCoins
                            | _ -> Seq.empty
                }

                let coinsToBuyLicense = coins |> Seq.truncate 1
                let coinsToBuyLicenseCount = coinsToBuyLicense |> Seq.length
                let! licenseUpdateResult = postLicense coinsToBuyLicense
                let coinsLeft = coins |> Seq.skip coinsToBuyLicenseCount
                return! job {
                    match licenseUpdateResult with 
                       | Ok newLicense -> 
                            //if state.ReportLicense && coinsToBuyLicenseCount > 0 then
                            //    do! Ch.send diggingLicenseCostOptimizer.reqCh (LicenseIsBought(coinsToBuyLicenseCount, newLicense))
                            
                            if coinsLeft |> Seq.length < 10 then
                                do! Ch.send diggingLicenseCostOptimizer.reqCh (GetCoins c)
                            return { state with License = Some { Id = newLicense.id; DigAllowed = newLicense.digAllowed; DigUsed = 0 }
                                                Coins = coinsLeft }
                       | Error _ -> return { state with Coins = coinsLeft }
                }
        }

        return! messageLoop newState
    }

    do! Job.foreverServer (messageLoop { License = None; OptimalDepth = None; Coins = Seq.empty })
    return c
}

let oneBlockArea = { posX = 0; posY = 0; sizeX = 1; sizeY = 1 }
let inline explore (diggersManager: DigManCh) (defaultErrorTimeout: int) (area: AreaDto) =
    let rec exploreArea (area: AreaDto) = job {
        let! exploreResult = postExplore(area)
        match exploreResult with 
        | Ok _ -> return  exploreResult
        | Error _ -> return! exploreArea area
    }

    let exploreOneBlock (coordinates: Coordinates) = 
        exploreArea { oneBlockArea with posX = coordinates.PosX; posY = coordinates.PosY }

    let rec exploreAndDigAreaByBlocks (area: AreaDto) (amount: int) (currentCoordinates: Coordinates) = job {
        let! result = exploreOneBlock currentCoordinates
        let! left = job {
            match result with
            | Ok exploreResult when exploreResult.amount > 0 -> 
                do! Ch.give diggersManager.reqCh (DiggerMessage.DiggerDigMessage ({ PosX = currentCoordinates.PosX; PosY = currentCoordinates.PosY; Amount = exploreResult.amount; Depth = 1 }))
                return amount - exploreResult.amount
            | _ -> return amount
        }

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
                return! job {
                    let! result = exploreAndDigAreaByBlocks area left newCoordinates // tail recursion?
                    return digged + result
                }
    }

    let rec exploreAndDigArea (errorTimeout: int) (area: AreaDto) = job {
        let! result = exploreArea area
        match result with
        | Ok exploreResult -> 
            if exploreResult.amount = 0 then return 0
            else return! exploreAndDigAreaByBlocks area exploreResult.amount { PosX = area.posX; PosY = area.posY }
        | Error _ -> 
            return! exploreAndDigArea (int(Math.Pow(float errorTimeout, 2.0))) area
    }
    
    exploreAndDigArea defaultErrorTimeout area

let treasureSender (c: TreasureSenderCh) (diggingDepthOptimizer: DepthOptimizerCh) = job {
    let inline sendTreasure treasureMsg reportTreasure = job {
        let! result = postCash treasureMsg.Treasure
        match result, treasureMsg.Retry with
        | Ok coins, _ -> 
            if reportTreasure then
                do! Ch.send diggingDepthOptimizer.reqCh (TreasureReport { Depth = treasureMsg.Depth; Coins = coins |> Seq.length })

            return reportTreasure
        | Error _, 2 -> return reportTreasure
        | _ -> 
            do! Ch.send c.reqCh (SendTreasureMessage { treasureMsg with Retry = treasureMsg.Retry + 1 })
            return reportTreasure
    }

    let rec messageLoop reportTreasure = job {
        let! msg = Ch.take c.reqCh
        let! reportTreasure = job {
            match msg with
            | SendTreasureMessage treasureMsg ->
                sendTreasure treasureMsg reportTreasure |> startIgnore
                return reportTreasure
            | SetTreasureReport value -> return value
        }

        return! messageLoop reportTreasure
    }
        
    do! Job.foreverServer (messageLoop true)
    return c
}

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
    LastBalanceCheck: int
}

let diggingLicensesCostOptimizer (maxExploreCost: int) = job {
    let c = { reqCh = Ch (); replyCh = Ch () }
    let rec getBalanceFromServer() = job {
        let! result = getBalance()
        match result with
        | Ok balance -> return balance
        | Error _ -> return! getBalanceFromServer()
    }
    
    let inline getWallet state = job {
        if not(state.Wallet |> Seq.isEmpty) || Environment.TickCount - state.LastBalanceCheck < 500 then return state, state.Wallet
        else return! job {
            let! balance = getBalanceFromServer()
            return { state with LastBalanceCheck = Environment.TickCount }, balance.wallet
        }
    }
    
    let inline sendCoins state (digger: DiggerCh) = job {
        let! state, wallet = getWallet state
        let coinsCount = wallet |> Seq.length
        if coinsCount > 0 then
            let minCoins = Math.Min(coinsCount, 250)
            let maxCoins = Math.Max(coinsCount / 2, minCoins)
            do! Ch.send digger.reqCh (AddCoinsToBuyLicense (wallet |> Seq.truncate maxCoins))
            return { state with Wallet = wallet |> Seq.skip maxCoins }
        else return state
    }    
    
    let inline processMessage state msg = job {
        match msg with
        | GetCoins digger -> return! sendCoins state digger
    }

    let rec messageLoop (state: LicensesCostOptimizerState) = job {
        let! msg = Ch.take c.reqCh
        //Console.WriteLine("license: " + DateTime.Now.ToString() + " msg: " + msg.ToString())
        let! newState = processMessage state msg
        return! messageLoop newState
    }
    
    do! Job.foreverServer (messageLoop { LicensesCost = Map.empty; ExploreCost = 1; OptimalCost = None; Wallet = Seq.empty; LastBalanceCheck = Environment.TickCount })
    return c
}

let depthCoefs = Map[1, 3.0; 2, 2.0; 3, 1.3; 4, 1.1; 5, 0.95; 6, 0.85; 7, 0.77; 8, 0.72; 9, 0.67; 10, 0.62]
let diggingDepthOptimizer (digManCh: DigManCh) (treasureSenderCh: TreasureSenderCh) = job {
    let c: DepthOptimizerCh = { reqCh = Ch (); replyCh = Ch ()}
    let rec messageLoop (treasuresCost: Map<int, int>) = job {
        let! msg = Ch.take c.reqCh
        let! newTreasuresCost = job {
            match msg with 
            | TreasureReport treasuresMsg ->
                let newTreasuresCost = 
                    if treasuresCost.ContainsKey treasuresMsg.Depth then treasuresCost
                    else treasuresCost.Add(treasuresMsg.Depth, treasuresMsg.Coins)
                if treasuresCost.Count = 10 then
                    let sorted = treasuresCost |> Seq.mapFold (fun state kv -> ((kv.Key, state + kv.Value), state + kv.Value)) 0
                                                |> (fun (agg, _) -> agg |> Seq.sortBy (fun (key, value) -> ((float value) / (float key)) * depthCoefs.[key]))
                    let (optimalDepth, _) = sorted |> Seq.last
                    do! Ch.send digManCh.reqCh (DiggerOptimalDepthMessage optimalDepth)

                    do! timeOutMillis 60000
                    do! Ch.send treasureSenderCh.reqCh (SetTreasureReport true)
                    return Map.empty
                else return newTreasuresCost
        }

        return! messageLoop newTreasuresCost
    }
        
    do! Job.foreverServer (messageLoop Map.empty)
    return c
}

[<Struct>]
type DiggerManagerState = { OptimalDepth: int option }
let diggersManager (c: DigManCh) (diggers: DiggerCh seq) = job {
    let diggerAgentsPool = infiniteEnumerator diggers
    let rec messageLoop state = job {
        let! msg = Ch.take c.reqCh
        let! newState = job {
            match msg with 
            | DiggerDigMessage digMsg -> 
                if state.OptimalDepth.IsSome && digMsg.Depth <= state.OptimalDepth.Value then
                    do! Alt.choosy [|for digger in diggers do Ch.give digger.prReqCh msg|]
                else do! Alt.choosy [|for digger in diggers do Ch.give digger.reqCh msg|]
                return state
            | AddCoinsToBuyLicense msg -> 
                do! Ch.send (diggerAgentsPool().coinsCh) msg
                return state
            | DiggerOptimalDepthMessage depth ->
                return { state with OptimalDepth = Some depth }
        }

        return! messageLoop newState
    }

    do! Job.foreverServer (messageLoop { OptimalDepth = None })
    return c
}

let inline exploreField (explorer: AreaDto -> Job<int>) (startCoordinates: Coordinates) (endCoordinates: Coordinates) (stepX: int) (stepY: int) = job {
    let maxPosX = endCoordinates.PosX - stepX
    let maxPosY = endCoordinates.PosY - stepY
    let areas = seq {
        for x in startCoordinates.PosX .. stepX .. maxPosX do
            for y in startCoordinates.PosY .. stepY .. maxPosY do
                yield { posX = x; posY = y; sizeX = Math.Min(stepX, 3500 - x); sizeY = stepY }
    }

    for area in areas do
        do! explorer area |> Job.Ignore
        //let! amount = explorer area
        //do! timeOutMillis (amount * int((Math.Pow(Math.Min(float(area.posX - startCoordinates.PosX), 1.0), 3.0) / 10000.0)))
        //let timeout = timeout + int (Math.Pow(float(area.posX - startCoordinates.PosX), 2.0)) / 3000
        //Console.WriteLine("timeout: " + timeout.ToString())
        //do! Async.Sleep(timeout)

}

let inline game() = job {
    let diggersCount = 10
    let digManCh: DigManCh = { reqCh = Ch(); replyCh = Ch() }
    let treasureSenderCh: TreasureSenderCh = { reqCh = Ch() }
    let! diggingDepthOptimizerAgent = diggingDepthOptimizer digManCh treasureSenderCh
    let! treasureResenderAgent = treasureSender treasureSenderCh diggingDepthOptimizerAgent
    let! diggingLicenseCostOptimizerAgent = diggingLicensesCostOptimizer 50
    let! diggers = [for i in 1 .. diggersCount do  digger digManCh treasureResenderAgent diggingLicenseCostOptimizerAgent] |> Job.conCollect
    //let diggerAgentsPool = infiniteEnumerator diggers
    let! diggersManager = diggersManager digManCh diggers

    let explorer = explore diggersManager 2

    let explorersCount = 14
    let maxX = 3500
    let step = maxX / explorersCount
    let tasks = [
        for i in 1 .. explorersCount do 
            exploreField explorer { PosX = (step * i) - step; PosY = 0 } { PosX = step * i; PosY = 3500 } 7 1
    ]

    do! tasks |> Job.conIgnore
    do! timeOutMillis(Int32.MaxValue)
}

[<EntryPoint>]
let main argv =
  game() |> start
  async {
    do! Async.SwitchToThreadPool()
    do! Async.Sleep Int32.MaxValue
  } |> Async.RunSynchronously
  0
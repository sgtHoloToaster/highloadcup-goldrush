﻿// Learn more about F# at http://docs.microsoft.com/dotnet/fsharp

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
    let doDig licenseId msg = async {
        Console.WriteLine("doDig called. license:" + licenseId.ToString() + "body: " + msg.ToString())
        let dig = { LicenseID = licenseId; PosX = msg.PosX; PosY = msg.PosY; Depth = msg.Depth }
        let! treasuresResult = client.PostDig dig
        match treasuresResult with 
        | Error ex -> 
            Console.WriteLine(ex); 
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

        Console.WriteLine("new license: " + newLicense.ToString())
        return! messageLoop newLicense
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
    //let diggerAgents = seq { 
    //    for _ in 1 .. 10 do
    //        MailboxProcessor.Start (digger client)
    //}

    let diggerAgent = MailboxProcessor.Start (digger client)
    //Console.WriteLine("diggers: " + (diggerAgents |> Seq.length).ToString())
    //let mutable diggerAgentsEnumerator = diggerAgents.GetEnumerator()
    for x in 0 .. 3500 do
        for y in 0 .. 3500 do
            let area = { oneBlockArea with PosX = x; PosY = y }
            //if not (diggerAgentsEnumerator.MoveNext()) then
            //    diggerAgentsEnumerator.Dispose()
            //    diggerAgentsEnumerator <- diggerAgents.GetEnumerator()
            //    diggerAgentsEnumerator.MoveNext() |> ignore

            //let diggerAgent = diggerAgentsEnumerator.Current
            do! explore client diggerAgent area
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
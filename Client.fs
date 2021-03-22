module Client

open System.Net.Http
open System.Text.Json
open System.Text
open System

type LicenseDto = {
    id: int
    digAllowed: int
} 

type AreaDto = {
    posX: int
    posY: int
    sizeX: int
    sizeY: int
}

type ExploreResult = {
    amount: int
}

type DigDto = {
    licenseID: int
    posX: int
    posY: int
    depth: int
}

type ExploreDto = {
    amount: int
}

type WalletDto = {
    wallet: int seq
}

type TreasureDto = {
    treasures: string seq
}

let inline deserializeResponseBody<'T> (response: HttpResponseMessage) =
    async {
        return! Utf8Json.JsonSerializer.DeserializeAsync<'T>(response.Content.ReadAsStream()) |> Async.AwaitTask
    }

let inline processResponse<'T> (response: HttpResponseMessage) =
    async {
        let! body = deserializeResponseBody<'T> response
        return Ok body
    }

type Client(baseUrl: string) =
    let persistentClient = new HttpClient(Timeout = TimeSpan.FromSeconds(300.0))
    let nonPersistentClient = new HttpClient(Timeout = TimeSpan.FromSeconds(30.0))
    let licensesUrl = baseUrl + "licenses"
    let digUrl = baseUrl + "dig"
    let cashUrl = baseUrl + "cash"
    let exploreUrl = baseUrl + "explore"
    let balanceUrl = baseUrl + "balance"
    member private this.Post<'T, 'T1> (client: HttpClient) (url: string) (body: 'T1) = 
        async {
            let bodyJson = JsonSerializer.Serialize(body)
            use content = new StringContent(bodyJson, Encoding.UTF8, "application/json")
            try
                let! response = client.PostAsync(url, content) |> Async.AwaitTask
                response.EnsureSuccessStatusCode() |> ignore
                return! processResponse<'T> response
            with
            | :? AggregateException as aggex -> 
                match aggex.InnerException with
                | :? HttpRequestException as ex -> return Error (ex :> Exception)
                | _ -> return Error aggex.InnerException
            | :? HttpRequestException as ex -> 
                return Error (ex :> Exception)
        }

    member private this.Get<'T> (client: HttpClient) (url: string) =
        async {
            try
                let! response = client.GetAsync(url) |> Async.AwaitTask
                return! processResponse<'T> response
            with
            | :? AggregateException as aggex -> 
                match aggex.InnerException with
                | :? HttpRequestException as ex -> 
                    return Error (ex :> Exception)
                | _ -> Console.WriteLine(aggex.InnerException); return Error aggex.InnerException
            | :? HttpRequestException as ex -> 
                return Error (ex :> Exception)
        }

    member this.PostLicense (coins: int seq) =  
        this.Post<LicenseDto, int seq> persistentClient licensesUrl coins

    member this.GetLicenses() =
        this.Get<LicenseDto seq> nonPersistentClient licensesUrl

    member this.PostDig (dig: DigDto) =
        async {
            let! response = this.Post<string seq, DigDto> nonPersistentClient digUrl dig
            return match response with 
                   | Ok treasures -> Ok { treasures = treasures }
                   | Error err -> Error err
        }

    member this.PostCash (treasure: string) =
        this.Post<int seq, string> persistentClient cashUrl treasure

    member this.PostExplore (area: AreaDto) = 
        async {
            return! this.Post<ExploreResult, AreaDto> nonPersistentClient exploreUrl area
        }

    member this.GetBalance() =
        this.Get<WalletDto> nonPersistentClient balanceUrl
module Client

open System.Net.Http
open System
open System.Net.Http.Json

[<Struct>]
type LicenseDto = {
    id: int
    digAllowed: int
} 

[<Struct>]
type AreaDto = {
    posX: int
    posY: int
    sizeX: int
    sizeY: int
}

[<Struct>]
type ExploreResult = {
    amount: int
}

[<Struct>]
type DigDto = {
    licenseID: int
    posX: int
    posY: int
    depth: int
}

[<Struct>]
type ExploreDto = {
    amount: int
}

[<Struct>]
type WalletDto = {
    wallet: int seq
}

[<Struct>]
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

let baseUrl = "http://" + Environment.GetEnvironmentVariable("ADDRESS") + ":8000/"
let licensesUrl = baseUrl + "licenses"
let digUrl = baseUrl + "dig"
let cashUrl = baseUrl + "cash"
let exploreUrl = baseUrl + "explore"
let balanceUrl = baseUrl + "balance"
let inline private post<'T, 'T1> (client: HttpClient) (url: string) (body: 'T1) = 
    async {
        try
            let! response = client.PostAsJsonAsync(url, body) |> Async.AwaitTask
            response.EnsureSuccessStatusCode() |> ignore
            return! processResponse<'T> response
        with
        | :? AggregateException as aggex -> 
            match aggex.InnerException with
            | :? HttpRequestException as ex -> return Error (ex :> Exception)
            | _ -> Console.WriteLine(url + " " + aggex.InnerException.Message); return Error aggex.InnerException
        | :? HttpRequestException as ex -> 
            return Error (ex :> Exception)
    }

let inline private get<'T> (client: HttpClient) (url: string) =
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

let inline postLicense client (coins: int seq) =  
    post<LicenseDto, int seq> client licensesUrl coins

let inline getLicenses client =
    get<LicenseDto seq> client licensesUrl

let inline postDig client (dig: DigDto) =
    async {
        let! response = post<string seq, DigDto> client digUrl dig
        return match response with 
                | Ok treasures -> Ok { treasures = treasures }
                | Error err -> Error err
    }

let inline postCash client (treasure: string) =
    post<int seq, string> client cashUrl treasure

let inline postExplore client (area: AreaDto) = 
    async {
        return! post<ExploreResult, AreaDto> client exploreUrl area
    }

let inline getBalance client =
    get<WalletDto> client balanceUrl
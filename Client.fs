module Client

open System.Net.Http
open System
open System.Net.Http.Json
open Hopac

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
    Utf8Json.JsonSerializer.DeserializeAsync<'T>(response.Content.ReadAsStream())

let inline processResponse<'T> (response: HttpResponseMessage) =
    deserializeResponseBody<'T> response

let baseUrl = "http://" + Environment.GetEnvironmentVariable("ADDRESS") + ":8000/"
let licensesUrl = baseUrl + "licenses"
let digUrl = baseUrl + "dig"
let cashUrl = baseUrl + "cash"
let exploreUrl = baseUrl + "explore"
let balanceUrl = baseUrl + "balance"
let inline private post<'T, 'T1> (client: HttpClient) (url: string) (body: 'T1) = job {
        try
            let! response = client.PostAsJsonAsync(url, body)
            response.EnsureSuccessStatusCode() |> ignore
            let! result = processResponse<'T> response
            return Ok result
        with
        | _ as ex -> 
            //Console.WriteLine(url + " " + ex.Message)
            return Error ex
    }

let inline private get<'T> (client: HttpClient) (url: string) = job {
        try
            let! response = client.GetAsync(url)
            let! result = processResponse<'T> response
            return Ok result
        with
        | _ as ex -> return Error ex
    }

let inline postLicense client (coins: int seq) =  
    post<LicenseDto, int seq> client licensesUrl coins

let inline getLicenses client =
    get<LicenseDto seq> client licensesUrl

let inline postDig client (dig: DigDto) = job {
        let! response = post<string seq, DigDto> client digUrl dig
        return match response with 
                | Ok treasures -> Ok { treasures = treasures }
                | Error err -> Error err
    }
    
let inline postCash client (treasure: string) =
    post<int seq, string> client cashUrl treasure

let inline postExplore client (area: AreaDto) = 
    post<ExploreResult, AreaDto> client exploreUrl area

let inline getBalance client =
    get<WalletDto> client balanceUrl
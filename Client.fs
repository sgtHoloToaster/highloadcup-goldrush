module Client

open System.Net.Http
open System.Text.Json
open System.Text
open Models
open System
open System.Text.Json.Serialization

type ExploreResult = {
    Area: Area
    Amount: int
}

let jsonSerializerOptions: JsonSerializerOptions = new JsonSerializerOptions( PropertyNameCaseInsensitive = true )
jsonSerializerOptions.Converters.Add(JsonFSharpConverter())

let inline deserializeResponseBody<'T> (response: HttpResponseMessage) =
    async {
        let! content = response.Content.ReadAsStringAsync() |> Async.AwaitTask
        return JsonSerializer.Deserialize<'T>(content, jsonSerializerOptions)
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
        this.Post<License, int seq> persistentClient licensesUrl coins

    member this.GetLicenses() =
        this.Get<License seq> nonPersistentClient licensesUrl

    member this.PostDig (dig: Dig) =
        async {
            let! response = this.Post<string seq, Dig> nonPersistentClient digUrl dig
            return match response with 
                   | Ok treasures -> Ok { Priority = 0; Treasures = treasures }
                   | Error err -> Error err
        }

    member this.PostCash (treasure: string) =
        this.Post<int seq, string> persistentClient cashUrl treasure

    member this.PostExplore (area: Area) = 
        async {
            let! exploreResult = this.Post<ExploreResult, Area> nonPersistentClient exploreUrl area
            return match exploreResult with
                   | Ok res -> Ok { Amount = res.Amount; Area = res.Area; Priority = 0 }
                   | Error err -> Error err
        }

    member this.GetBalance() =
        this.Get<Wallet> nonPersistentClient balanceUrl
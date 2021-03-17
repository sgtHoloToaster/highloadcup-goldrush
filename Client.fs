module Client

open System.Net.Http
open System.Text.Json
open System.Text
open Models
open System
open System.Net
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
        Console.WriteLine("response content: \n" + content);
        return JsonSerializer.Deserialize<'T>(content, jsonSerializerOptions)
    }

let inline processResponse<'T> (response: HttpResponseMessage) =
    async {
        let! body = deserializeResponseBody<'T> response
        return Ok body
    }

type Client(baseUrl) =
    let client = new HttpClient()
    member private this.Post<'T, 'T1> (url: string) (body: 'T1) = 
        async {
            Console.WriteLine("posting: " + url)
            let bodyJson = JsonSerializer.Serialize(body)
            use content = new StringContent(bodyJson, Encoding.UTF8, "application/json")
            try
                let! response = client.PostAsync(url, content) |> Async.AwaitTask
                response.EnsureSuccessStatusCode() |> ignore
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

    member private this.Get<'T> (url: string) =
        async {
            try
                Console.WriteLine("getting: " + url)
                let! response = client.GetAsync(url) |> Async.AwaitTask
                return! processResponse response
            with
            | :? AggregateException as aggex -> 
                match aggex.InnerException with
                | :? HttpRequestException as ex -> 
                    Console.WriteLine("error:\n" + ex.Message)
                    return Error (ex :> Exception)
                | _ -> Console.WriteLine(aggex.InnerException); return Error aggex.InnerException
            | :? HttpRequestException as ex -> 
                Console.WriteLine("error:\n" + ex.Message)
                return Error (ex :> Exception)
        }

    member this.PostLicense (coins: int seq) =  
        this.Post<License, int seq> (baseUrl + "licenses") coins

    member this.GetLicenses() =
        this.Get<License seq> (baseUrl + "licenses")

    member this.PostDig (dig: Dig) =
        async {
            let! response = this.Post<string seq, Dig> (baseUrl + "dig") dig
            return match response with 
                   | Ok treasures -> Ok { Priority = 0; Treasures = treasures }
                   | Error err -> Error err
        }

    member this.PostCash (treasure: string) =
        this.Post<int seq, string> (baseUrl + "cash") treasure

    member this.PostExplore (area: Area) = 
        async {
            let! exploreResult = this.Post<ExploreResult, Area> (baseUrl + "explore") area
            return match exploreResult with
                   | Ok res -> Ok { Amount = res.Amount; Area = res.Area; Priority = 0 }
                   | Error err -> Error err
        }
        
    member this.GetBalance() =
        this.Get<Wallet> (baseUrl + "balance")

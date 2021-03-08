module Client

open System.Net.Http
open System.Text.Json
open System.Text
open Models

type ExploreResult = {
    Area: Area
    Amount: int
}

let inline deserializeResponseBody<'T> (response: HttpResponseMessage) =
    async {
        let! content = response.Content.ReadAsStreamAsync() |> Async.AwaitTask
        return! JsonSerializer.DeserializeAsync<'T>(content).AsTask() |> Async.AwaitTask
    }

let inline processResponse<'T> (response: HttpResponseMessage) =
    async {
        match response.IsSuccessStatusCode with 
            | true -> 
                let! body = deserializeResponseBody<'T> response
                return Ok body
            | false -> 
                return Error response.StatusCode
    }

type Client() =
    let baseUrl = "here will be url"
    let client = new HttpClient()
    let mutable _license = { DigAllowed = 0; DigUsed = 0; Id = None }

    member private this.Post<'T, 'T1> (url: string) (body: 'T1) = 
        async {
            let bodyJson = JsonSerializer.Serialize(body)
            use content = new StringContent(bodyJson, Encoding.UTF8, "application/json")
            let! response = client.PostAsync(url, content) |> Async.AwaitTask
            return! processResponse<'T> response
        }

    member private this.Get<'T> (url: string) =
        async {
            let! response = client.GetAsync(url) |> Async.AwaitTask
            return! processResponse response
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
        this.Post<unit, string> (baseUrl + "cash") treasure

    member this.PostExplore (area: Area) = 
        async {
            let! exploreResult = this.Post<ExploreResult, Area> (baseUrl + "explore") area
            return match exploreResult with
                   | Ok res -> Ok { Amount = res.Amount; Area = res.Area; Priority = 0 }
                   | Error err -> Error err
        }
        
    member this.GetBalance() =
        this.Get<Wallet> (baseUrl + "balance")

    member this.UpdateLicense() =
        async {
            let! result = this.PostLicense Seq.empty<int>
            match result with
            | Ok license -> _license <- license
            | _ -> ()
        }

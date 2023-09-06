﻿namespace Interlude.Web.Server.API

open System.Collections.Generic
open NetCoreServer
open Percyqaz.Common
open Prelude
open Interlude.Web.Shared.API
open Interlude.Web.Shared.Requests
open Interlude.Web.Server.API

module API =

    type Handler = string * Map<string, string array> * Map<string, string> * HttpResponse -> Async<unit>
    let handlers = Dictionary<(HttpMethod * string), Handler>()

    let inline add_endpoint route handle =
        handlers.Add( route, handle )

    do
        add_endpoint Auth.Discord.ROUTE Auth.Discord.handle

        add_endpoint Charts.Identify.ROUTE Charts.Identify.handle
        add_endpoint Charts.Scores.Save.ROUTE Charts.Scores.Save.handle
        add_endpoint Charts.Scores.Leaderboard.ROUTE Charts.Scores.Leaderboard.handle

        add_endpoint Tables.Records.ROUTE Tables.Records.handle

        add_endpoint Friends.List.ROUTE Friends.List.handle
        add_endpoint Friends.Add.ROUTE Friends.Add.handle
        add_endpoint Friends.Remove.ROUTE Friends.Remove.handle

        add_endpoint Health.HealthCheck.ROUTE Health.HealthCheck.handle

    let handle_request(method: HttpMethod, route: string, body: string, query_params: Map<string, string array>, headers: Map<string, string>, response: HttpResponse) =
        async {
            if handlers.ContainsKey((method, route)) then
                try
                    let handler = handlers.[(method, route)]
                    do! handler(body, query_params, headers, response)
                with
                | :? NotAuthorizedException -> response.MakeErrorResponse(401, "Needs authorization token") |> ignore
                | :? AuthorizeFailedException -> response.MakeErrorResponse(403, "Bad authorization token") |> ignore
                | err -> 
                    Logging.Error(sprintf "Error in %O %s: %O" method route err)
                    response.MakeErrorResponse(500, "Internal error") |> ignore
            else response.MakeErrorResponse(404, "Not found") |> ignore
        }
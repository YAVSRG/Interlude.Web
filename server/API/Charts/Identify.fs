﻿namespace Interlude.Web.Server.API.Charts

open NetCoreServer
open Interlude.Web.Shared.Requests
open Interlude.Web.Server.API
open Interlude.Web.Server.Domain

module Identify =

    let handle (body: string, query_params: Map<string, string array>, headers: Map<string, string>, response: HttpResponse) = 
        async {
            if not (query_params.ContainsKey "id") then
                response.MakeErrorResponse(400, "'id' is required") |> ignore
            else

            let hash = query_params.["id"].[0].ToUpper()
            match Charts.by_hash hash with
            | Some (chart, song) ->
                response.ReplyJson({ Found = true; Song = song; Chart = chart; Mirrors = chart.Sources |> Charts.mirrors |> List.ofSeq } : Charts.Identify.Response)
            | None ->
                response.ReplyJson({| Found = false |})
        }
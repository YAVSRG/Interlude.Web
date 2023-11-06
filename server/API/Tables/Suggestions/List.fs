﻿namespace Interlude.Web.Server.API.Tables.Suggestions

open NetCoreServer
open Interlude.Web.Shared.Requests
open Interlude.Web.Server.API
open Interlude.Web.Server.Domain

module List =

    let handle
        (
            body: string,
            query_params: Map<string, string array>,
            headers: Map<string, string>,
            response: HttpResponse
        ) =
        async {
            require_query_parameter query_params "table"

            let table = query_params.["table"].[0]

            match Backbeat.tables.TryFind table with
            | None -> raise NotFoundException
            | _ ->

            let suggestions = TableSuggestion.list table

            response.ReplyJson(
                {
                    Suggestions =
                        suggestions
                        |> Array.map (fun (id, x) ->
                            {
                                Id = id
                                ChartId = x.ChartId
                                OsuBeatmapId = x.OsuBeatmapId
                                EtternaPackId = x.EtternaPackId
                                Artist = x.Artist
                                Title = x.Title
                                Difficulty = x.Difficulty
                                SuggestedLevels = x.SuggestedLevels
                            }
                        )
                }
                : Tables.Suggestions.List.Response
            )
        }

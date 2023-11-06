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
            //let _, _ = authorize headers

            let table = query_params.["table"].[0]

            match Backbeat.tables.TryFind table with
            | None -> raise NotFoundException
            | _ ->

            let suggestions = TableSuggestion.list table

            let user_ids =
                suggestions
                |> Seq.map (fun (id, x) -> x.SuggestedLevels.Keys)
                |> Seq.concat
                |> Seq.distinct
                |> Array.ofSeq

            let user_map = Array.zip user_ids (User.by_ids user_ids) |> Map.ofSeq

            let map_suggested_by (user_suggestions: Map<int64, int>) =
                user_suggestions
                |> Map.toSeq
                |> Seq.groupBy snd
                |> Seq.map (fun (level, users) ->
                    level,
                    users
                    |> Seq.map fst
                    |> Seq.map (fun id ->
                        match user_map.TryFind(id) with
                        | Some(Some user) -> user.Username
                        | _ -> "???"
                    )
                    |> Array.ofSeq
                )
                |> Map.ofSeq

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
                                LevelsSuggestedBy = map_suggested_by x.SuggestedLevels
                            }
                        )
                }
                : Tables.Suggestions.List.Response
            )
        }

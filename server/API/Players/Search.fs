﻿namespace Interlude.Web.Server.API.Players

open NetCoreServer
open Interlude.Web.Shared.Requests
open Interlude.Web.Server.API
open Interlude.Web.Server.Domain

module Search =

    let handle
        (
            body: string,
            query_params: Map<string, string array>,
            headers: Map<string, string>,
            response: HttpResponse
        ) =
        async {
            require_query_parameter query_params "query"
            let _, _ = authorize headers

            let matches = User.search_by_username query_params.["query"].[0]
            let users = User.by_ids (matches |> Array.map fst)

            response.ReplyJson(
                {
                    Matches =
                        users
                        |> Array.choose (
                            Option.map (fun x ->
                                {
                                    Username = x.Username
                                    Color = x.Color |> Option.defaultValue Badge.DEFAULT_COLOR
                                }
                            )
                        )
                }
                : Players.Search.Response
            )
        }

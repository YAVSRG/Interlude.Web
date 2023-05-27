﻿namespace Interlude.Web.Server.Online

open System
open System.Collections.Generic
open Percyqaz.Common
open Interlude.Web.Shared
open Interlude.Web.Server.Domain

[<RequireQualifiedAccess>]
type UserState =
    | Nothing
    | Handshake
    | LoggedIn of username: string

module UserState = 

    [<RequireQualifiedAccess>]
    type Action =
        | Connect of Guid
        | Disconnect of Guid
        | Handshake of Guid
        | Login of Guid * string
        | Logout of Guid

    let private user_states = Dictionary<Guid, UserState>()
    let private usernames = Dictionary<string, Guid>()

    let private state_change = 
        { new Async.Service<Action, unit>()
            with override this.Handle(req) = async {
                    match req with

                    | Action.Connect id ->
                        user_states.Add(id, UserState.Nothing)

                    | Action.Disconnect id ->
                        if user_states.ContainsKey id then
                            match user_states.[id] with
                            | UserState.LoggedIn username ->
                                usernames.Remove username |> ignore
                                Logging.Info(sprintf "[<- %s" username)
                            | _ -> ()
                            user_states.Remove id |> ignore

                    | Action.Handshake id ->
                        match user_states.[id] with
                        | UserState.Nothing -> 
                            user_states.[id] <- UserState.Handshake
                            Server.send(id, Downstream.HANDSHAKE_SUCCESS)
                        | _ -> Server.kick(id, "Handshake sent twice")

                    | Action.Login (id, token) ->
                        match user_states.[id] with
                        | UserState.Handshake ->
                            Users.login (token,
                                function
                                | Ok username ->
                                    usernames.Add(username, id)
                                    user_states.[id] <- UserState.LoggedIn username
                                    Server.send(id, Downstream.LOGIN_SUCCESS username)
                                    Logging.Info(sprintf "[-> %s" username)
                                | Error reason ->
                                    Logging.Info(sprintf "%O failed to authenticate: %s" id reason)
                                    Server.send(id, Downstream.LOGIN_FAILED reason)
                            ) |> Async.RunSynchronously
                        | UserState.Nothing -> Server.kick(id, "Login sent before handshake")
                        | _ -> Server.kick(id, "Login sent twice")

                    | Action.Logout id ->
                        match user_states.[id] with
                        | UserState.LoggedIn username ->
                            usernames.Remove username |> ignore
                            user_states.[id] <- UserState.Handshake
                            Logging.Info(sprintf "[<- %s" username)
                        | _ -> Server.kick(id, "Not logged in")
            }
        }

    let connect(id) =
        state_change.Request(Action.Connect (id), ignore)

    let disconnect(id) =
        state_change.Request(Action.Disconnect (id), ignore)

    let handshake(id) =
        state_change.Request(Action.Handshake (id), ignore)

    let login(id, username) =
        state_change.Request(Action.Login (id, username), ignore)

    let logout(id) =
        state_change.Request(Action.Logout (id), ignore)

    let private username_lookup =
        { new Async.Service<Guid, string option>()
            with override this.Handle(req) = async {
                    let ok, state = user_states.TryGetValue req
                    if ok then
                        match state with
                        | UserState.LoggedIn username -> return Some username
                        | _ -> return None
                    else return None
            }
        }

    let find_username(id: Guid) = username_lookup.RequestAsync id

    let private session_lookup =
        { new Async.Service<string, Guid option>()
            with override this.Handle(req) = async {
                    let ok, id = usernames.TryGetValue req
                    if ok then return Some id
                    else return None
            }
        }

    let find_session(username: string) = session_lookup.RequestAsync username
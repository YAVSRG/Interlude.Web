﻿namespace Interlude.Web.Server

open System
open System.Collections.Generic
open System.Linq
open System.IO
open Percyqaz.Common
open Interlude.Web.Shared

type LobbyId = Guid
type PlayerId = Guid

exception MaliciousError of PlayerId * string
exception UserError of PlayerId * string
module [<AutoOpen>] Errors =
    let malice id error_message = raise (MaliciousError (id, error_message))
    let user_error id error_message = raise (UserError (id, error_message))

type Player =
    {
        Username: string
        mutable Status: LobbyPlayerStatus
        mutable CurrentPlayBuffer: MemoryStream
        mutable PlayPacketsReceived: int
        mutable PlayComplete: bool
    }
    static member Create name =
        { 
            Username = name
            Status = LobbyPlayerStatus.NotReady
            CurrentPlayBuffer = new MemoryStream([||], false)
            PlayPacketsReceived = 0
            PlayComplete = false
        }
    member this.StartPlay() =
        this.Status <- LobbyPlayerStatus.Playing
        this.PlayComplete <- false
        this.CurrentPlayBuffer <- new MemoryStream()
        this.PlayPacketsReceived <- 0
    member this.FinishPlay() =
        this.PlayComplete <- true
    member this.ReceivePlayPacket(id: PlayerId, data: byte array) =
        if this.Status <> LobbyPlayerStatus.Playing || this.PlayComplete then
            malice id "Sent play packet while not playing"
        elif data.Length % 6 > 0 then
            // todo: check 4 bytes every 6 for a monotone increasing value? otherwise flag as garbage and kick player
            malice id "Sending garbage data"
        else
            this.CurrentPlayBuffer.Write(data, 0, data.Length)
            this.PlayPacketsReceived <- this.PlayPacketsReceived + 1
            if int this.CurrentPlayBuffer.Length / (this.PlayPacketsReceived * MULTIPLAYER_REPLAY_DELAY_SECONDS) > PLAY_PACKET_THRESHOLD_PER_SECOND then
                malice id "Too much data sent too often"
    member this.GetReplay() = this.CurrentPlayBuffer.ToArray()

type Lobby =
    {
        Owner: PlayerId
        mutable Settings: LobbySettings
        mutable Host: PlayerId
        mutable Chart: LobbyChart option
        mutable GameRunning: bool
        Players: Dictionary<PlayerId, Player>
    }
    static member Create (playerId, username, name) =
        {
            Owner = playerId
            Settings = { Name = name; AutomaticRoundCountdown = false; HostRotation = false }
            Host = playerId
            Chart = None
            GameRunning = false
            Players = 
                let d = Dictionary<PlayerId, Player>()
                d.Add(playerId, Player.Create username)
                d
        }

module Lobby = 

    type Action =
        | List of player: PlayerId
        | Create of player: PlayerId * name: string
        | Join of player: PlayerId * id: LobbyId
        | Leave of player: PlayerId
        | Invite of invitor: PlayerId * invitee: string
        | Chat of sender: PlayerId * msg: string
        | ReadyUp of player: PlayerId * isReady: bool
        | SelectChart of player: PlayerId * chart: LobbyChart
        | StartGame of player: PlayerId
        | BeginPlaying of player: PlayerId
        | FinishPlaying of player: PlayerId * abandoned: bool
        | BeginSpectating of player: PlayerId
        | PlayData of player: PlayerId * data: byte array
        | Settings of player: PlayerId * settings: LobbySettings
        | ChangeHost of player: PlayerId * newhost: string
        | MissingChart of player: PlayerId
        | GameplayTimeout of lobby: LobbyId

    let private lobbies = Dictionary<LobbyId, Lobby>()
    let private in_lobby = Dictionary<PlayerId, LobbyId>()

    let private get_player_lobby_id(player: PlayerId) : LobbyId option =
        if in_lobby.ContainsKey player then Some in_lobby.[player] else None

    let private valid_lobby_name (proposed: string) : bool =
        if (proposed.Length < 2 || proposed.Length > 30) then false else

        if proposed.Trim().Length <> proposed.Length then false else

        (Seq.forall (fun (c: char) -> Seq.contains c UserState.VALID_USERNAME_CHARACTERS) proposed)

    let private multicast(lobby: Lobby, packet: Downstream) =
        for p in lobby.Players.Keys do
            Server.send(p, packet)
    
    let private multicast_except(id: PlayerId, lobby: Lobby, packet: Downstream) =
        for p in lobby.Players.Keys do
            if p <> id then Server.send(p, packet)

    let private transfer_host(lobby: Lobby, new_host: PlayerId) =
        Server.send(lobby.Host, Downstream.YOU_ARE_HOST false)
        Server.send(new_host, Downstream.YOU_ARE_HOST true)
        multicast(lobby, Downstream.LOBBY_EVENT(LobbyEvent.Host, lobby.Players.[new_host].Username))
        lobby.Host <- new_host

    let private game_end(lobby: Lobby) =
        lobby.GameRunning <- false
        multicast(lobby, Downstream.GAME_END)
        Logging.Debug(sprintf "End of round in lobby %s" lobby.Settings.Name)

        if lobby.Players.Values.Any(fun p -> p.Status = LobbyPlayerStatus.Playing && not p.PlayComplete) then
            // Somebody was a straggler
            Logging.Debug(sprintf "%s round (%s) did not end cleanly" lobby.Settings.Name lobby.Chart.Value.Title)
            if lobby.Settings.HostRotation then
                Server.send(lobby.Host, Downstream.SYSTEM_MESSAGE "Round didn't end properly so you are still host")

        else if lobby.Settings.HostRotation && lobby.Players.Values.Any(fun p -> p.Status = LobbyPlayerStatus.Playing && p.PlayComplete) then
            // Somebody played the song successfully and nobody was a straggler, hence the round went ok, time for host rotate
            let user_ids = ResizeArray(lobby.Players.Keys)
            let host_index = (user_ids.IndexOf(lobby.Host) + 1) % user_ids.Count
            transfer_host(lobby, user_ids[host_index])

        for p in lobby.Players.Values do
            p.Status <- LobbyPlayerStatus.NotReady

    let private ensure_logged_in(player) : Async<string> = async {
            match! UserState.find_username player with
            | None -> return malice player "You are not logged in"
            | Some username -> return username
        }

    let private ensure_in_lobby(player) =
        match get_player_lobby_id player with
        | Some id -> id, lobbies.[id]
        | None -> user_error player "You are not in a lobby"

    let private ensure_not_in_lobby(player) =
        if (get_player_lobby_id player).IsSome then user_error player "You are already in a lobby"

    let private state_change = 
        { new Async.Service<Action, unit>()
            with override this.Handle req = async {
                    try
                        match req with

                        | List (player) ->
                            let! _ = ensure_logged_in player

                            let lobbies = 
                                seq {
                                    for lobby_id in lobbies.Keys do
                                        let lobby = lobbies.[lobby_id]
                                        yield { 
                                            Id = lobby_id
                                            Name = lobby.Settings.Name
                                            Players = lobby.Players.Count |> byte
                                            CurrentlyPlaying = match lobby.Chart with Some c -> Some (c.Artist + " - " + c.Title) | None -> None
                                        }
                                }
                                |> Array.ofSeq

                            Server.send(player, Downstream.LOBBY_LIST lobbies)

                        | Create (player, lobby_name) ->
                            let! username = ensure_logged_in player
                            ensure_not_in_lobby player


                            let lobby_name = if valid_lobby_name lobby_name then lobby_name else username + "'s lobby"
                            let lobby_id = Guid.NewGuid()

                            let lobby = Lobby.Create(player, username, lobby_name)

                            lobbies.Add(lobby_id, lobby)
                            in_lobby.Add(player, lobby_id)
                            Server.send(player, Downstream.YOU_JOINED_LOBBY [||])
                            Server.send(player, Downstream.YOU_ARE_HOST true)
                            Server.send(player, Downstream.LOBBY_SETTINGS lobby.Settings)
                            Logging.Info (sprintf "Opened lobby: %s (%O)" lobby.Settings.Name lobby_id)



                        | Join (player, lobby_id) ->
                            let! username = ensure_logged_in player
                            ensure_not_in_lobby player

                            if not (lobbies.ContainsKey lobby_id) then user_error player "Lobby no longer exists"


                            let lobby = lobbies.[lobby_id]
                            let player_list = lobby.Players.Values |> Seq.map (fun p -> p.Username) |> Array.ofSeq

                            multicast(lobby, Downstream.PLAYER_JOINED_LOBBY username)
                            multicast(lobby, Downstream.LOBBY_EVENT(LobbyEvent.Join, username))
                            
                            in_lobby.Add(player, lobby_id)
                            lobby.Players.Add(player, Player.Create username)

                            Server.send(player, Downstream.YOU_JOINED_LOBBY player_list)
                            Server.send(player, Downstream.LOBBY_SETTINGS lobby.Settings)
                            if lobby.Chart.IsSome then Server.send(player, Downstream.SELECT_CHART lobby.Chart.Value)
                            for p in lobby.Players.Values do
                                if p.Status <> LobbyPlayerStatus.NotReady then Server.send(player, Downstream.PLAYER_STATUS(p.Username, p.Status))
                            if lobby.GameRunning then Server.send(player, Downstream.GAME_START)



                        | Leave player ->
                            let! username = ensure_logged_in player
                            let lobby_id, lobby = ensure_in_lobby player

                        
                            lobby.Players.Remove player |> ignore
                            in_lobby.Remove player |> ignore

                            multicast(lobby, Downstream.PLAYER_LEFT_LOBBY username)
                            multicast(lobby, Downstream.LOBBY_EVENT(LobbyEvent.Leave, username))
                            Server.send(player, Downstream.YOU_LEFT_LOBBY)

                            if lobby.Players.Count = 0 then
                                lobbies.Remove lobby_id |> ignore
                                Logging.Info (sprintf "Closed lobby: %s (%O)" lobby.Settings.Name lobby_id)
                            else
                                if lobby.GameRunning && lobby.Players.Values.Any(fun p -> p.Status = LobbyPlayerStatus.Playing && not p.PlayComplete) |> not then game_end lobby
                                if lobby.Host = player then
                                    lobby.Host <- Seq.head lobby.Players.Keys
                                    Server.send(lobby.Host, Downstream.YOU_ARE_HOST true)
                                    multicast(lobby, Downstream.LOBBY_EVENT(LobbyEvent.Host, lobby.Players.[lobby.Host].Username))



                        | Invite (sender, recipient) ->
                            let! username = ensure_logged_in sender
                            let lobby_id, lobby = ensure_in_lobby sender

                            match! UserState.find_session recipient with
                            | None -> user_error sender "User not found"
                            | Some recipient_id ->

                            if in_lobby.ContainsKey recipient_id && in_lobby.[recipient_id] = lobby_id then
                                user_error sender "User is already in this lobby"


                            Server.send(recipient_id, Downstream.INVITED_TO_LOBBY (username, lobby_id))
                            multicast(lobby, Downstream.LOBBY_EVENT(LobbyEvent.Invite, recipient))
                            
                        | Chat (player, message) ->
                            let! username = ensure_logged_in player
                            let _, lobby = ensure_in_lobby player

                            multicast(lobby, Downstream.CHAT(username, message))



                        | ReadyUp (player, ready) ->
                            let! username = ensure_logged_in player
                            let _, lobby = ensure_in_lobby player

                            let old_status = lobby.Players.[player].Status
                            match old_status with
                            | LobbyPlayerStatus.Playing
                            | LobbyPlayerStatus.AbandonedPlay
                            | LobbyPlayerStatus.Spectating -> malice player "Ready status changed while playing/spectating"
                            | _ -> ()


                            let new_status = if ready then LobbyPlayerStatus.Ready else LobbyPlayerStatus.NotReady
                            if new_status <> old_status then

                                lobby.Players.[player].Status <- new_status
                            
                                multicast_except(player, lobby, Downstream.PLAYER_STATUS(username, new_status))
                                multicast(lobby, Downstream.LOBBY_EVENT((if ready then LobbyEvent.Ready else LobbyEvent.NotReady), username))



                        | SelectChart (player, chart) ->
                            let! _ = ensure_logged_in player
                            let _, lobby = ensure_in_lobby player

                            if lobby.Host <> player then user_error player "You are not host"
                            if lobby.GameRunning then user_error player "Game is currently running"


                            if lobby.Chart <> Some chart then

                                lobby.Chart <- Some chart
                                for p in lobby.Players.Values do
                                    p.Status <- LobbyPlayerStatus.NotReady

                                multicast(lobby, Downstream.SELECT_CHART chart)



                        | StartGame player ->
                            let! _ = ensure_logged_in player
                            let _, lobby = ensure_in_lobby player
                        
                            if lobby.Host <> player then user_error player "You are not host"
                            if lobby.Chart.IsNone then user_error player "No chart selected"
                            if lobby.GameRunning then user_error player "Game is currently running"


                            lobby.GameRunning <- true
                            multicast(lobby, Downstream.GAME_START)



                        | BeginPlaying player ->
                            let! username = ensure_logged_in player
                            let _, lobby = ensure_in_lobby player
                        
                            if not lobby.GameRunning then user_error player "Game is not running"


                            lobby.Players.[player].StartPlay()
                            multicast_except(player, lobby, Downstream.PLAYER_STATUS (username, lobby.Players.[player].Status))
                            // todo? if anyone has sent play data you are probably starting too late and should be kicked
                            // ^^ unsure if wise because someone can maliciously send play data instantly to try and get people kicked



                        | FinishPlaying (player, abandoned) ->
                            let! username = ensure_logged_in player
                            let lobby_id, lobby = ensure_in_lobby player

                            if not lobby.GameRunning then user_error player "Game is not running"
                            if lobby.Players.[player].PlayComplete then malice player "Play finish packet already sent"


                            let plr = lobby.Players.[player]
                            plr.FinishPlay()

                            if abandoned then
                                plr.Status <- LobbyPlayerStatus.AbandonedPlay
                                multicast_except(player, lobby, Downstream.PLAYER_STATUS (username, plr.Status))

                            if lobby.Players.Values.Any(fun p -> p.Status = LobbyPlayerStatus.Playing && not p.PlayComplete) |> not then
                                // you are last player in the lobby to finish
                                game_end lobby

                            elif not abandoned && lobby.Players.Values.Any(fun p -> p <> plr && p.Status = LobbyPlayerStatus.Playing && p.PlayComplete) |> not then
                                // you are first player in the lobby to finish
                                Logging.Debug(sprintf "First player to finish is %s, starting timeout" username)
                                async {
                                    do! Async.Sleep(1000 * MULTIPLAYER_REPLAY_DELAY_SECONDS)
                                    this.Request(GameplayTimeout lobby_id, ignore)
                                } |> Async.Start



                        | BeginSpectating player ->
                            let! username = ensure_logged_in player
                            let _, lobby = ensure_in_lobby player

                            if not lobby.GameRunning then user_error player "Game is not running"
                            match lobby.Players.[player].Status with
                            | LobbyPlayerStatus.Playing
                            | LobbyPlayerStatus.Spectating -> malice player "Spectated while already playing/spectating"
                            | _ -> ()


                            lobby.Players.[player].Status <- LobbyPlayerStatus.Spectating
                            multicast_except(player, lobby, Downstream.PLAYER_STATUS (username, LobbyPlayerStatus.Spectating))
                            for p in lobby.Players.Values do
                                if p.Status = LobbyPlayerStatus.Playing && p.PlayPacketsReceived > 0 then
                                    // todo: break play data that is too large into smaller packets, for when you start spectating late into a song
                                    Server.send(player, Downstream.PLAY_DATA(p.Username, p.GetReplay()))



                        | PlayData (player, data) ->
                            let! username = ensure_logged_in player
                            let _, lobby = ensure_in_lobby player
                            if not lobby.GameRunning then user_error player "Replay data sent but game is not running"


                            lobby.Players.[player].ReceivePlayPacket(player, data)
                            for p in lobby.Players.Keys do
                                if p <> player && (lobby.Players.[p].Status = LobbyPlayerStatus.Playing || lobby.Players.[p].Status = LobbyPlayerStatus.Spectating) then
                                    Server.send(p, Downstream.PLAY_DATA(username, data))



                        | Settings (player, settings) ->
                            let! _ = ensure_logged_in player
                            let _, lobby = ensure_in_lobby player
                        
                            if lobby.Host <> player then user_error player "You are not host"


                            lobby.Settings <- settings
                            multicast(lobby, Downstream.LOBBY_SETTINGS settings)


                    
                        | ChangeHost (player, newhost) ->
                            let! _ = ensure_logged_in player
                            let lobby_id, lobby = ensure_in_lobby player

                            if lobby.Host <> player then user_error player "You are not host"

                            match! UserState.find_session newhost with
                            | None -> user_error player "User is not in this lobby"
                            | Some newhost_id ->
                            if player = newhost_id then malice player "Hosting yourself"

                            if not (in_lobby.ContainsKey newhost_id) || in_lobby.[newhost_id] <> lobby_id then
                                user_error player "User is not in this lobby"

                        
                            transfer_host(lobby, newhost_id)



                        | MissingChart (player) ->
                            let! username = ensure_logged_in player
                            let _, lobby = ensure_in_lobby player

                            if lobby.Players.[player].Status <> LobbyPlayerStatus.NotReady then
                                malice player "Can't be missing the chart in this state"


                            lobby.Players.[player].Status <- LobbyPlayerStatus.MissingChart
                            multicast_except(player, lobby, Downstream.PLAYER_STATUS(username, LobbyPlayerStatus.MissingChart))



                        | GameplayTimeout lobby_id ->
                            if not (lobbies.ContainsKey lobby_id) then Logging.Debug("Lobby closed before gameplay timeout")
                            else
                        
                            Logging.Debug(sprintf "Timeout called on lobby with id %O" lobby_id)

                            // todo: fix bug where if you start a new round before this timeout, it will end instantly

                            if lobbies.[lobby_id].GameRunning then game_end lobbies.[lobby_id]



                    with
                    | MaliciousError (player, error) -> Server.kick(player, error)
                    | UserError (player, message) -> Server.send(player, Downstream.SYSTEM_MESSAGE message)
            }
        }

    type Action with
        member this.Do = state_change.Request (this, ignore)


    let ensure_player_leaves_lobby(player, callback) =
        if in_lobby.ContainsKey(player) then state_change.Request( Leave(player), callback)
        else callback()
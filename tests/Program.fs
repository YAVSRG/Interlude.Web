﻿open System
open Percyqaz.Common
open Interlude.Web.Shared

Logging.Info "Make sure you have the server running"

type Status =
    | Disconnected
    | Connected
    | LoggedIn
    | InLobby of int

type TestClient(i: int) =
    inherit Client(System.Net.IPAddress.Parse("127.0.0.1"), 32767)

    let mutable status = Disconnected

    member this.Status = status

    override this.OnConnected() = 
        status <- Connected
        Logging.Info(sprintf "Client %i connected" i)

    override this.OnDisconnected() = 
        Logging.Info(sprintf "Client %i disconnected" i)
        status <- Disconnected

    override this.OnPacketReceived(packet: Downstream) =
        match packet with
        | Downstream.HANDSHAKE_SUCCESS -> this.Send(Upstream.LOGIN (sprintf "Test user %i" i))
        | Downstream.LOGIN_SUCCESS username -> 
            Logging.Info(sprintf "%s logged in " username)
            status <- LoggedIn
            if i = 0 then
                this.Send(Upstream.CREATE_LOBBY "Test lobby")
        | Downstream.YOU_JOINED_LOBBY ps -> 
            if i = 0 then
                for j = 1 to 9 do
                    this.Send(Upstream.INVITE_TO_LOBBY (sprintf "Test user %i" j))
            status <- InLobby 1
        | Downstream.LOBBY_SETTINGS s ->
            Logging.Info(sprintf "%i now in lobby: %A" i s)
        | Downstream.INVITED_TO_LOBBY (inviter, id) ->
            Logging.Info(sprintf "Client %i accepts invite from '%s'" i inviter)
            this.Send(Upstream.JOIN_LOBBY id)
        | Downstream.PLAYER_JOINED_LOBBY user ->
            match status with 
            | InLobby n -> 
                status <- InLobby (n + 1)
                if i = 0 && n + 1 = 10 then this.Send(Upstream.LEAVE_LOBBY)
            | _ -> ()
        | Downstream.YOU_ARE_HOST ->
            if i <> 0 && i <> 9 then
                this.Send(Upstream.LEAVE_LOBBY)
        | Downstream.YOU_LEFT_LOBBY ->
            Logging.Info(sprintf "%i left lobby" i)
            status <- LoggedIn
        | Downstream.SYSTEM_MESSAGE s ->
            Logging.Info(sprintf "@~> %i: %s" i s)
        | _ -> ()

let clients = Array.init 10 TestClient

for c in clients do
    c.Connect()

Console.ReadLine() |> ignore
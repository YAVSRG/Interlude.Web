﻿namespace Interlude.Web.Shared

open System
open System.IO

[<AutoOpen>]
module Packets =

    let PROTOCOL_VERSION = 5uy

    let MULTIPLAYER_REPLAY_DELAY_SECONDS = 3
    let MULTIPLAYER_REPLAY_DELAY_MS = float32 MULTIPLAYER_REPLAY_DELAY_SECONDS * 1000.0f

    let PLAY_PACKET_THRESHOLD_PER_SECOND = 600
    
    type LobbyPlayerStatus =
        | NotReady = 0uy
        | Ready = 1uy
        | Playing = 2uy
        | AbandonedPlay = 3uy
        | Spectating = 4uy

    type LobbyChart =
        {
            Hash: string
            Artist: string
            Title: string
            Creator: string
            Rate: float32
        }
        member this.Write(bw: BinaryWriter) =
            bw.Write this.Hash
            bw.Write this.Artist
            bw.Write this.Title
            bw.Write this.Creator
            bw.Write this.Rate
        static member Read(br: BinaryReader) =
            {
                Hash = br.ReadString()
                Artist = br.ReadString()
                Title = br.ReadString()
                Creator = br.ReadString()
                Rate = br.ReadSingle()
            }

    type LobbySettings =
        {
            Name: string
        }

    type LobbyInfo =
        {
            Id: Guid
            Name: string
            Players: byte
            CurrentlyPlaying: string option
        }
        member this.Write(bw: BinaryWriter) =
            bw.Write (this.Id.ToByteArray())
            bw.Write this.Name
            bw.Write this.Players
            bw.Write (Option.defaultValue "" this.CurrentlyPlaying)
        static member Read(br: BinaryReader) =
            {
                Id = new Guid(br.ReadBytes 16)
                Name = br.ReadString()
                Players = br.ReadByte()
                CurrentlyPlaying = br.ReadString() |> function "" -> None | s -> Some s
            }

    type LobbyEvent =
        | Join = 0uy
        | Leave = 1uy
        | Host = 2uy
        | Ready = 3uy
        | NotReady = 4uy
        | Invite = 5uy
        | Generic = 6uy

    [<RequireQualifiedAccess>]
    type Upstream =
        | VERSION of byte
        | LOGIN of username: string
        | LOGOUT

        | GET_LOBBIES
        | JOIN_LOBBY of id: Guid
        | CREATE_LOBBY of name: string

        | INVITE_TO_LOBBY of username: string
        | LEAVE_LOBBY
        | CHAT of message: string
        | READY_STATUS of bool

        | BEGIN_PLAYING
        | PLAY_DATA of byte array
        | BEGIN_SPECTATING
        | FINISH_PLAYING of abandoned: bool

        | TRANSFER_HOST of username: string
        | SELECT_CHART of LobbyChart
        | LOBBY_SETTINGS of LobbySettings
        | START_GAME

        | KICK_PLAYER of username: string // nyi

        static member Read(kind: byte, data: byte array) : Upstream =
            use ms = new MemoryStream(data)
            use br = new BinaryReader(ms)
            let packet = 
                match kind with
                | 0x00uy -> VERSION (br.ReadByte())
                | 0x01uy -> LOGIN (br.ReadString())
                | 0x02uy -> LOGOUT

                | 0x10uy -> GET_LOBBIES
                | 0x11uy -> JOIN_LOBBY (new Guid(br.ReadBytes 16)) 
                | 0x12uy -> CREATE_LOBBY (br.ReadString())

                | 0x20uy -> INVITE_TO_LOBBY (br.ReadString())
                | 0x21uy -> LEAVE_LOBBY
                | 0x22uy -> CHAT (br.ReadString())
                | 0x23uy -> READY_STATUS (br.ReadBoolean())

                | 0x30uy -> BEGIN_PLAYING
                | 0x31uy -> BEGIN_SPECTATING
                | 0x32uy ->
                    let length = int (br.BaseStream.Length - br.BaseStream.Position)
                    if length > PLAY_PACKET_THRESHOLD_PER_SECOND * MULTIPLAYER_REPLAY_DELAY_SECONDS then
                        failwithf "Excessive replay data being sent to server"
                    PLAY_DATA (br.ReadBytes(length))
                | 0x33uy -> FINISH_PLAYING (br.ReadBoolean())

                | 0x40uy -> TRANSFER_HOST (br.ReadString())
                | 0x41uy -> SELECT_CHART (LobbyChart.Read br)
                | 0x42uy -> LOBBY_SETTINGS { Name = br.ReadString() }
                | 0x43uy -> START_GAME

                | 0x50uy -> KICK_PLAYER (br.ReadString())

                | _ -> failwithf "Unknown packet type: %i" kind
            if ms.Position <> ms.Length then failwithf "Expected end-of-packet but there are %i extra bytes" (ms.Length - ms.Position)
            packet

        member this.Write() : byte * byte array =
            use ms = new MemoryStream()
            use bw = new BinaryWriter(ms)
            let kind = 
                match this with
                | VERSION v -> bw.Write v; 0x00uy
                | LOGIN name -> bw.Write name; 0x01uy
                | LOGOUT -> 0x02uy
                
                | GET_LOBBIES -> 0x10uy
                | JOIN_LOBBY id -> bw.Write (id.ToByteArray()); 0x11uy
                | CREATE_LOBBY name -> bw.Write name; 0x12uy
                
                | INVITE_TO_LOBBY username -> bw.Write username; 0x20uy
                | LEAVE_LOBBY -> 0x21uy
                | CHAT msg -> bw.Write msg; 0x22uy
                | READY_STATUS ready -> bw.Write ready; 0x23uy

                | BEGIN_PLAYING -> 0x30uy
                | BEGIN_SPECTATING -> 0x31uy
                | PLAY_DATA data -> bw.Write data; 0x32uy
                | FINISH_PLAYING abandon -> bw.Write abandon; 0x33uy

                | TRANSFER_HOST username -> bw.Write username; 0x40uy
                | SELECT_CHART chart -> chart.Write bw; 0x41uy
                | LOBBY_SETTINGS settings -> bw.Write settings.Name; 0x42uy
                | START_GAME -> 0x43uy

                | KICK_PLAYER username -> bw.Write username; 0x50uy
            kind, ms.ToArray()
            
    [<RequireQualifiedAccess>]
    type Downstream =
        | DISCONNECT of reason: string
        | HANDSHAKE_SUCCESS
        | LOGIN_SUCCESS of username: string
        // todo: login failure with reason
        // todo: ping and idle timeout system after 2 mins

        | LOBBY_LIST of lobbies: LobbyInfo array
        | YOU_JOINED_LOBBY of players: string array // todo: send status along with username
        | INVITED_TO_LOBBY of by_who: string * id: Guid

        | YOU_LEFT_LOBBY
        | YOU_ARE_HOST of bool
        | PLAYER_JOINED_LOBBY of username: string
        | PLAYER_LEFT_LOBBY of username: string
        | SELECT_CHART of LobbyChart
        | LOBBY_SETTINGS of LobbySettings
        | LOBBY_EVENT of LobbyEvent * data: string
        | SYSTEM_MESSAGE of string
        | CHAT of sender: string * message: string
        | PLAYER_STATUS of username: string * status: LobbyPlayerStatus

        | GAME_START
        | PLAY_DATA of username: string * data: byte array
        | GAME_END

        static member Read(kind: byte, data: byte array) : Downstream =
            use ms = new MemoryStream(data)
            use br = new BinaryReader(ms)
            let packet = 
                match kind with
                | 0x00uy -> DISCONNECT (br.ReadString())
                | 0x01uy -> HANDSHAKE_SUCCESS
                | 0x02uy -> LOGIN_SUCCESS (br.ReadString())

                | 0x10uy -> LOBBY_LIST ( Array.init (br.ReadByte() |> int) (fun _ -> LobbyInfo.Read br) )
                | 0x11uy -> YOU_JOINED_LOBBY ( Array.init (br.ReadByte() |> int) (fun _ -> br.ReadString()) )
                | 0x12uy -> INVITED_TO_LOBBY (br.ReadString(), new Guid(br.ReadBytes 16))
                | 0x13uy -> SYSTEM_MESSAGE (br.ReadString())

                | 0x20uy -> YOU_LEFT_LOBBY
                | 0x21uy -> YOU_ARE_HOST (br.ReadBoolean())
                | 0x22uy -> PLAYER_JOINED_LOBBY (br.ReadString())
                | 0x23uy -> PLAYER_LEFT_LOBBY (br.ReadString())
                | 0x24uy -> SELECT_CHART (LobbyChart.Read br)
                | 0x25uy -> LOBBY_SETTINGS { Name = br.ReadString() }
                | 0x26uy -> LOBBY_EVENT (br.ReadByte() |> LanguagePrimitives.EnumOfValue, br.ReadString())
                | 0x27uy -> CHAT (br.ReadString(), br.ReadString())
                | 0x28uy -> PLAYER_STATUS (br.ReadString(), br.ReadByte() |> LanguagePrimitives.EnumOfValue)

                | 0x30uy -> GAME_START
                | 0x31uy -> PLAY_DATA (br.ReadString(), br.ReadBytes(int (br.BaseStream.Length - br.BaseStream.Position)))
                | 0x32uy -> GAME_END

                | _ -> failwithf "Unknown packet type: %i" kind
            if ms.Position <> ms.Length then failwithf "Expected end-of-packet but there are %i extra bytes" (ms.Length - ms.Position)
            packet

        member this.Write() : byte * byte array =
            use ms = new MemoryStream()
            use bw = new BinaryWriter(ms)
            let kind = 
                match this with
                | DISCONNECT reason -> bw.Write reason; 0x00uy
                | HANDSHAKE_SUCCESS -> 0x01uy
                | LOGIN_SUCCESS name -> bw.Write name; 0x02uy

                | LOBBY_LIST lobbies -> 
                    bw.Write (byte lobbies.Length)
                    for lobby in lobbies do lobby.Write bw
                    0x10uy
                | YOU_JOINED_LOBBY players -> 
                    bw.Write (byte players.Length)
                    for player in players do bw.Write player
                    0x11uy
                | INVITED_TO_LOBBY (by_who, id) -> bw.Write by_who; bw.Write (id.ToByteArray()); 0x12uy
                | SYSTEM_MESSAGE message -> bw.Write message; 0x13uy

                | YOU_LEFT_LOBBY -> 0x20uy
                | YOU_ARE_HOST you_are_host -> bw.Write you_are_host; 0x21uy
                | PLAYER_JOINED_LOBBY username -> bw.Write username; 0x22uy
                | PLAYER_LEFT_LOBBY username -> bw.Write username; 0x23uy
                | SELECT_CHART chart -> chart.Write bw; 0x24uy
                | LOBBY_SETTINGS settings -> bw.Write settings.Name; 0x25uy
                | LOBBY_EVENT (kind, data) -> bw.Write (byte kind); bw.Write data; 0x26uy
                | CHAT (sender, msg) -> bw.Write sender; bw.Write msg; 0x27uy
                | PLAYER_STATUS (username, status) -> bw.Write username; bw.Write (byte status); 0x28uy
                
                | GAME_START -> 0x30uy
                | PLAY_DATA (username, data) -> bw.Write username; bw.Write data; 0x31uy
                | GAME_END -> 0x32uy
            kind, ms.ToArray()
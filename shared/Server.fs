﻿namespace Interlude.Web.Shared

open System
open System.Net.Sockets
open NetCoreServer
open Percyqaz.Common

module Server =

    type Config =
        {
            Address: string
            Port: int
            Handle_Packet: Guid * Upstream -> unit
            Handle_Connect: Guid -> unit
            Handle_Disconnect: Guid -> unit
        }

    type private Session(server: TcpServer, config: Config) =
        inherit TcpSession(server)

        let buffer = ref Empty

        override this.OnConnected() =
            config.Handle_Connect this.Id
            Logging.Debug(sprintf "%O :: %O :: %O" DateTime.Now this.Id this.Socket.RemoteEndPoint)

        override this.OnDisconnected() =
            config.Handle_Disconnect this.Id
        
        override this.OnError(error: SocketError) =
            Logging.Error(sprintf "Socket error in session %O: %O" this.Id error)

        override this.OnReceived(data: byte array, offset, size) =
            try Buffer.handle(buffer, data, offset, size, Upstream.Read >> fun packet -> config.Handle_Packet(this.Id, packet))
            with err ->
                Logging.Error(sprintf "Internal error processing socket data: %O" err)
                this.ProtocolDisconnect "Internal error"

        member this.SendPacket(packet: Downstream) =
            let packet_with_header = Buffer.packet_bytes(packet.Write())
            this.Send packet_with_header

        member this.ProtocolDisconnect(reason: string) =
            this.SendPacket(Downstream.DISCONNECT reason) |> ignore
            this.Disconnect() |> ignore

    type private Listener(config: Config) =
        inherit TcpServer(
            config.Address,
            config.Port,
            OptionKeepAlive = true,
            OptionTcpKeepAliveTime = 120,
            OptionTcpKeepAliveRetryCount = 1)

        override this.CreateSession() = new Session(this, config)
        override this.OnError(error: SocketError) =
            Logging.Error(sprintf "Error in TCP server: %O" error)

    let mutable private server = Unchecked.defaultof<Listener>
    
    let init(config: Config) =
        server <- new Listener(config)

    let start() = 
        Logging.Info "Starting socket server..."
        if server.Start() then Logging.Info "Started socket server."

    let send(id: Guid, packet: Downstream) =
        let packet_with_header = Buffer.packet_bytes(packet.Write())
        let session = server.FindSession(id)
        if not (isNull session) then
            try session.Send packet_with_header |> ignore
            with :? ObjectDisposedException -> Logging.Debug("Socket was disposed before packet could be sent")

    let kick(id: Guid, reason: string) =
        Logging.Info (sprintf "Kicking session %O: %s" id reason)
        send(id, Downstream.DISCONNECT reason)
        let session = server.FindSession(id)
        if not (isNull session) then session.Disconnect() |> ignore

    let stop() = 
        Logging.Info "Stopping socket server..."
        if server.Stop() then Logging.Info "Stopped socket server."
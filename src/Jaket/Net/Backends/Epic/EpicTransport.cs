namespace Jaket.Net.Backends;

using Epic.OnlineServices;
using Epic.OnlineServices.P2P;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

/// <summary> Connection implementation over EOS P2P; identifies the peer by its product user id. </summary>
public class EpicConnection : INetConnection
{
    /// <summary> Socket all of the mod connections live on. </summary>
    public static readonly SocketId Socket = new() { SocketName = "jaket" };
    /// <summary> Channel game packets are sent on. </summary>
    public const byte DATA_CHANNEL = 0;
    /// <summary> Channel the connection bootstrap packet is sent on. </summary>
    public const byte HELLO_CHANNEL = 1;

    /// <summary> Reusable buffer for outcoming packets, as the SDK accepts managed memory only. </summary>
    private static byte[] buffer = new byte[EpicBackend.MAX_PACKET_SIZE];

    private readonly EpicBackend backend;
    /// <summary> Product user id of the peer. </summary>
    public readonly ProductUserId Peer;

    public EpicConnection(EpicBackend backend, ProductUserId peer)
    {
        this.backend = backend;
        Peer = peer;
    }

    public uint UserData { get; set; }

    public void Accept()
    {
        var acceptOpt = new AcceptConnectionOptions { LocalUserId = backend.LocalUser, RemoteUserId = Peer, SocketId = Socket };
        backend.Platform.GetP2PInterface().AcceptConnection(ref acceptOpt);
    }

    public void Close()
    {
        var closeOpt = new CloseConnectionOptions { LocalUserId = backend.LocalUser, RemoteUserId = Peer, SocketId = Socket };
        backend.Platform.GetP2PInterface().CloseConnection(ref closeOpt);
    }

    public void Flush() { } // EOS sends packets right away

    public void Send(Ptr data, int size) => Send(data, size, DATA_CHANNEL);

    public void Send(Ptr data, int size, byte channel)
    {
        if (size > EpicBackend.MAX_PACKET_SIZE)
        {
            Log.Error($"[BACK] Outcoming packet of {size} bytes exceeds the {EpicBackend.MAX_PACKET_SIZE} bytes limit and is dropped");
            return;
        }
        if (size > 0 && data != Ptr.Zero) Marshal.Copy(data, buffer, 0, size);

        var sendOpt = new SendPacketOptions
        {
            LocalUserId          = backend.LocalUser,
            RemoteUserId         = Peer,
            SocketId             = Socket,
            Channel              = channel,
            Data                 = new ArraySegment<byte>(buffer, 0, size),
            AllowDelayedDelivery = true,
            Reliability          = PacketReliability.ReliableOrdered
        };
        var res = backend.Platform.GetP2PInterface().SendPacket(ref sendOpt);
        if (res != Result.Success) Log.Warning($"[BACK] Couldn't send a packet to {Peer}: {res}");
    }
}

/// <summary> Base of both endpoint implementations over EOS P2P: pumps packets and connection notifications. </summary>
public abstract class EpicEndpoint
{
    protected readonly EpicBackend backend;
    protected readonly INetListener listener;

    /// <summary> Stable connection wrappers, as the project compares them by reference. </summary>
    protected Dictionary<ProductUserId, EpicConnection> cons = new();
    /// <summary> Notification handles to clean up on close. </summary>
    private ulong reqHandle, estHandle, clsHandle;

    /// <summary> Reusable pinned buffer for incoming packets. </summary>
    private byte[] recvBuffer = new byte[EpicBackend.MAX_PACKET_SIZE];
    private GCHandle recvPin;
    private Ptr recvPtr;

    public EpicEndpoint(EpicBackend backend, INetListener listener)
    {
        this.backend = backend;
        this.listener = listener;

        recvPin = GCHandle.Alloc(recvBuffer, GCHandleType.Pinned);
        recvPtr = recvPin.AddrOfPinnedObject();

        var p2p = backend.Platform.GetP2PInterface();

        var reqOpt = new AddNotifyPeerConnectionRequestOptions { LocalUserId = backend.LocalUser, SocketId = EpicConnection.Socket };
        reqHandle = p2p.AddNotifyPeerConnectionRequest(ref reqOpt, null, (ref OnIncomingConnectionRequestInfo info) => OnRequest(info.RemoteUserId));

        var estOpt = new AddNotifyPeerConnectionEstablishedOptions { LocalUserId = backend.LocalUser, SocketId = EpicConnection.Socket };
        estHandle = p2p.AddNotifyPeerConnectionEstablished(ref estOpt, null, (ref OnPeerConnectionEstablishedInfo info) =>
        {
            if (info.ConnectionType != ConnectionEstablishedType.Reconnection) OnEstablished(info.RemoteUserId);
        });

        var clsOpt = new AddNotifyPeerConnectionClosedOptions { LocalUserId = backend.LocalUser, SocketId = EpicConnection.Socket };
        clsHandle = p2p.AddNotifyPeerConnectionClosed(ref clsOpt, null, (ref OnRemoteConnectionClosedInfo info) => OnClosed(info.RemoteUserId));
    }

    /// <summary> Returns a stable wrapper of the connection to the given peer. </summary>
    protected EpicConnection Wrap(ProductUserId peer) => cons.TryGetValue(peer, out var w) ? w : cons[peer] = new(backend, peer);

    protected abstract void OnRequest(ProductUserId peer);
    protected abstract void OnEstablished(ProductUserId peer);
    protected abstract void OnClosed(ProductUserId peer);

    /// <summary> Pumps all of the received packets and dispatches game data to the listener. </summary>
    public void Receive()
    {
        var p2p = backend.Platform.GetP2PInterface();
        var sizeOpt = new GetNextReceivedPacketSizeOptions { LocalUserId = backend.LocalUser };

        while (p2p.GetNextReceivedPacketSize(ref sizeOpt, out _) == Result.Success)
        {
            var recvOpt = new ReceivePacketOptions { LocalUserId = backend.LocalUser, MaxDataSizeBytes = (uint)recvBuffer.Length };
            ProductUserId sender = null;
            SocketId socket = default;

            if (p2p.ReceivePacket(ref recvOpt, ref sender, ref socket, out var channel, new ArraySegment<byte>(recvBuffer), out var written) != Result.Success) break;

            // packets of the bootstrap channel only exist to trigger a connection request
            if (channel != EpicConnection.DATA_CHANNEL || sender == null) continue;

            listener.OnMessage(Wrap(sender), EpicBackend.Uid(sender), recvPtr, (int)written);
        }
    }

    public virtual void Close()
    {
        var p2p = backend.Platform.GetP2PInterface();
        p2p.RemoveNotifyPeerConnectionRequest(reqHandle);
        p2p.RemoveNotifyPeerConnectionEstablished(estHandle);
        p2p.RemoveNotifyPeerConnectionClosed(clsHandle);

        cons.Values.Each(c => c.Close());
        cons.Clear();

        if (recvPin.IsAllocated) recvPin.Free();
    }
}

/// <summary> Listen socket implementation over EOS P2P. </summary>
public class EpicServerEndpoint : EpicEndpoint, INetServer
{
    /// <summary> Peers that have been accepted and fully connected. </summary>
    private List<ProductUserId> connected = new();
    /// <summary> Connection requests waiting for the requester to appear in the lobby member list. </summary>
    private List<(ProductUserId peer, float deadline)> requests = new();

    public EpicServerEndpoint(EpicBackend backend, INetListener listener) : base(backend, listener) { }

    public IEnumerable<INetConnection> Connected
    {
        get
        {
            foreach (var peer in connected) yield return Wrap(peer);
        }
    }

    protected override void OnRequest(ProductUserId peer)
    {
        // membership info may lag behind the connection request, so requests wait for it for a few seconds
        if (!requests.Any(r => r.peer == peer)) requests.Add((peer, Time.unscaledTime + 8f));
    }

    protected override void OnEstablished(ProductUserId peer)
    {
        if (!connected.Contains(peer))
        {
            connected.Add(peer);
            listener.OnConnected(Wrap(peer), EpicBackend.Uid(peer));
        }
    }

    protected override void OnClosed(ProductUserId peer)
    {
        if (connected.Remove(peer)) listener.OnDisconnected(Wrap(peer), EpicBackend.Uid(peer));
        cons.Remove(peer);
    }

    public new void Receive()
    {
        // resolve pending connection requests
        for (int i = requests.Count - 1; i >= 0; i--)
        {
            var (peer, deadline) = requests[i];
            uint uid = EpicBackend.Uid(peer);

            if (LobbyController.Contains(uid))
            {
                requests.RemoveAt(i);
                listener.OnConnecting(Wrap(peer), uid, true);
            }
            else if (Time.unscaledTime > deadline)
            {
                requests.RemoveAt(i);
                Log.Debug("[SERVER] Connection is rejected: not in the lobby");
                Wrap(peer).Close();
            }
        }
        base.Receive();
    }

    void INetServer.Receive() => Receive();
}

/// <summary> Outcoming connection implementation over EOS P2P. </summary>
public class EpicClientEndpoint : EpicEndpoint, INetClient
{
    /// <summary> Product user id of the lobby owner. </summary>
    private readonly ProductUserId owner;

    public EpicClientEndpoint(EpicBackend backend, INetListener listener, ProductUserId owner) : base(backend, listener)
    {
        this.owner = owner;

        // sending any packet establishes the connection: the host receives a request and accepts it
        listener.OnConnecting(Connection as EpicConnection, 0u, true);
        (Connection as EpicConnection).Send(Ptr.Zero, 1, EpicConnection.HELLO_CHANNEL);
    }

    public INetConnection Connection => Wrap(owner);

    protected override void OnRequest(ProductUserId peer)
    {
        // the host may reopen the connection, e.g. after a brief network failure
        if (peer == owner) Wrap(peer).Accept();
    }

    protected override void OnEstablished(ProductUserId peer)
    {
        if (peer == owner) listener.OnConnected(Wrap(peer), 0u);
    }

    protected override void OnClosed(ProductUserId peer)
    {
        if (peer == owner) listener.OnDisconnected(Wrap(peer), 0u);
    }
}

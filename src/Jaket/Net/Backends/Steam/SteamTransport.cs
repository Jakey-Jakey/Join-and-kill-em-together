namespace Jaket.Net.Backends;

using Steamworks;
using Steamworks.Data;
using System.Collections.Generic;

/// <summary> Connection implementation over Steam relay sockets; wraps Steamworks.Data.Connection. </summary>
public class SteamConnection : INetConnection
{
    /// <summary> Raw steam connection behind this wrapper. </summary>
    private Connection raw;

    public SteamConnection(Connection raw) => this.raw = raw;

    public uint UserData { get; set; }

    public void Accept() => raw.Accept();
    public void Close() => raw.Close();
    public void Flush() => raw.Flush();
    public void Send(Ptr data, int size)
    {
        if (Version.DEBUG && size > 1170) Log.Error($"[BACK] Outcoming packet of {size} bytes exceeds the 1170 bytes limit");
        raw.SendMessage(data, size);
    }
}

/// <summary> Listen socket implementation over Steam relay sockets; wraps SocketManager. </summary>
public class SteamServerEndpoint : INetServer, ISocketManager
{
    /// <summary> Raw socket manager behind this wrapper. </summary>
    private SocketManager manager;
    /// <summary> Listener processing connection events. </summary>
    private INetListener listener;
    /// <summary> Stable connection wrappers, as the project compares them by reference. </summary>
    private Dictionary<uint, SteamConnection> cons = new();

    public SteamServerEndpoint(INetListener listener)
    {
        this.listener = listener;
        manager = SteamNetworkingSockets.CreateRelaySocket<SocketManager>(4242);
        manager.Interface = this;
    }

    /// <summary> Returns a stable wrapper of the given connection. </summary>
    private SteamConnection Wrap(Connection con) => cons.TryGetValue(con.Id, out var w) ? w : cons[con.Id] = new(con);

    public IEnumerable<INetConnection> Connected
    {
        get
        {
            foreach (var con in manager.Connected) yield return Wrap(con);
        }
    }

    public void Receive() => manager.Receive();
    public void Close() => manager.Close();

    #region manager

    public void OnConnecting(Connection con, ConnectionInfo info) => listener.OnConnecting(Wrap(con), info.Identity.SteamId.AccountId, info.Identity.IsSteamId);

    public void OnConnected(Connection con, ConnectionInfo info) => listener.OnConnected(Wrap(con), info.Identity.SteamId.AccountId);

    public void OnDisconnected(Connection con, ConnectionInfo info)
    {
        listener.OnDisconnected(Wrap(con), info.Identity.SteamId.AccountId);
        cons.Remove(con.Id);
    }

    public void OnMessage(Connection con, NetIdentity netId, Ptr data, int size, long msg, long time, int channel) => listener.OnMessage(Wrap(con), netId.SteamId.AccountId, data, size);

    #endregion
}

/// <summary> Outcoming connection implementation over Steam relay sockets; wraps ConnectionManager. </summary>
public class SteamClientEndpoint : INetClient, IConnectionManager
{
    /// <summary> Raw connection manager behind this wrapper. </summary>
    private ConnectionManager manager;
    /// <summary> Listener processing connection events. </summary>
    private INetListener listener;
    /// <summary> Stable wrapper of the connection to the owner. </summary>
    private SteamConnection con;

    public SteamClientEndpoint(INetListener listener, SteamId owner)
    {
        this.listener = listener;
        manager = SteamNetworkingSockets.ConnectRelay<ConnectionManager>(owner, 4242);
        manager.Interface = this;
        con = new(manager.Connection);
    }

    public INetConnection Connection => con;

    public void Receive() => manager.Receive();
    public void Close() => manager.Close();

    #region manager

    public void OnConnecting(ConnectionInfo info) => listener.OnConnecting(con, 0u, true);

    public void OnConnected(ConnectionInfo info) => listener.OnConnected(con, 0u);

    public void OnDisconnected(ConnectionInfo info) => listener.OnDisconnected(con, 0u);

    public void OnMessage(Ptr data, int size, long msg, long time, int channel) => listener.OnMessage(con, 0u, data, size);

    #endregion
}

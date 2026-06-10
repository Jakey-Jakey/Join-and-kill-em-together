namespace Jaket.Net.Backends;

using System.Collections.Generic;

/// <summary> Listen socket of any backend; mirrors the surface of SocketManager used by the mod. </summary>
public interface INetServer
{
    /// <summary> All of the established connections. </summary>
    IEnumerable<INetConnection> Connected { get; }

    /// <summary> Receives incoming data and dispatches it to the listener. </summary>
    void Receive();
    /// <summary> Closes the socket and all of its connections. </summary>
    void Close();
}

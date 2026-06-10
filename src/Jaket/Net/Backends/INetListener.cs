namespace Jaket.Net.Backends;

/// <summary> Receiver of connection events; mirrors the shapes of ISocketManager and IConnectionManager. </summary>
public interface INetListener
{
    /// <summary> Someone is requesting a connection; call con.Accept() or con.Close() to decide. </summary>
    void OnConnecting(INetConnection con, uint accId, bool authentic);
    /// <summary> The connection is fully established. </summary>
    void OnConnected(INetConnection con, uint accId);
    /// <summary> The connection is closed by either side. </summary>
    void OnDisconnected(INetConnection con, uint accId);
    /// <summary> A data packet has arrived; sender is zero on the client side. </summary>
    void OnMessage(INetConnection con, uint sender, Ptr data, int size);
}

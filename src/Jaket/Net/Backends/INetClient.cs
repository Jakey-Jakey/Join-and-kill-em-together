namespace Jaket.Net.Backends;

/// <summary> Outcoming connection of any backend; mirrors the surface of ConnectionManager used by the mod. </summary>
public interface INetClient
{
    /// <summary> Connection to the lobby owner. </summary>
    INetConnection Connection { get; }

    /// <summary> Receives incoming data and dispatches it to the listener. </summary>
    void Receive();
    /// <summary> Closes the connection. </summary>
    void Close();
}

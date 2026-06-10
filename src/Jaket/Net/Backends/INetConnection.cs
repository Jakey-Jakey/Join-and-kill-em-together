namespace Jaket.Net.Backends;

/// <summary> Network connection of any backend; mirrors the surface of Steamworks.Data.Connection used by the mod. </summary>
public interface INetConnection
{
    /// <summary> Account identifier of the member behind the connection. </summary>
    uint UserData { get; set; }

    /// <summary> Accepts an incoming connection request. </summary>
    void Accept();
    /// <summary> Closes the connection. </summary>
    void Close();
    /// <summary> Flushes any pending outcoming data. </summary>
    void Flush();
    /// <summary> Sends the given data to the connection. </summary>
    void Send(Ptr data, int size);
}

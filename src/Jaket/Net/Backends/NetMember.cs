namespace Jaket.Net.Backends;

/// <summary> Lobby member of any backend; mirrors the surface of Steamworks.Friend used by the mod. </summary>
public struct NetMember
{
    /// <summary> Account identifier of the member. </summary>
    public uint AccId;
    /// <summary> Raw backend specific identifier: Steam id or hash of a product user id. </summary>
    public ulong Raw;
    /// <summary> Display name of the member. </summary>
    public string Name;
    /// <summary> Whether the member is the local player. </summary>
    public bool IsMe;
}

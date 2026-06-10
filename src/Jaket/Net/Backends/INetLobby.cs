namespace Jaket.Net.Backends;

using System.Collections.Generic;

/// <summary> Matchmaking lobby of any backend; mirrors the surface of Steamworks.Data.Lobby used by the mod. </summary>
public interface INetLobby
{
    /// <summary> Code that other players can use to join the lobby. </summary>
    string Code { get; }
    /// <summary> Owner of the lobby. </summary>
    NetMember Owner { get; }
    /// <summary> All of the lobby members, including the local player. </summary>
    IEnumerable<NetMember> Members { get; }
    /// <summary> Current number of members. </summary>
    int MemberCount { get; }
    /// <summary> Maximum number of members. </summary>
    int MaxMembers { get; }

    /// <summary> Gets lobby data by the given key. </summary>
    string GetData(string key);
    /// <summary> Sets lobby data by the given key; works for the owner only. </summary>
    void SetData(string key, string value);
    /// <summary> Sets the lobby visibility: 0 is private, 1 is friends only, 2 is public. </summary>
    void SetVisibility(byte level);
    /// <summary> Leaves the lobby. </summary>
    void Leave();
}

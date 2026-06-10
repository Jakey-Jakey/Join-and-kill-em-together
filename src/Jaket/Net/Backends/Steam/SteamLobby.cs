namespace Jaket.Net.Backends;

using Steamworks.Data;
using System.Collections.Generic;

/// <summary> Lobby implementation over Steam matchmaking; wraps Steamworks.Data.Lobby. </summary>
public class SteamLobby : INetLobby
{
    /// <summary> Raw steam lobby behind this wrapper. </summary>
    public readonly Lobby Raw;

    public SteamLobby(Lobby raw) => Raw = raw;

    public string Code => Raw.Id.ToString();

    public NetMember Owner => SteamBackend.Wrap(Raw.Owner);

    public IEnumerable<NetMember> Members
    {
        get
        {
            foreach (var member in Raw.Members) yield return SteamBackend.Wrap(member);
        }
    }

    public int MemberCount => Raw.MemberCount;
    public int MaxMembers => Raw.MaxMembers;

    public string GetData(string key) => Raw.GetData(key);
    public void SetData(string key, string value) => Raw.SetData(key, value);

    public void SetVisibility(byte level)
    {
        switch (level)
        {
            case 0: Raw.SetPrivate();     break;
            case 1: Raw.SetFriendsOnly(); break;
            case 2: Raw.SetPublic();      break;
        }
    }

    public void Leave() => Raw.Leave();
}

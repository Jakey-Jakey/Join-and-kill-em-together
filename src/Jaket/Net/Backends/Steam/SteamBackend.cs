namespace Jaket.Net.Backends;

using Steamworks;
using Steamworks.Data;

/// <summary> Backend implementation over Steam matchmaking and relay sockets; wraps the Facepunch library. </summary>
public class SteamBackend : INetBackend
{
    public string Name => "steam";
    public bool Valid => SteamClient.IsValid;
    public uint SelfId => SteamClient.SteamId.AccountId;
    public string SelfName => SteamClient.Name;
    public bool HasOverlay => true;

    /// <summary> Wraps a steam friend into a backend-neutral member. </summary>
    public static NetMember Wrap(Friend f) => new() { AccId = f.Id.AccountId, Raw = f.Id.Value, Name = f.Name, IsMe = f.IsMe };

    public void Load()
    {
        SteamMatchmaking.OnLobbyDataChanged += lobby => Events.OnLobbyAction.Fire();

        SteamFriends.OnGameLobbyJoinRequested += (lobby, id) => Events.OnLobbyInvite.Fire(lobby.Id.ToString());

        SteamMatchmaking.OnLobbyMemberJoined += (lobby, member) => Events.OnMemberJoin.Fire(Wrap(member));
        SteamMatchmaking.OnLobbyMemberLeave += (lobby, member) => Events.OnMemberLeave.Fire(Wrap(member));
    }

    public void Tick() { } // the game runs steam callbacks itself

    public string NameOf(uint id) => new Friend(id | 76561197960265728u).Name;

    #region matchmaking

    public void CreateLobby(int maxMembers, Cons<INetLobby> done) => SteamMatchmaking.CreateLobbyAsync(maxMembers).ContinueWith(t =>
    {
        t.Result?.SetJoinable(true);
        t.Result?.SetPrivate();
        done(t.Result == null ? null : new SteamLobby(t.Result.Value));
    });

    public bool ValidCode(string code) => ulong.TryParse(code, out _);

    public void JoinLobby(string code, Cons<INetLobby> done)
    {
        if (!ulong.TryParse(code, out var id))
        {
            done(null);
            return;
        }
        Lobby lobby = new(id);
        lobby.Join().ContinueWith(t =>
        {
            if (t.Result == RoomEnter.Success) done(new SteamLobby(lobby));
            else
            {
                Log.Warning($"[LOBY] Couldn't join the lobby, the result is {t.Result}");
                done(null);
            }
        });
    }

    public void FetchLobbies(Cons<LobbyInfo[]> done) => SteamMatchmaking.LobbyList.WithKeyValue("client", "jaket").RequestAsync().ContinueWith(t =>
    {
        var lobbies = t.Result ?? [];
        var infos = new LobbyInfo[lobbies.Length];

        for (int i = 0; i < lobbies.Length; i++)
        {
            var l = lobbies[i];
            infos[i] = new LobbyInfo
            {
                Name        = l.GetData("name"),
                Level       = l.GetData("level"),
                MemberCount = l.MemberCount,
                MaxMembers  = l.MaxMembers,
                Join        = () => LobbyController.JoinLobby(l.Id.ToString())
            };
        }
        done(infos);
    });

    #endregion
    #region transport

    public INetServer OpenServer(INetListener listener) => new SteamServerEndpoint(listener);

    public INetClient Connect(INetListener listener) => new SteamClientEndpoint(listener, (LobbyController.Lobby as SteamLobby).Raw.Owner.Id);

    #endregion
    #region social

    public void OpenProfile(NetMember member) => SteamFriends.OpenUserOverlay(member.Raw, "steamid");

    public void OpenInviteOverlay()
    {
        if (LobbyController.Lobby is SteamLobby lobby) SteamFriends.OpenGameInviteOverlay(lobby.Raw.Id);
    }

    public void SetRichPresence(string key, string value) => SteamFriends.SetRichPresence(key, value);

    #endregion
}

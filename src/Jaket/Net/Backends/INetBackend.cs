namespace Jaket.Net.Backends;

/// <summary>
/// Multiplayer platform backend: identity, matchmaking and transport.
/// The mod ships two implementations, Steam (default) and Epic Online Services.
/// </summary>
public interface INetBackend
{
    /// <summary> Short name of the backend used in logs and lobby data. </summary>
    string Name { get; }
    /// <summary> Whether the backend is initialized and usable. </summary>
    bool Valid { get; }
    /// <summary> Account identifier of the local player; the entire mod keys entities, bans and packets on it. </summary>
    uint SelfId { get; }
    /// <summary> Display name of the local player. </summary>
    string SelfName { get; }
    /// <summary> Whether the backend has a social overlay: profiles and invitations. </summary>
    bool HasOverlay { get; }

    /// <summary> Initializes the backend and wires its callbacks to the project events. </summary>
    void Load();
    /// <summary> Pumps the backend callbacks; called every frame. </summary>
    void Tick();
    /// <summary> Returns the display name of the member with the given identifier. </summary>
    string NameOf(uint id);

    /// <summary> Creates a new lobby and reports it to the callback, null on failure. </summary>
    void CreateLobby(int maxMembers, Cons<INetLobby> done);
    /// <summary> Whether the given string can possibly be a lobby code. </summary>
    bool ValidCode(string code);
    /// <summary> Joins the lobby with the given code and reports it to the callback, null on failure. </summary>
    void JoinLobby(string code, Cons<INetLobby> done);
    /// <summary> Fetches the list of public lobbies. </summary>
    void FetchLobbies(Cons<LobbyInfo[]> done);

    /// <summary> Opens a listen socket for the members of the lobby. </summary>
    INetServer OpenServer(INetListener listener);
    /// <summary> Connects to the owner of the current lobby. </summary>
    INetClient Connect(INetListener listener);

    /// <summary> Opens the profile of the given member in the overlay, if there is one. </summary>
    void OpenProfile(NetMember member);
    /// <summary> Opens an invitation dialog in the overlay, if there is one. </summary>
    void OpenInviteOverlay();
    /// <summary> Updates the rich presence value with the given key, if supported. </summary>
    void SetRichPresence(string key, string value);
}

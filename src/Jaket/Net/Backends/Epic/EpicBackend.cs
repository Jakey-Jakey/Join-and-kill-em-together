namespace Jaket.Net.Backends;

/// <summary> Backend implementation over Epic Online Services; auth via device id, lobbies and P2P via the EOS SDK. </summary>
public class EpicBackend : INetBackend
{
    public string Name => "epic";
    public bool Valid => false; // to be implemented
    public uint SelfId => 0u;
    public string SelfName => "Player";
    public bool HasOverlay => false;

    public void Load() => Log.Error("[BACK] The EOS backend is not implemented yet");
    public void Tick() { }
    public string NameOf(uint id) => $"Player#{id % 10000:0000}";

    public void CreateLobby(int maxMembers, Cons<INetLobby> done) => done(null);
    public bool ValidCode(string code) => false;
    public void JoinLobby(string code, Cons<INetLobby> done) => done(null);
    public void FetchLobbies(Cons<LobbyInfo[]> done) => done([]);

    public INetServer OpenServer(INetListener listener) => null;
    public INetClient Connect(INetListener listener) => null;

    public void OpenProfile(NetMember member) { }
    public void OpenInviteOverlay() { }
    public void SetRichPresence(string key, string value) { }
}

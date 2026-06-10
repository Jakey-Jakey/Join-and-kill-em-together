namespace Jaket.Net.Backends;

using Epic.OnlineServices;
using Epic.OnlineServices.Connect;
using Epic.OnlineServices.Lobby;
using Epic.OnlineServices.Logging;
using Epic.OnlineServices.P2P;
using Epic.OnlineServices.Platform;
using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

using Jaket.Assets;

/// <summary>
/// Backend implementation over Epic Online Services: auth via the Connect interface and a device id,
/// so the players need neither Steam nor an Epic account. Lobbies live in a global jaket bucket and
/// use the EOS lobby id as the join code; transport is EOS P2P with reliable ordered packets.
/// </summary>
public class EpicBackend : INetBackend
{
    /// <summary> Identifier of the lobby bucket all jaket lobbies live in. </summary>
    public const string BUCKET_ID = "jaket:global";
    /// <summary> Maximum size of an EOS P2P packet; all of the mod packets fit in. </summary>
    public const int MAX_PACKET_SIZE = 1170;

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern Ptr LoadLibraryW(string path);

    /// <summary> EOS platform handle, null until initialized. </summary>
    public PlatformInterface Platform { get; private set; }
    /// <summary> Product user id of the local player, null until logged in. </summary>
    public ProductUserId LocalUser { get; private set; }
    /// <summary> Lobby that the player is currently in, mirrors LobbyController.Lobby. </summary>
    public EpicLobby Lobby;

    private uint selfId;
    private string selfName = "Player";

    public string Name => "epic";
    public bool Valid => LocalUser != null;
    public uint SelfId => selfId;
    public string SelfName => selfName;
    public bool HasOverlay => false;

    /// <summary> Account identifier of any member is a stable hash of its product user id. </summary>
    public static uint Uid(ProductUserId puid)
    {
        uint hash = 2166136261u;
        foreach (char c in puid.ToString())
        {
            hash ^= (byte)c;
            hash *= 16777619u;
        }
        return hash == 0u ? 1u : hash;
    }

    public void Load()
    {
        var cfg = NetBackends.Config;
        var productId    = cfg.Bind("EOS", "ProductId",    "", "Product ID from the Epic Developer Portal").Value;
        var sandboxId    = cfg.Bind("EOS", "SandboxId",    "", "Sandbox ID from the Epic Developer Portal").Value;
        var deploymentId = cfg.Bind("EOS", "DeploymentId", "", "Deployment ID from the Epic Developer Portal").Value;
        var clientId     = cfg.Bind("EOS", "ClientId",     "", "Client ID of a GameClient policy client").Value;
        var clientSecret = cfg.Bind("EOS", "ClientSecret", "", "Client secret of that client").Value;

        selfName = cfg.Bind("EOS", "PlayerName", Environment.UserName, "Display name visible to other players").Value;
        if (string.IsNullOrWhiteSpace(selfName) || selfName.Length > 24) selfName = "Player";

        if (productId == "")
        {
            Log.Error("[BACK] EOS credentials are not set; fill in BepInEx/config/Jaket.Backends.cfg and restart the game, see PORTING.md");
            return;
        }

        // the plugin folder is not on the dll search path, so the native library must be loaded explicitly before the first P/Invoke
        var native = Path.Combine(Path.GetDirectoryName(Plugin.Instance.Location), "EOSSDK-Win64-Shipping.dll");
        if (LoadLibraryW(native) == Ptr.Zero)
        {
            Log.Error($"[BACK] Couldn't load {native}, error {Marshal.GetLastWin32Error()}");
            return;
        }

        var initOpt = new InitializeOptions { ProductName = "Jaket", ProductVersion = Jaket.Version.CURRENT };
        var initRes = PlatformInterface.Initialize(ref initOpt);
        if (initRes != Result.Success && initRes != Result.AlreadyConfigured)
        {
            Log.Error($"[BACK] EOS initialization failed: {initRes}");
            return;
        }

        LoggingInterface.SetLogLevel(LogCategory.AllCategories, LogLevel.Warning);
        LoggingInterface.SetCallback((ref LogMessage msg) => Log.Warning($"[EOS/{msg.Category}] {msg.Message}"));

        var options = new Options
        {
            ProductId         = productId,
            SandboxId         = sandboxId,
            DeploymentId      = deploymentId,
            ClientCredentials = new ClientCredentials { ClientId = clientId, ClientSecret = clientSecret },
            Flags             = PlatformFlags.DisableOverlay | PlatformFlags.DisableSocialOverlay,
            CacheDirectory    = Path.Combine(Path.GetTempPath(), "jaket-eos")
        };
        Platform = PlatformInterface.Create(ref options);

        if (Platform == null)
        {
            Log.Error("[BACK] EOS platform creation failed, check the credentials");
            return;
        }

        var deviceOpt = new CreateDeviceIdOptions { DeviceModel = SystemInfo.deviceModel };
        Platform.GetConnectInterface().CreateDeviceId(ref deviceOpt, null, (ref CreateDeviceIdCallbackInfo info) =>
        {
            if (info.ResultCode != Result.Success && info.ResultCode != Result.DuplicateNotAllowed)
                Log.Error($"[BACK] Device id creation failed: {info.ResultCode}");
            else
                Login();
        });

        Application.quitting += () =>
        {
            Platform?.Release();
            Platform = null;
            PlatformInterface.Shutdown();
        };
    }

    /// <summary> Logs in via the Connect interface; creates a product user on the first launch. </summary>
    private void Login()
    {
        var loginOpt = new LoginOptions
        {
            Credentials   = new Credentials { Token = null, Type = ExternalCredentialType.DeviceidAccessToken },
            UserLoginInfo = new UserLoginInfo { DisplayName = selfName }
        };
        Platform.GetConnectInterface().Login(ref loginOpt, null, (ref LoginCallbackInfo info) =>
        {
            if (info.ResultCode == Result.Success)
                LoggedIn(info.LocalUserId);

            else if (info.ResultCode == Result.InvalidUser && info.ContinuanceToken != null)
            {
                var createOpt = new CreateUserOptions { ContinuanceToken = info.ContinuanceToken };
                Platform.GetConnectInterface().CreateUser(ref createOpt, null, (ref CreateUserCallbackInfo created) =>
                {
                    if (created.ResultCode == Result.Success)
                        LoggedIn(created.LocalUserId);
                    else
                        Log.Error($"[BACK] EOS user creation failed: {created.ResultCode}");
                });
            }
            else Log.Error($"[BACK] EOS login failed: {info.ResultCode}");
        });
    }

    /// <summary> Finishes the login: assigns the identity and wires the lobby notifications. </summary>
    private void LoggedIn(ProductUserId user)
    {
        LocalUser = user;
        selfId = Uid(user);

        // the local player may have been created before the login finished, so its identity needs a fix up
        Jaket.Tools.Tools.AccId = selfId;
        if (Networking.LocalPlayer.Id != selfId)
        {
            Networking.LocalPlayer.Id = Networking.LocalPlayer.Owner = selfId;
            Networking.LocalPlayer.Push();
        }
        Log.Info($"[BACK] Logged into EOS as {user}, account id is {selfId}");

        var lobby = Platform.GetLobbyInterface();

        var updOpt = new AddNotifyLobbyUpdateReceivedOptions();
        lobby.AddNotifyLobbyUpdateReceived(ref updOpt, null, (ref LobbyUpdateReceivedCallbackInfo info) =>
        {
            if (Lobby?.Id == info.LobbyId.ToString())
            {
                Lobby.Refresh();
                Events.OnLobbyAction.Fire();
            }
        });

        var memUpdOpt = new AddNotifyLobbyMemberUpdateReceivedOptions();
        lobby.AddNotifyLobbyMemberUpdateReceived(ref memUpdOpt, null, (ref LobbyMemberUpdateReceivedCallbackInfo info) =>
        {
            if (Lobby?.Id == info.LobbyId.ToString())
            {
                Lobby.Refresh();
                Events.OnLobbyAction.Fire();
            }
        });

        var memStsOpt = new AddNotifyLobbyMemberStatusReceivedOptions();
        lobby.AddNotifyLobbyMemberStatusReceived(ref memStsOpt, null, (ref LobbyMemberStatusReceivedCallbackInfo info) =>
        {
            if (Lobby?.Id != info.LobbyId.ToString()) return;

            var member = Lobby.Member(info.TargetUserId);
            Lobby.Refresh();

            switch (info.CurrentStatus)
            {
                case LobbyMemberStatus.Joined:
                    Events.OnMemberJoin.Fire(member);
                    Lobby.ValidateUniqueness(info.TargetUserId);
                    break;

                case LobbyMemberStatus.Left:
                case LobbyMemberStatus.Disconnected:
                case LobbyMemberStatus.Kicked:
                    Events.OnMemberLeave.Fire(member);
                    break;

                case LobbyMemberStatus.Closed:
                    // the lobby is destroyed, e.g. the owner left; report the owner leaving so that LobbyController reacts
                    Events.OnMemberLeave.Fire(Lobby.Owner);
                    break;
            }
        });
    }

    public void Tick()
    {
        Platform?.Tick();
        Lobby?.FlushPending();
    }

    public string NameOf(uint id)
    {
        if (id == selfId) return selfName;
        return Lobby?.NameOf(id) ?? $"Player#{id % 10000:0000}";
    }

    #region matchmaking

    public void CreateLobby(int maxMembers, Cons<INetLobby> done)
    {
        if (!Valid)
        {
            Log.Error("[BACK] Can't create a lobby: EOS is not logged in yet");
            done(null);
            return;
        }
        var createOpt = new CreateLobbyOptions
        {
            LocalUserId          = LocalUser,
            MaxLobbyMembers      = (uint)maxMembers,
            PermissionLevel      = LobbyPermissionLevel.Joinviapresence, // mirrors SetPrivate of the steam backend
            PresenceEnabled      = false,
            AllowInvites         = false,
            BucketId             = BUCKET_ID,
            DisableHostMigration = true,
            EnableJoinById       = true
        };
        Platform.GetLobbyInterface().CreateLobby(ref createOpt, null, (ref CreateLobbyCallbackInfo info) =>
        {
            if (info.ResultCode != Result.Success)
            {
                Log.Warning($"[LOBY] Couldn't create a lobby, the result is {info.ResultCode}");
                done(null);
                return;
            }
            Lobby = new EpicLobby(this, info.LobbyId);
            Lobby.PublishName();
            Lobby.Refresh();
            done(Lobby);
        });
    }

    public bool ValidCode(string code) => !string.IsNullOrWhiteSpace(code) && code.Length >= 8 && code.Length <= 64 && code.All(char.IsLetterOrDigit);

    public void JoinLobby(string code, Cons<INetLobby> done)
    {
        if (!Valid)
        {
            Log.Error("[BACK] Can't join a lobby: EOS is not logged in yet");
            done(null);
            return;
        }
        var joinOpt = new JoinLobbyByIdOptions { LobbyId = code, LocalUserId = LocalUser };
        Platform.GetLobbyInterface().JoinLobbyById(ref joinOpt, null, (ref JoinLobbyByIdCallbackInfo info) =>
        {
            if (info.ResultCode != Result.Success)
            {
                Log.Warning($"[LOBY] Couldn't join the lobby, the result is {info.ResultCode}");
                done(null);
                return;
            }
            Lobby = new EpicLobby(this, info.LobbyId);
            Lobby.PublishName();
            Lobby.Refresh();
            done(Lobby);
        });
    }

    public void FetchLobbies(Cons<LobbyInfo[]> done)
    {
        if (!Valid)
        {
            done([]);
            return;
        }
        var searchOpt = new CreateLobbySearchOptions { MaxResults = 64 };
        if (Platform.GetLobbyInterface().CreateLobbySearch(ref searchOpt, out var search) != Result.Success)
        {
            done([]);
            return;
        }

        var paramOpt = new LobbySearchSetParameterOptions
        {
            Parameter    = new AttributeData { Key = "client", Value = "jaket" },
            ComparisonOp = ComparisonOp.Equal
        };
        search.SetParameter(ref paramOpt);

        var findOpt = new LobbySearchFindOptions { LocalUserId = LocalUser };
        search.Find(ref findOpt, null, (ref LobbySearchFindCallbackInfo info) =>
        {
            var countOpt = new LobbySearchGetSearchResultCountOptions();
            uint count = info.ResultCode == Result.Success ? search.GetSearchResultCount(ref countOpt) : 0u;

            var infos = new LobbyInfo[count];
            for (uint i = 0; i < count; i++)
            {
                var copyOpt = new LobbySearchCopySearchResultByIndexOptions { LobbyIndex = i };
                if (search.CopySearchResultByIndex(ref copyOpt, out var details) != Result.Success) continue;

                var infoOpt = new LobbyDetailsCopyInfoOptions();
                details.CopyInfo(ref infoOpt, out var lobbyInfo);

                string Attr(string key)
                {
                    var attrOpt = new LobbyDetailsCopyAttributeByKeyOptions { AttrKey = key };
                    return details.CopyAttributeByKey(ref attrOpt, out var attr) == Result.Success ? attr?.Data?.Value.AsUtf8 : null;
                }

                string code = lobbyInfo?.LobbyId;
                infos[i] = new LobbyInfo
                {
                    Name        = Attr("name") ?? "How?",
                    Level       = Attr("level") ?? "How?",
                    MemberCount = (int)((lobbyInfo?.MaxMembers - lobbyInfo?.AvailableSlots) ?? 0),
                    MaxMembers  = (int)(lobbyInfo?.MaxMembers ?? 0),
                    Join        = () => LobbyController.JoinLobby(code)
                };
                details.Release();
            }
            search.Release();
            done(infos);
        });
    }

    #endregion
    #region transport

    public INetServer OpenServer(INetListener listener) => new EpicServerEndpoint(this, listener);

    public INetClient Connect(INetListener listener) => new EpicClientEndpoint(this, listener, (LobbyController.Lobby as EpicLobby).OwnerPuid);

    #endregion
    #region social

    public void OpenProfile(NetMember member) { } // there is no overlay

    public void OpenInviteOverlay()
    {
        // there is no overlay either, so copying the lobby code is the best available alternative
        LobbyController.CopyCode();
    }

    public void SetRichPresence(string key, string value) { }

    #endregion
}

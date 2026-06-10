namespace JaketEosSpike;

using BepInEx;
using BepInEx.Configuration;
using Epic.OnlineServices;
using Epic.OnlineServices.Connect;
using Epic.OnlineServices.Lobby;
using Epic.OnlineServices.Logging;
using Epic.OnlineServices.P2P;
using Epic.OnlineServices.Platform;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

/// <summary>
/// Minimal acceptance-test plugin for the EOS port: initializes the EOS platform under the game's
/// Mono runtime, logs in via Connect + Device ID (no Epic account), then either hosts a lobby and
/// echoes P2P packets, or finds the hosted lobby and pings it. Run two game instances to test.
/// </summary>
[BepInPlugin("xzxADIxzx.Jaket.EosSpike", "JaketEosSpike", "0.1.0")]
public class SpikePlugin : BaseUnityPlugin
{
    const string SOCKET_NAME = "jaket-spike";
    const string BUCKET_ID = "jaket:spike";

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr LoadLibraryW(string path);

    ConfigEntry<string> productId, sandboxId, deploymentId, clientId, clientSecret;

    PlatformInterface platform;
    ProductUserId localUser;
    ProductUserId peer;
    bool hosting;

    SocketId socketId = new() { SocketName = SOCKET_NAME };

    void Awake()
    {
        productId    = Config.Bind("EOS", "ProductId",    "", "Product ID from the Epic Developer Portal");
        sandboxId    = Config.Bind("EOS", "SandboxId",    "", "Sandbox ID from the Epic Developer Portal");
        deploymentId = Config.Bind("EOS", "DeploymentId", "", "Deployment ID from the Epic Developer Portal");
        clientId     = Config.Bind("EOS", "ClientId",     "", "Client ID of a GameClient policy client");
        clientSecret = Config.Bind("EOS", "ClientSecret", "", "Client secret of that client");

        if (productId.Value == "")
        {
            Logger.LogError("EOS credentials are not set; fill in BepInEx/config/xzxADIxzx.Jaket.EosSpike.cfg and restart");
            return;
        }

        // the game never loads the EOS native library, and the plugin folder is not on the dll search
        // path, so the library must be loaded explicitly before the first P/Invoke into it
        var native = Path.Combine(Path.GetDirectoryName(Info.Location), "EOSSDK-Win64-Shipping.dll");
        if (LoadLibraryW(native) == IntPtr.Zero)
        {
            Logger.LogError($"Failed to load {native}, error {Marshal.GetLastWin32Error()}");
            return;
        }

        var initOpt = new InitializeOptions { ProductName = "JaketEosSpike", ProductVersion = "0.1.0" };
        var initRes = PlatformInterface.Initialize(ref initOpt);
        if (initRes != Result.Success && initRes != Result.AlreadyConfigured)
        {
            Logger.LogError($"EOS initialize failed: {initRes}");
            return;
        }

        LoggingInterface.SetLogLevel(LogCategory.AllCategories, LogLevel.Warning);
        LoggingInterface.SetCallback((ref LogMessage msg) => Logger.LogWarning($"[EOS/{msg.Category}] {msg.Message}"));

        var options = new Options
        {
            ProductId         = productId.Value,
            SandboxId         = sandboxId.Value,
            DeploymentId      = deploymentId.Value,
            ClientCredentials = new ClientCredentials { ClientId = clientId.Value, ClientSecret = clientSecret.Value },
            Flags             = PlatformFlags.DisableOverlay | PlatformFlags.DisableSocialOverlay,
            CacheDirectory    = Path.Combine(Path.GetTempPath(), "jaket-eos-spike")
        };
        platform = PlatformInterface.Create(ref options);

        if (platform == null)
        {
            Logger.LogError("EOS platform creation failed, check the credentials");
            return;
        }

        Logger.LogInfo("EOS platform created, creating a device id...");

        var deviceOpt = new CreateDeviceIdOptions { DeviceModel = SystemInfo.deviceModel };
        platform.GetConnectInterface().CreateDeviceId(ref deviceOpt, null, (ref CreateDeviceIdCallbackInfo info) =>
        {
            if (info.ResultCode != Result.Success && info.ResultCode != Result.DuplicateNotAllowed)
            {
                Logger.LogError($"Device id creation failed: {info.ResultCode}");
                return;
            }
            Login();
        });
    }

    void Update()
    {
        platform?.Tick();
        if (localUser != null) Poll();
    }

    void OnDestroy()
    {
        platform?.Release();
        platform = null;
        PlatformInterface.Shutdown();
    }

    void Login()
    {
        Logger.LogInfo("Logging in via Connect...");

        var loginOpt = new LoginOptions
        {
            Credentials   = new Credentials { Token = null, Type = ExternalCredentialType.DeviceidAccessToken },
            UserLoginInfo = new UserLoginInfo { DisplayName = "Spike" + new System.Random().Next(1000, 9999) }
        };
        platform.GetConnectInterface().Login(ref loginOpt, null, (ref LoginCallbackInfo info) =>
        {
            if (info.ResultCode != Result.Success)
            {
                Logger.LogError($"Connect login failed: {info.ResultCode}");
                return;
            }
            localUser = info.LocalUserId;
            Logger.LogInfo($"Logged in as {localUser}");

            var notifyOpt = new AddNotifyPeerConnectionRequestOptions { LocalUserId = localUser, SocketId = socketId };
            platform.GetP2PInterface().AddNotifyPeerConnectionRequest(ref notifyOpt, null, (ref OnIncomingConnectionRequestInfo req) =>
            {
                Logger.LogInfo($"Accepting connection from {req.RemoteUserId}");
                var acceptOpt = new AcceptConnectionOptions { LocalUserId = localUser, RemoteUserId = req.RemoteUserId, SocketId = socketId };
                platform.GetP2PInterface().AcceptConnection(ref acceptOpt);
            });

            Search();
        });
    }

    /// <summary> Looks for an existing spike lobby: joins it if found, hosts a new one otherwise. </summary>
    void Search()
    {
        var searchOpt = new CreateLobbySearchOptions { MaxResults = 1 };
        if (platform.GetLobbyInterface().CreateLobbySearch(ref searchOpt, out var search) != Result.Success)
        {
            Logger.LogError("Lobby search creation failed");
            return;
        }

        var paramOpt = new LobbySearchSetParameterOptions
        {
            Parameter    = new AttributeData { Key = "client", Value = new AttributeDataValue { AsUtf8 = "jaket-spike" } },
            ComparisonOp = ComparisonOp.Equal
        };
        search.SetParameter(ref paramOpt);

        var findOpt = new LobbySearchFindOptions { LocalUserId = localUser };
        search.Find(ref findOpt, null, (ref LobbySearchFindCallbackInfo info) =>
        {
            var countOpt = new LobbySearchGetSearchResultCountOptions();
            if (info.ResultCode == Result.Success && search.GetSearchResultCount(ref countOpt) > 0)
            {
                var copyOpt = new LobbySearchCopySearchResultByIndexOptions { LobbyIndex = 0 };
                search.CopySearchResultByIndex(ref copyOpt, out var details);

                var infoOpt = new LobbyDetailsCopyInfoOptions();
                details.CopyInfo(ref infoOpt, out var lobbyInfo);

                Logger.LogInfo($"Found spike lobby {lobbyInfo?.LobbyId}, joining as client");
                Join(details);
            }
            else
            {
                Logger.LogInfo("No spike lobby found, hosting a new one");
                Host();
            }
            search.Release();
        });
    }

    void Host()
    {
        hosting = true;
        var createOpt = new CreateLobbyOptions
        {
            LocalUserId      = localUser,
            MaxLobbyMembers  = 8,
            PermissionLevel  = LobbyPermissionLevel.Publicadvertised,
            PresenceEnabled  = false,
            AllowInvites     = false,
            BucketId         = BUCKET_ID,
            EnableJoinById   = true
        };
        platform.GetLobbyInterface().CreateLobby(ref createOpt, null, (ref CreateLobbyCallbackInfo info) =>
        {
            if (info.ResultCode != Result.Success)
            {
                Logger.LogError($"Lobby creation failed: {info.ResultCode}");
                return;
            }
            Logger.LogInfo($"Lobby {info.LobbyId} created, advertising and waiting for pings...");

            var modOpt = new UpdateLobbyModificationOptions { LobbyId = info.LobbyId, LocalUserId = localUser };
            platform.GetLobbyInterface().UpdateLobbyModification(ref modOpt, out var mod);

            var attrOpt = new LobbyModificationAddAttributeOptions
            {
                Attribute  = new AttributeData { Key = "client", Value = new AttributeDataValue { AsUtf8 = "jaket-spike" } },
                Visibility = LobbyAttributeVisibility.Public
            };
            mod.AddAttribute(ref attrOpt);

            var updateOpt = new UpdateLobbyOptions { LobbyModificationHandle = mod };
            platform.GetLobbyInterface().UpdateLobby(ref updateOpt, null, (ref UpdateLobbyCallbackInfo upd) =>
            {
                Logger.LogInfo($"Lobby attributes published: {upd.ResultCode}");
                mod.Release();
            });
        });
    }

    void Join(LobbyDetails details)
    {
        var joinOpt = new JoinLobbyOptions { LobbyDetailsHandle = details, LocalUserId = localUser };
        platform.GetLobbyInterface().JoinLobby(ref joinOpt, null, (ref JoinLobbyCallbackInfo info) =>
        {
            if (info.ResultCode != Result.Success)
            {
                Logger.LogError($"Lobby join failed: {info.ResultCode}");
                return;
            }

            var ownerOpt = new LobbyDetailsGetLobbyOwnerOptions();
            peer = details.GetLobbyOwner(ref ownerOpt);
            details.Release();

            Logger.LogInfo($"Joined lobby {info.LobbyId}, pinging the owner {peer}");
            Send(peer, "PING " + DateTime.Now.ToLongTimeString());
        });
    }

    void Send(ProductUserId target, string text)
    {
        var data = Encoding.UTF8.GetBytes(text);
        var sendOpt = new SendPacketOptions
        {
            LocalUserId          = localUser,
            RemoteUserId         = target,
            SocketId             = socketId,
            Channel              = 0,
            Data                 = new ArraySegment<byte>(data),
            AllowDelayedDelivery = true,
            Reliability          = PacketReliability.ReliableOrdered
        };
        var res = platform.GetP2PInterface().SendPacket(ref sendOpt);
        Logger.LogInfo($"Sent \"{text}\" to {target}: {res}");
    }

    byte[] buffer = new byte[1170];

    void Poll()
    {
        var sizeOpt = new GetNextReceivedPacketSizeOptions { LocalUserId = localUser };
        while (platform.GetP2PInterface().GetNextReceivedPacketSize(ref sizeOpt, out var size) == Result.Success)
        {
            var recvOpt = new ReceivePacketOptions { LocalUserId = localUser, MaxDataSizeBytes = (uint)buffer.Length };
            ProductUserId sender = null;
            SocketId sock = default;

            var res = platform.GetP2PInterface().ReceivePacket(ref recvOpt, ref sender, ref sock, out _, new ArraySegment<byte>(buffer), out var written);
            if (res != Result.Success) break;

            var text = Encoding.UTF8.GetString(buffer, 0, (int)written);
            Logger.LogInfo($"Received \"{text}\" from {sender}");

            // the host echoes everything back; receiving a PONG on the other side completes the test
            if (hosting) Send(sender, "PONG " + text);
            else Logger.LogInfo("=== SPIKE TEST PASSED: lobby + P2P echo work ===");
        }
    }
}

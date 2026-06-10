namespace Jaket.Net.Backends;

using Epic.OnlineServices;
using Epic.OnlineServices.Lobby;
using System.Collections.Generic;

/// <summary>
/// Lobby implementation over EOS lobbies. Lobby data lives in public lobby attributes, member names live
/// in member attributes, and the account id of every member is a stable hash of its product user id,
/// validated by the lobby owner. Attribute writes are batched and flushed once per frame.
/// </summary>
public class EpicLobby : INetLobby
{
    private readonly EpicBackend backend;
    /// <summary> Identifier of the lobby, also used as the join code. </summary>
    public readonly string Id;

    /// <summary> Cached lobby attributes, updated on every refresh. </summary>
    private Dictionary<string, string> attrs = new();
    /// <summary> Attribute writes waiting to be flushed; only the owner can flush them. </summary>
    private Dictionary<string, string> pending = new();
    /// <summary> Cached list of members: product user id, account id and name. </summary>
    private List<(ProductUserId puid, uint uid, string name)> members = new();

    /// <summary> Cached lobby info. </summary>
    private ProductUserId ownerPuid;
    private int maxMembers;
    /// <summary> Pending visibility change, null if there is none. </summary>
    private LobbyPermissionLevel? pendingLevel;
    /// <summary> Whether the local name is yet to be published as a member attribute. </summary>
    private bool pendingName;

    public EpicLobby(EpicBackend backend, string id)
    {
        this.backend = backend;
        Id = id;
    }

    /// <summary> Product user id of the lobby owner; the client endpoint connects to it. </summary>
    public ProductUserId OwnerPuid => ownerPuid;

    /// <summary> Refreshes the cached attributes, members and info from the lobby details. </summary>
    public void Refresh()
    {
        var copyOpt = new CopyLobbyDetailsHandleOptions { LobbyId = Id, LocalUserId = backend.LocalUser };
        if (backend.Platform.GetLobbyInterface().CopyLobbyDetailsHandle(ref copyOpt, out var details) != Result.Success) return;

        var infoOpt = new LobbyDetailsCopyInfoOptions();
        if (details.CopyInfo(ref infoOpt, out var info) == Result.Success && info != null)
        {
            ownerPuid = info?.LobbyOwnerUserId;
            maxMembers = (int)(info?.MaxMembers ?? 0);
        }

        attrs.Clear();
        var countOpt = new LobbyDetailsGetAttributeCountOptions();
        uint count = details.GetAttributeCount(ref countOpt);

        for (uint i = 0; i < count; i++)
        {
            var attrOpt = new LobbyDetailsCopyAttributeByIndexOptions { AttrIndex = i };
            if (details.CopyAttributeByIndex(ref attrOpt, out var attr) == Result.Success && attr?.Data != null)
            {
                var data = attr?.Data;
                attrs[data?.Key] = data?.Value.AsUtf8;
            }
        }

        members.Clear();
        var memberCountOpt = new LobbyDetailsGetMemberCountOptions();
        uint memberCount = details.GetMemberCount(ref memberCountOpt);

        for (uint i = 0; i < memberCount; i++)
        {
            var memberOpt = new LobbyDetailsGetMemberByIndexOptions { MemberIndex = i };
            var puid = details.GetMemberByIndex(ref memberOpt);
            if (puid == null) continue;

            var nameOpt = new LobbyDetailsCopyMemberAttributeByKeyOptions { TargetUserId = puid, AttrKey = "name" };
            string name = details.CopyMemberAttributeByKey(ref nameOpt, out var attr) == Result.Success ? attr?.Data?.Value.AsUtf8 : null;

            members.Add((puid, EpicBackend.Uid(puid), name));
        }
        details.Release();
    }

    /// <summary> Wraps the member with the given product user id. </summary>
    public NetMember Member(ProductUserId puid)
    {
        uint uid = EpicBackend.Uid(puid);
        return new()
        {
            AccId = uid,
            Raw   = uid,
            Name  = NameOf(uid) ?? $"Player#{uid % 10000:0000}",
            IsMe  = puid == backend.LocalUser
        };
    }

    /// <summary> Returns the name of the member with the given account id, null if not found. </summary>
    public string NameOf(uint uid)
    {
        var found = members.Find(m => m.uid == uid);
        return found.puid == null ? null : found.name ?? $"Player#{uid % 10000:0000}";
    }

    /// <summary> Account ids are hashes, so the owner kicks any member whose hash collides with an existing one. </summary>
    public void ValidateUniqueness(ProductUserId joined)
    {
        if (!LobbyController.IsOwner || joined == backend.LocalUser) return;

        uint uid = EpicBackend.Uid(joined);
        if (members.Any(m => m.uid == uid && m.puid != joined))
        {
            Log.Warning($"[LOBY] Kicking {joined}: account id hash collision");
            var kickOpt = new KickMemberOptions { LobbyId = Id, LocalUserId = backend.LocalUser, TargetUserId = joined };
            backend.Platform.GetLobbyInterface().KickMember(ref kickOpt, null, (ref KickMemberCallbackInfo info) => { });
        }
    }

    /// <summary> Requests publishing of the local player name as a member attribute. </summary>
    public void PublishName() => pendingName = true;

    /// <summary> Flushes pending attribute and visibility changes; called once per frame. </summary>
    public void FlushPending()
    {
        if (pending.Count == 0 && pendingLevel == null && !pendingName) return;

        var modOpt = new UpdateLobbyModificationOptions { LobbyId = Id, LocalUserId = backend.LocalUser };
        if (backend.Platform.GetLobbyInterface().UpdateLobbyModification(ref modOpt, out var mod) != Result.Success) return;

        foreach (var pair in pending)
        {
            var attrOpt = new LobbyModificationAddAttributeOptions
            {
                Attribute  = new AttributeData { Key = pair.Key, Value = pair.Value },
                Visibility = LobbyAttributeVisibility.Public
            };
            mod.AddAttribute(ref attrOpt);
            attrs[pair.Key] = pair.Value; // optimistic, so that GetData sees the value immediately
        }
        pending.Clear();

        if (pendingLevel != null)
        {
            var lvlOpt = new LobbyModificationSetPermissionLevelOptions { PermissionLevel = pendingLevel.Value };
            mod.SetPermissionLevel(ref lvlOpt);
            pendingLevel = null;
        }

        if (pendingName)
        {
            var nameOpt = new LobbyModificationAddMemberAttributeOptions
            {
                Attribute  = new AttributeData { Key = "name", Value = backend.SelfName },
                Visibility = LobbyAttributeVisibility.Public
            };
            mod.AddMemberAttribute(ref nameOpt);
            pendingName = false;
        }

        var updateOpt = new UpdateLobbyOptions { LobbyModificationHandle = mod };
        backend.Platform.GetLobbyInterface().UpdateLobby(ref updateOpt, null, (ref UpdateLobbyCallbackInfo info) =>
        {
            if (info.ResultCode != Result.Success) Log.Warning($"[LOBY] Lobby update failed: {info.ResultCode}");
            mod.Release();
        });
    }

    #region lobby

    public string Code => Id;

    public NetMember Owner => ownerPuid == null ? default : Member(ownerPuid);

    public IEnumerable<NetMember> Members
    {
        get
        {
            foreach (var member in members) yield return Member(member.puid);
        }
    }

    public int MemberCount => members.Count;
    public int MaxMembers => maxMembers;

    public string GetData(string key) => pending.TryGetValue(key, out var queued) ? queued : attrs.TryGetValue(key, out var value) ? value : null;

    public void SetData(string key, string value)
    {
        if (!LobbyController.IsOwner || value == null) return;
        if (GetData(key) != value) pending[key] = value;
    }

    public void SetVisibility(byte level) => pendingLevel = level == 2 ? LobbyPermissionLevel.Publicadvertised : LobbyPermissionLevel.Joinviapresence;

    public void Leave()
    {
        if (backend.Lobby == this) backend.Lobby = null;

        var leaveOpt = new LeaveLobbyOptions { LobbyId = Id, LocalUserId = backend.LocalUser };
        backend.Platform.GetLobbyInterface().LeaveLobby(ref leaveOpt, null, (ref LeaveLobbyCallbackInfo info) => { });
    }

    #endregion
}

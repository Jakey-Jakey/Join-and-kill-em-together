namespace Jaket.Net;

using UnityEngine;

using Jaket.Assets;
using Jaket.Net.Backends;

/// <summary> Class responsible for managing the lobby. </summary>
public static class LobbyController
{
    /// <summary> Matchmaking lobby that the player is connected to. </summary>
    public static INetLobby Lobby;
    public static bool Online => Lobby != null;
    public static bool Offline => Lobby == null;

    /// <summary> Identifier of the lobby owner. </summary>
    public static uint Owner;
    /// <summary> Whether the player owns the lobby. </summary>
    public static bool IsOwner;

    /// <summary> Whether a lobby is being produced. </summary>
    public static bool Creating { get; private set; }
    /// <summary> Whether lobbies are being fetched. </summary>
    public static bool Fetching { get; private set; }

    /// <summary> Subscribes to several events for proper work. </summary>
    public static void Load()
    {
        Events.OnLobbyAction += () =>
        {
            Log.Debug($"[LOBY] Lobby name is {LobbyConfig.Name ?? "null"}, mode is {LobbyConfig.Mode ?? "null"}, level is {LobbyConfig.Level ?? "null"}");

            if (Online && !IsOwner && LobbyConfig.Banned.Has(AccId.ToString()))
            {
                // notify the player to avoid confusion
                Bundle.Hud2NS("lobby.banned");

                LeaveLobby();
                Log.Info("[LOBY] Left the lobby due to being banned");
            }

            if (Online && !IsOwner && Version.HasIncompatibility && !LobbyConfig.ModsAllowed)
            {
                // notify the player to avoid confusion
                Bundle.Hud2NS("lobby.modded");

                LeaveLobby();
                Log.Info("[LOBY] Left the lobby due to incompatible mods");
            }
        };

        Events.OnMemberLeave += m =>
        {
            if (Owner == m.AccId)
            {
                LeaveLobby();
                Log.Info("[LOBY] Left the lobby due to owner leaving");
            }
        };

        Events.OnLoad += () => LobbyConfig.Level = Scene;
        Events.OnMainMenuLoad += () => LeaveLobby(false);
    }

    /// <summary> Whether there is a player with the given id among the members of the lobby. </summary>
    public static bool Contains(uint id) => Lobby?.Members.Any(m => m.AccId == id) ?? false;

    /// <summary> Returns identifier of the member with the given index. </summary>
    public static uint MemberId(int index)
    {
        foreach (var member in Lobby?.Members) if (index-- <= 0) return member.AccId;
        return 0u;
    }

    #region control

    /// <summary> Creates a new lobby and joins it. </summary>
    public static void CreateLobby()
    {
        if (Creating || Online) return;
        Log.Info("[LOBY] Creating a lobby...");

        Creating = true;
        NetBackends.Current.CreateLobby(8, lobby =>
        {
            Creating = false;
            Lobby = lobby;
            Owner = AccId;
            IsOwner = true;

            LobbyConfig.Reset();

            Events.OnLobbyEnter.Fire();
            Log.Info("[LOBY] Successfully created a lobby");
        });
    }

    /// <summary> Leaves the lobby and, if necessary, loads the main menu. </summary>
    public static void LeaveLobby(bool load = true)
    {
        if (Creating || Offline) return;
        Log.Info("[LOBY] Leaving the lobby...");

        Lobby?.Leave();
        Lobby = null;

        Networking.Close();
        if (load) LoadScn("Main Menu");

        Events.OnLobbyAction.Fire();
        Log.Info("[LOBY] Successfully left the lobby");
    }

    /// <summary> Opens an invitation dialog. </summary>
    public static void InviteFriend() => NetBackends.Current.OpenInviteOverlay();

    /// <summary> Connects to the lobby with the given code. </summary>
    public static void JoinLobby(string code)
    {
        if (Lobby?.Code == code) return;
        Log.Info("[LOBY] Joining a lobby...");

        // leave the previous lobby before joining the new one
        if (Online) LeaveLobby(false);

        NetBackends.Current.JoinLobby(code, lobby =>
        {
            if (lobby != null)
            {
                Lobby = lobby;
                Owner = lobby.Owner.AccId;
                IsOwner = false;

                Events.OnLobbyEnter.Fire();
                Log.Info($"[LOBY] Successfully joined the lobby");
            }
            else Log.Warning($"[LOBY] Couldn't join the lobby");
        });
    }

    #endregion
    #region codes & browser

    /// <summary> Copies the lobby code to the clipboard. </summary>
    public static void CopyCode()
    {
        GUIUtility.systemCopyBuffer = Lobby?.Code ?? "How?";
        Bundle.Hud("lobby.copied");
    }

    /// <summary> Joins by the lobby code from the clipboard. </summary>
    public static void JoinByCode()
    {
        if (NetBackends.Current.ValidCode(GUIUtility.systemCopyBuffer)) JoinLobby(GUIUtility.systemCopyBuffer);
        else Bundle.Hud("lobby.failed");
    }

    /// <summary> Fetches the list of public lobbies. </summary>
    public static void FetchLobbies(Cons<LobbyInfo[]> cons)
    {
        if (Fetching) return;
        Log.Info("[LOBY] Fetching the list of public lobbies...");

        Fetching = true;
        NetBackends.Current.FetchLobbies(lobbies =>
        {
            Fetching = false;
            cons(lobbies ?? []);

            Log.Info($"[LOBY] Fetched {lobbies?.Length ?? 0} lobbies");
        });
    }

    #endregion
}

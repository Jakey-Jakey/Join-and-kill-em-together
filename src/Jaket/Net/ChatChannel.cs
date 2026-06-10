namespace Jaket.Net;

using System.Text;
using UnityEngine;

using Jaket.Assets;
using Jaket.Content;
using Jaket.IO;
using Jaket.Net.Admin;
using Jaket.Net.Types;
using Jaket.Sam;
using Jaket.UI;
using Jaket.UI.Dialogs;
using Jaket.World;

/// <summary>
/// Class responsible for the chat control channel. Previously the mod abused steam lobby chat for this;
/// now both text messages and #/x control messages travel as ordinary packets relayed by the host,
/// which makes the channel backend independent.
/// </summary>
public static class ChatChannel
{
    /// <summary> Sends the given message to the members of the lobby and processes it locally. </summary>
    public static void Send(string msg)
    {
        if (LobbyController.Offline) return;
        if (msg.Length > Chat.MAX_LENGTH + 4) msg = msg[..Chat.MAX_LENGTH];

        var bytes = Encoding.UTF8.GetBytes(msg);
        Networking.Send(PacketType.Chat, 4 + bytes.Length, w =>
        {
            w.Id(AccId);
            w.Bytes(bytes);
        });
        Process(AccId, msg);
    }

    /// <summary> Reads a chat packet body, the sender identifier is already consumed by the caller. </summary>
    public static void Receive(uint sender, int bytesCount, Reader r)
    {
        if (Administration.Banned.Contains(sender)) return;

        var bytes = new byte[bytesCount];
        r.Bytes(bytes);

        Process(sender, Encoding.UTF8.GetString(bytes));
    }

    /// <summary> Processes both control and text messages; ported from the old steam lobby chat handler. </summary>
    public static void Process(uint sender, string msg)
    {
        if (msg.Length > Chat.MAX_LENGTH + 4) msg = msg[..Chat.MAX_LENGTH];

        string name = sender.Name.Replace("[", "\\[");
        bool owner = sender == LobbyController.Owner;
        bool me = sender == AccId;

        if (msg == "#/d")
        {
            Bundle.Msg("player.died", name);

            StyleHUD.Instance.AddPoints(Mathf.RoundToInt(420f * StyleCalculator.Instance.airTime), Bundle.Parse("[green]FRATRICIDE"));

            Gameflow.OnDeath(sender);

            if (LobbyConfig.HealBosses) Networking.Entities.Alive<Enemy>(e => e.Boss, e => e.Heal());
        }

        else if (msg.StartsWith("#/s") && uint.TryParse(msg[3..], out uint sid) && owner)
            Gameflow.OnStart(sid);

        else if (msg.StartsWith("#/v") && byte.TryParse(msg[3..], out byte vid) && owner)
            Gameflow.OnVictory(vid);

        else if (msg.StartsWith("#/b") && uint.TryParse(msg[3..], out uint bid) && owner)
            Bundle.Msg("player.banned", bid.Name);

        else if (msg.StartsWith("#/r") && byte.TryParse(msg[3..], out byte rps))
            Bundle.Msg("emote.roll", name, $"#emote.{rps}");

        else if (msg.StartsWith("#/t"))
        {
            if (me)
                SamAPI.TryPlay(msg = msg[3..], Networking.LocalPlayer.Voice);

            else if (Networking.Entities.TryGetValue(sender, out var e) && e is RemotePlayer p)
                SamAPI.TryPlay(msg = msg[3..], p.Voice);

            UI.Chat.Receive(msg, Networking.GetColor(sender), name, Chat.TTS_TAG);
        }
        else
            UI.Chat.Receive(msg, Networking.GetColor(sender), name);
    }
}

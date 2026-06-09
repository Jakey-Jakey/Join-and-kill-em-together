namespace Jaket.World;

using System.Collections.Generic;
using System.Text;
using UnityEngine;

using Jaket.Content;
using Jaket.Harmony;
using Jaket.Input;
using Jaket.IO;
using Jaket.Net;
using Jaket.Net.Types;

/// <summary> Class responsible for Cyber Grind synchronization. </summary>
public class CyberGrind
{
    static EndlessGrid grid => EndlessGrid.Instance;
    static NewMovement nm => NewMovement.Instance;

    /// <summary> Side of the arena in cubes, it also defines the dimensions of patterns. </summary>
    public const int ARENA_SIZE = 16;
    /// <summary> Symbols that the prefabs part of a pattern can consist of. </summary>
    public const string PREFABS = "0npJsH";
    /// <summary> Number of bytes that a wave takes in a packet: number, heights and prefabs. </summary>
    public const int WAVE_SIZE = 4 + ARENA_SIZE * ARENA_SIZE + ARENA_SIZE * ARENA_SIZE / 2;

    /// <summary> Number of the current wave, synced by the host. </summary>
    public static int CurrentWave;
    /// <summary> Pattern of the current wave, synced by the host. </summary>
    public static ArenaPattern CurrentPattern;
    /// <summary> Name of the current pattern, empty unless the host uses custom patterns. </summary>
    public static string CurrentName = "";

    /// <summary> Wave progress synced by the host: dead, spawned and total amount of enemies. </summary>
    public static int DeadEnemies, TempEnemies, TotalEnemies = 999;
    /// <summary> Identifiers of radiant enemies, synced by the host so that buffs are visible to everyone. </summary>
    public static List<uint> Radiant = new();

    /// <summary> Wave that is to be loaded once the grid is ready. </summary>
    private static ArenaPattern pendingPattern;
    /// <summary> Number of the wave that is to be loaded. </summary>
    private static int pendingWave;
    /// <summary> Name of the pattern that is to be loaded. </summary>
    private static string pendingName = "";

    /// <summary> Radiant enemies whose entities are not created yet. </summary>
    private static List<EnemyIdentifier> spawned = new();
    /// <summary> Identifiers of radiant enemies that have already been buffed. </summary>
    private static List<uint> buffed = new();

    /// <summary> Values of the last progress snapshot, used to avoid sending the same data twice. </summary>
    private static int lastDead = -1, lastTemp = -1, lastTotal = -1;

    /// <summary> Subscribes to several events for proper work. </summary>
    public static void Load()
    {
        Events.OnLoadingStart += () =>
        {
            CurrentWave = pendingWave = 0;
            CurrentPattern = pendingPattern = null;
            CurrentName = pendingName = "";

            DeadEnemies = TempEnemies = 0;
            TotalEnemies = 999;
            lastDead = lastTemp = lastTotal = -1;

            Radiant.Clear();
            spawned.Clear();
            buffed.Clear();
        };

        Events.OnLoad += () =>
        {
            if (LobbyController.Online && !LobbyController.IsOwner && Scene == "Endless") Apply();
        };

        // late joiners receive the current wave within the level data, but the progress has to be resent
        Events.OnMemberJoin += _ => lastDead = lastTemp = lastTotal = -1;

        Events.EveryTick += () =>
        {
            if (LobbyController.Offline || !LobbyController.IsOwner || Scene != "Endless" || grid == null || grid.anw == null) return;

            if (grid.anw.deadEnemies != lastDead || grid.tempEnemyAmount != lastTemp || grid.enemyAmount != lastTotal) Networking.Send(PacketType.CyberAction, 13, w =>
            {
                w.Byte(1);
                w.Int(lastDead  = grid.anw.deadEnemies);
                w.Int(lastTemp  = grid.tempEnemyAmount);
                w.Int(lastTotal = grid.enemyAmount);
            });
        };

        Events.EveryHalf += () =>
        {
            if (LobbyController.Offline || Scene != "Endless") return;

            if (LobbyController.IsOwner)
            {
                spawned.RemoveAll(eid =>
                {
                    if (eid == null) return true;
                    if (eid.TryGetEntity(out Enemy e)) { Radiant.Add(e.Id); return true; }
                    return false;
                });
                Radiant.RemoveAll(id => Networking.Entities[id] is not Enemy e || e.Hidden);

                if (Radiant.Count > 0) SyncRadiance();
            }
            else Radiant.Each(id =>
            {
                if (!buffed.Contains(id) && Networking.Entities[id] is Enemy e && !e.Hidden)
                {
                    e.Irradiate();
                    buffed.Add(id);
                }
            });
        };
    }

    /// <summary> Number of living players. </summary>
    public static int PlayersAlive()
    {
        int amount = nm.dead ? 0 : 1;
        Networking.Entities.Player(p => p.Health > 0, p => amount++);
        return amount;
    }

    #region data

    /// <summary> Whether the current wave has to be present in the level data. </summary>
    public static bool Synced => LobbyController.IsOwner && (Pending ?? Scene) == "Endless" && CurrentPattern != null;

    /// <summary> Number of bytes that the wave takes in a packet, including the name of the pattern. </summary>
    public static int WaveSize => WAVE_SIZE + 1 + CurrentName.Length;

    /// <summary> Number of bytes that the wave data takes in the level data. </summary>
    public static int BufferSize => Synced ? 1 + WaveSize : 1;

    /// <summary> Writes the wave data into the level data so that late joiners catch up. </summary>
    public static void WriteData(Writer w)
    {
        w.Bool(Synced);
        if (Synced) WriteWave(w);
    }

    /// <summary> Reads the wave data from the level data. </summary>
    public static void ReadData(Reader r)
    {
        if (r.Bool()) ReadWave(r);
    }

    /// <summary> Writes the current wave into memory. </summary>
    public static void WriteWave(Writer w)
    {
        w.Int(CurrentWave);
        EncodeHeights(ref w, CurrentPattern.heights);
        EncodePrefabs(ref w, CurrentPattern.prefabs);
        w.String(CurrentName);
    }

    /// <summary> Reads a wave from memory and queues its loading. </summary>
    public static void ReadWave(Reader r)
    {
        pendingWave = r.Int();
        pendingPattern = ScriptableObject.CreateInstance<ArenaPattern>();
        pendingPattern.heights = DecodeHeights(ref r);
        pendingPattern.prefabs = DecodePrefabs(ref r);
        pendingName = r.String();
    }

    /// <summary> Performs an action received from the host. </summary>
    public static void Perform(Reader r)
    {
        switch (r.Byte())
        {
            case 0:
                ReadWave(r);
                Apply();
                break;
            case 1:
                DeadEnemies  = r.Int();
                TempEnemies  = r.Int();
                TotalEnemies = r.Int();
                break;
            case 2:
                for (int amount = r.Byte(); amount > 0; amount--)
                {
                    var id = r.Id();
                    if (!Radiant.Contains(id)) Radiant.Add(id);
                }
                break;
        }
    }

    #endregion
    #region logic

    /// <summary> Sends the given arena pattern to all of the clients. </summary>
    public static void SyncPattern(ArenaPattern pattern)
    {
        CurrentWave = grid.currentWave;
        CurrentPattern = pattern;
        CurrentName = grid.customPatternMode ? CleanName(pattern.name) : "";

        Networking.Send(PacketType.CyberAction, 1 + WaveSize, w =>
        {
            w.Byte(0);
            WriteWave(w);
        });
    }

    /// <summary> Sends the identifiers of radiant enemies to all of the clients. </summary>
    public static void SyncRadiance()
    {
        int amount = Mathf.Min(Radiant.Count, 64);

        Networking.Send(PacketType.CyberAction, 2 + 4 * amount, w =>
        {
            w.Byte(2);
            w.Byte((byte)amount);
            for (int i = 0; i < amount; i++) w.Id(Radiant[i]);
        });
    }

    /// <summary> Loads the pending wave as soon as the grid is ready. </summary>
    public static void Apply() => Events.Post(() => pendingPattern == null || (Scene == "Endless" && grid != null && grid.cubes != null), () =>
    {
        if (pendingPattern == null || Scene != "Endless") return;

        CurrentWave = pendingWave;
        CurrentPattern = pendingPattern;
        CurrentName = pendingName;
        pendingPattern = null;

        // display the name of the pattern just like the vanilla custom mode does
        if (CurrentName != "") HudMessageReceiver.Instance?.SendHudMessage(CurrentName, "", "", 0, silent: true);

        // activate the Huge Flying Panel™
        grid.waveNumberText.transform.parent.parent.gameObject.SetActive(true);

        // ensure that the pattern pool is not empty, otherwise the synced pattern won't be loaded
        grid.customPatternMode = false;
        grid.NextWave();

        // do not reset any value if it is the first load
        var col = grid.GetComponent<Collider>();
        if (col.enabled)
        {
            col.enabled = false; // start the timer and the music
            ObjFind("Everything")?.transform.Find("Timer")?.gameObject.SetActive(true);
            return;
        }

        // play cheering sound effect
        var cr = CrowdReactions.Instance;
        if (cr) cr.React(cr.cheerLong);

        if (nm.dead) Movement.CyberRespawn();
    });

    /// <summary> Extracts a readable name from the path of a custom pattern, mirroring the vanilla logic. </summary>
    public static string CleanName(string name)
    {
        name = name.Split('\\', '/')[^1];
        if (name.Length > 4) name = name[..^4];

        name = name.Replace("CG_", "").Replace("Cg_", "").Replace("cg_", "").Replace('_', ' ');
        name = System.Text.RegularExpressions.Regex.Replace(name, @"(\P{Ll})(\P{Ll}\p{Ll})", "$1 $2");
        name = System.Text.RegularExpressions.Regex.Replace(name, @"(\p{Ll})(\P{Ll})",       "$1 $2");
        name = name.Replace("  ", " ").Trim().ToUpper();

        return name.Length > 64 ? name[..64] : name;
    }

    #endregion
    #region pattern

    /// <summary> Encodes the heights part of a pattern, one signed byte per cube. </summary>
    private static void EncodeHeights(ref Writer w, string heights)
    {
        var rows = heights.Split('\n');
        for (int i = 0; i < ARENA_SIZE; i++)
        {
            var row = i < rows.Length ? rows[i] : "";
            int cubes = 0;

            for (int j = 0; j < row.Length && cubes < ARENA_SIZE; j++)
            {
                char c = row[j];
                if (c == '(')
                {
                    int e = row.IndexOf(')', j);
                    if (e == -1) break;

                    int.TryParse(row[(j + 1)..e], out int value);
                    w.Byte((byte)(sbyte)Mathf.Clamp(value, sbyte.MinValue, sbyte.MaxValue));

                    cubes++;
                    j = e;
                }
                else if (char.IsDigit(c))
                {
                    w.Byte((byte)(c - '0'));
                    cubes++;
                }
                else if (c == '-')
                {
                    w.Byte(0);
                    cubes++;
                }
            }
            while (cubes++ < ARENA_SIZE) w.Byte(0);
        }
    }

    /// <summary> Decodes the heights part of a pattern back into vanilla format. </summary>
    private static string DecodeHeights(ref Reader r)
    {
        StringBuilder sb = new(ARENA_SIZE * ARENA_SIZE * 2);
        for (int i = 0; i < ARENA_SIZE; i++)
        {
            if (i > 0) sb.Append('\n');
            for (int j = 0; j < ARENA_SIZE; j++)
            {
                int value = (sbyte)r.Byte();
                if (value >= 0 && value <= 9)
                    sb.Append((char)('0' + value));
                else
                    sb.Append('(').Append(value).Append(')');
            }
        }
        return sb.ToString();
    }

    /// <summary> Encodes the prefabs part of a pattern, one nibble per cube. </summary>
    private static void EncodePrefabs(ref Writer w, string prefabs)
    {
        var rows = prefabs.Split('\n');
        int half = 0;
        bool odd = false;

        for (int i = 0; i < ARENA_SIZE; i++)
        {
            var row = i < rows.Length ? rows[i] : "";
            for (int j = 0; j < ARENA_SIZE; j++)
            {
                int value = j < row.Length ? Mathf.Max(0, PREFABS.IndexOf(row[j])) : 0;

                if (odd)
                    w.Byte((byte)(half | value << 4));
                else
                    half = value;

                odd = !odd;
            }
        }
    }

    /// <summary> Decodes the prefabs part of a pattern back into vanilla format. </summary>
    private static string DecodePrefabs(ref Reader r)
    {
        StringBuilder sb = new(ARENA_SIZE * ARENA_SIZE + ARENA_SIZE);
        int half = 0;
        bool odd = false;

        for (int i = 0; i < ARENA_SIZE; i++)
        {
            if (i > 0) sb.Append('\n');
            for (int j = 0; j < ARENA_SIZE; j++)
            {
                if (odd)
                    sb.Append(PREFABS[Mathf.Min(half >> 4 & 0xF, PREFABS.Length - 1)]);
                else
                    sb.Append(PREFABS[Mathf.Min((half = r.Byte()) & 0xF, PREFABS.Length - 1)]);

                odd = !odd;
            }
        }
        return sb.ToString();
    }

    #endregion
    #region harmony

    [DynamicPatch(typeof(EndlessGrid), nameof(EndlessGrid.OnTriggerEnter))]
    [Prefix]
    static bool Enter() => LobbyController.Offline || LobbyController.IsOwner;

    [DynamicPatch(typeof(EndlessGrid), nameof(EndlessGrid.LoadPattern))]
    [Prefix]
    static void Load(ref ArenaPattern pattern)
    {
        if (LobbyController.Offline) return;
        if (LobbyController.IsOwner)
        {
            SyncPattern(pattern);

            // respawn the host, the wave has ended after all
            if (nm.dead) Movement.CyberRespawn();
        }
        else pattern = CurrentPattern ?? pattern;
    }

    [DynamicPatch(typeof(EndlessGrid), nameof(EndlessGrid.Update))]
    [Prefix]
    static void Sync(EndlessGrid __instance)
    {
        if (LobbyController.Offline || LobbyController.IsOwner || __instance.anw == null) return;

        // display the synced state and prevent the launch of a new wave, it's the host's task
        __instance.currentWave = CurrentWave;
        __instance.tempEnemyAmount = TempEnemies;
        __instance.enemyAmount = Mathf.Max(TotalEnemies, DeadEnemies + 1);
        __instance.anw.deadEnemies = DeadEnemies;
    }

    [DynamicPatch(typeof(EndlessGrid), nameof(EndlessGrid.GetEnemies))]
    [Prefix]
    static bool Spawn(EndlessGrid __instance)
    {
        if (LobbyController.Offline || LobbyController.IsOwner) return true;

        // clients need the navigation for the enemies they own, but the enemies themselves come from the host
        __instance.nms.BuildNavMesh();
        __instance.nvmhlpr.GenerateLinks(__instance.cubes);
        return false;
    }

    [DynamicPatch(typeof(EndlessGrid), nameof(EndlessGrid.SpawnOnGrid))]
    [Prefix]
    static bool Spawn(bool enemy) => LobbyController.Offline || LobbyController.IsOwner || !enemy;

    [DynamicPatch(typeof(EndlessGrid), nameof(EndlessGrid.SpawnOnGrid))]
    [Postfix]
    static void Spawned(GameObject __result, bool enemy, bool radiant)
    {
        if (LobbyController.Online && LobbyController.IsOwner && enemy && radiant && __result != null) spawned.Add(__result.GetComponentInChildren<EnemyIdentifier>(true));
    }

    [DynamicPatch(typeof(FinalCyberRank), nameof(FinalCyberRank.GameOver))]
    [Prefix]
    static bool Over() => LobbyController.Offline || PlayersAlive() == 0;

    [StaticPatch(typeof(LeaderboardController), nameof(LeaderboardController.CanSubmitScores), HarmonyLib.MethodType.Getter)]
    [Postfix]
    static void Scores(ref bool __result) => __result &= !Networking.WasMultiplayerUsed;

    #endregion
}

namespace Jaket.Net.Backends;

using BepInEx;
using BepInEx.Configuration;
using UnityEngine;

/// <summary> Class responsible for selecting the multiplayer backend at startup. </summary>
public static class NetBackends
{
    /// <summary> Steam backend, default whenever Steam is running. </summary>
    public static INetBackend Steam { get; private set; }
    /// <summary> Epic Online Services backend, fallback that requires no Steam. </summary>
    public static INetBackend Epic { get; private set; }
    /// <summary> Backend selected at startup and used for everything. </summary>
    public static INetBackend Current { get; private set; }

    /// <summary> Config file storing the backend preference and the EOS credentials. </summary>
    public static ConfigFile Config { get; private set; }

    /// <summary> Selects a backend and initializes it. </summary>
    public static void Load()
    {
        Config = new(System.IO.Path.Combine(Paths.ConfigPath, "Jaket.Backends.cfg"), true);
        var preference = Config.Bind("General", "Backend", "auto", "Multiplayer backend to use: auto, steam or epic. Auto selects Steam whenever it is running.");

        Steam = new SteamBackend();
        Epic = new EpicBackend();

        Current = preference.Value switch
        {
            "steam" => Steam,
            "epic"  => Epic,
            _       => Steamworks.SteamClient.IsValid ? Steam : Epic
        };
        Current.Load();
        Jaket.Tools.Tools.AccId = Current.SelfId;

        Create<Ticker>("Backend");
        Log.Info($"[BACK] Selected the {Current.Name} backend");
    }

    /// <summary> Component that pumps the backend callbacks every frame. </summary>
    public class Ticker : MonoBehaviour
    {
        private void Update() => Current?.Tick();
    }
}

namespace Jaket.Net.Types;

using UnityEngine;

using Jaket.Harmony;

/// <summary>
/// Per-enemy table of AI methods whose products are already synced by a vendor and thus must be suppressed on
/// remote machines: full animator playback fires animation events there, which would double-spawn the products.
/// Purely local effects, e.g. explosions and shockwaves, are deliberately not listed, as each machine has to
/// replay them to damage its own player.
/// </summary>
public static class Roster
{
    /// <summary> Whether the agent the component belongs to is simulated by the local machine. </summary>
    static bool Simulated(Component comp)
    {
        var agent = comp.GetComponentInParent<Entity.Agent>();
        return agent == null || agent.name[0] == 'L';
    }

    // products of these methods are synced by the projectiles vendor
    [DynamicPatch(typeof(ZombieProjectiles), nameof(ZombieProjectiles.ThrowProjectile))] // stray, soldier
    [DynamicPatch(typeof(ZombieProjectiles), nameof(ZombieProjectiles.ShootProjectile))] // soldier
    [DynamicPatch(typeof(StatueBoss),        nameof(StatueBoss.OrbSpawn))]               // cerberus
    [DynamicPatch(typeof(Drone),             nameof(Drone.Shoot))]                       // drone, virtue
    [DynamicPatch(typeof(Mindflayer),        nameof(Mindflayer.ShootProjectiles))]       // mindflayer
    [DynamicPatch(typeof(Mannequin),         nameof(Mannequin.ShootProjectile))]         // mannequin
    [DynamicPatch(typeof(MirrorReaper),      nameof(MirrorReaper.SpawnProjectiles))]     // reaper
    // products of these methods are synced by the hitscans vendor
    [DynamicPatch(typeof(Turret),            nameof(Turret.Shoot))]                      // sentry
    // products of these methods are synced by the insignias vendor
    [DynamicPatch(typeof(Drone),             nameof(Drone.SpawnDroneInsignia))]          // virtue
    [Prefix]
    static bool Peace(MonoBehaviour __instance) => Simulated(__instance);
}

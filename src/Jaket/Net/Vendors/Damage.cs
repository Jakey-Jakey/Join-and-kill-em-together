namespace Jaket.Net.Vendors;

using System.Collections.Generic;
using UnityEngine;

using Jaket.Assets;
using Jaket.Content;
using Jaket.Harmony;
using Jaket.IO;
using NetEnemy = Jaket.Net.Types.Enemy;

using static Entities;

/// <summary> Vendor responsible for explosions and damage. </summary>
public class Damage : Vendor
{
    const int EFFECT_SIZE = 51;
    const int BEAM_SIZE = 34;
    const int HAZARD_SIZE = 29;
    const float BEAM_RATE = .08f;
    const float BEAM_HURT_COOLDOWN = .2f;
    const float HAZARD_RATE = .1f;
    const float HAZARD_HURT_COOLDOWN = .35f;

    static readonly Dictionary<int, float> beams = new();
    static readonly Dictionary<int, float> hazards = new();
    static float lastBeamHurt;
    static float lastHazardHurt;

    class NetworkEffect : MonoBehaviour { }
    class SyncedEffect : MonoBehaviour { }

    /// <summary> List of internal names of all melee damage types. </summary>
    public static readonly string[] Melee = { "coin", "punch", "heavypunch", "hook", "ground slam", "drill", "drillpunch", "hammer", "chainsawzone", "shotgunzone" };

    public void Load()
    {
        EntityType counter = EntityType.Shockwave;
        GameAssets.Explosions.Each(w =>
        {
            byte index = (byte)counter++;
            GameAssets.Prefab(w, p => Vendor.Prefabs[index] = p);
        });
    }

    public EntityType Type(GameObject obj)
    {
        Source(obj, out var type);
        return type;
    }

    public GameObject Make(EntityType type, Vector3 position = default, Transform parent = null)
    {
        if (!type.IsExplosion()) return null;

        var obj = Inst(Vendor.Prefabs[(byte)type], position);
        if (parent) obj.transform.SetParent(parent, true);
        return obj;
    }

    public GameObject Make(Reader r)
    {
        var type = r.EntityType();
        var position = r.Vector();
        var rotation = r.Vector();

        r.Bools(out var enemy, out var harmless, out var friendlyFire, out var boosted, out var ultrabooster, out var unblockable, out var electric, out var noDamageToEnemy);

        int damage = r.Int();
        float speed = r.Float();
        float maxSize = r.Float();
        float force = r.Float();
        float enemyDamageMultiplier = r.Float();
        int playerDamageOverride = r.Int();
        var enemyType = (EnemyType)r.Byte();

        var obj = Make(type, position);
        if (!obj) return null;

        obj.AddComponent<NetworkEffect>();
        obj.name = "network effect";
        obj.transform.eulerAngles = rotation;

        obj.GetComponentsInChildren<Explosion>(true).Each(e =>
        {
            e.enemy = enemy;
            e.harmless = harmless;
            e.friendlyFire = friendlyFire;
            e.boosted = boosted;
            e.ultrabooster = ultrabooster;
            e.unblockable = unblockable;
            e.electric = electric;
            e.damage = damage;
            e.speed = speed;
            e.maxSize = maxSize;
            e.pushForceMultiplier = force;
            e.enemyDamageMultiplier = enemyDamageMultiplier;
            e.playerDamageOverride = playerDamageOverride;
            e.canHit = AffectedSubjects.PlayerOnly;
            e.sourceWeapon = null;
        });

        obj.GetComponentsInChildren<PhysicalShockwave>(true).Each(s =>
        {
            s.enemy = enemy;
            s.damage = damage;
            s.speed = speed;
            s.maxSize = maxSize;
            s.force = force;
            s.enemyType = enemyType;
            s.hasHurtPlayer = false;
            s.noDamageToEnemy = true;
            s.ignorePlayerDash = noDamageToEnemy;
        });

        return obj;
    }

    public void Sync(GameObject obj, params bool[] args)
    {
        if (LobbyController.Offline || obj.GetComponentInParent<NetworkEffect>()) return;

        var source = Source(obj, out var type);
        if (!source || type == EntityType.None || source.GetComponent<SyncedEffect>()) return;

        if (obj.TryGetComponent(out Explosion e))
        {
            if (!e.enemy) return;
            source.gameObject.AddComponent<SyncedEffect>();

            Send(type, source.position, source.eulerAngles,
                e.enemy, e.harmless, e.friendlyFire, e.boosted, e.ultrabooster, e.unblockable, e.electric, false,
                e.damage, e.speed, e.maxSize, e.pushForceMultiplier, e.enemyDamageMultiplier, e.playerDamageOverride, default);
        }
        else if (obj.TryGetComponent(out PhysicalShockwave s))
        {
            if (!s.enemy) return;
            source.gameObject.AddComponent<SyncedEffect>();

            Send(type, source.position, source.eulerAngles,
                s.enemy, false, false, false, false, false, false, s.ignorePlayerDash,
                s.damage, s.speed, s.maxSize, s.force, 1f, 0, s.enemyType);
        }
    }

    public void Beam(Reader r)
    {
        var start = r.Vector();
        var end = r.Vector();
        float damage = r.Float();
        float width = r.Float();
        r.Bools(out var ignoreInvincibility, out _, out _, out _, out _, out _, out _, out _);
        r.Byte();

        Entities.Hitscans.Make(EntityType.BeamMalicious, start, end, false, byte.MaxValue);

        var player = NewMovement.Instance;
        if (!player || player.dead || Time.time - lastBeamHurt < BEAM_HURT_COOLDOWN) return;

        var point = player.transform.position + Vector3.up;
        float distance = DistanceToSegment(point, start, end);
        if (distance > Mathf.Max(width, .4f)) return;

        lastBeamHurt = Time.time;
        player.GetHurt(Mathf.RoundToInt(damage), false, 1f, false, false, 0f, ignoreInvincibility);
    }

    public void Hazard(Reader r)
    {
        var position = r.Vector();
        float radius = r.Float();
        int damage = r.Int();
        float force = r.Float();
        float cooldown = r.Float();
        r.Bools(out var antiHp, out var ignoreInvincibility, out _, out _, out _, out _, out _, out _);

        var player = NewMovement.Instance;
        if (!player || player.dead || Time.time - lastHazardHurt < cooldown) return;
        if (Vector3.Distance(player.transform.position + Vector3.up, position) > radius) return;

        lastHazardHurt = Time.time;
        player.GetHurt(damage, true, 1f, false, false, cooldown, ignoreInvincibility);
        if (force > 0f) player.LaunchFromPoint(position, force, radius);
        if (antiHp) player.ForceAntiHP(99f, false, false, true, false);
    }

    static Transform Source(GameObject obj, out EntityType type)
    {
        for (var t = obj?.transform; t; t = t.parent)
        {
            string name = t.name.Replace("(Clone)", "");
            type = Vendor.Find
            (
                EntityType.Shockwave,
                EntityType.HammerParticleHeavy,
                p => p && p.name == name
            );
            if (type != EntityType.None) return t;
        }

        type = EntityType.None;
        return null;
    }

    static void Send(EntityType type, Vector3 position, Vector3 rotation, bool enemy, bool harmless, bool friendlyFire, bool boosted, bool ultrabooster, bool unblockable, bool electric, bool noDamageToEnemy, int damage, float speed, float maxSize, float force, float enemyDamageMultiplier, int playerDamageOverride, EnemyType enemyType)
    {
        Networking.Send(PacketType.Effect, EFFECT_SIZE, w =>
        {
            w.Enum(type);
            w.Vector(position);
            w.Vector(rotation);
            w.Bools(enemy, harmless, friendlyFire, boosted, ultrabooster, unblockable, electric, noDamageToEnemy);
            w.Int(damage);
            w.Float(speed);
            w.Float(maxSize);
            w.Float(force);
            w.Float(enemyDamageMultiplier);
            w.Int(playerDamageOverride);
            w.Byte((byte)enemyType);
        });
    }

    static void SendBeam(Vector3 start, Vector3 end, float damage, float width, bool ignoreInvincibility, EnemyType safeType)
    {
        Networking.Send(PacketType.Beam, BEAM_SIZE, w =>
        {
            w.Vector(start);
            w.Vector(end);
            w.Float(damage);
            w.Float(width);
            w.Bools(ignoreInvincibility);
            w.Byte((byte)safeType);
        });
    }

    static void SendHazard(Vector3 position, float radius, int damage, float force, float cooldown, bool antiHp, bool ignoreInvincibility)
    {
        Networking.Send(PacketType.Hazard, HAZARD_SIZE, w =>
        {
            w.Vector(position);
            w.Float(radius);
            w.Int(damage);
            w.Float(force);
            w.Float(cooldown);
            w.Bools(antiHp, ignoreInvincibility);
        });
    }

    static float DistanceToSegment(Vector3 point, Vector3 start, Vector3 end)
    {
        var line = end - start;
        float length = Vector3.Dot(line, line);
        if (length <= .001f) return Vector3.Distance(point, start);

        float t = Mathf.Clamp01(Vector3.Dot(point - start, line) / length);
        return Vector3.Distance(point, start + line * t);
    }

    static Vector3 BeamPoint(LineRenderer line, int index)
    {
        var point = line.GetPosition(index);
        return line.useWorldSpace ? point : line.transform.TransformPoint(point);
    }

    #region dealing

    /// <summary> Distributes the damage over the network. </summary>
    public void Deal(uint tid, float damage) => Networking.Send(PacketType.Damage, 8, w =>
    {
        w.Id(tid);
        w.Float(damage);
    });

    /// <summary> Delivers remote damage from the network. </summary>
    public void Deal(EnemyIdentifier eid, float damage)
    {
        eid.hitter = "network";
        eid.DeliverDamage(eid.gameObject, default, default, damage, false);
    }

    #endregion
    #region harmony

    [DynamicPatch(typeof(EnemyIdentifier), nameof(EnemyIdentifier.DeliverDamage))]
    [Prefix]
    static void MeleeDmg(EnemyIdentifier __instance, float multiplier)
    {
        if (__instance.dead || multiplier == 0f) return;

        if (Melee.Has(__instance.hitter) && __instance.TryGetComponent(out Entity.Agent a)) Entities.Damage.Deal(a.Patron.Id, multiplier);

        if (Version.DEBUG) Log.Debug($"[ENTS] Damage of {multiplier} units was dealt by {__instance.hitter}");
    }

    [DynamicPatch(typeof(Explosion), nameof(Explosion.Start))]
    [Prefix]
    static void ExplosionStart(Explosion __instance) => Entities.Damage.Sync(__instance.gameObject);

    [DynamicPatch(typeof(PhysicalShockwave), nameof(PhysicalShockwave.Start))]
    [Prefix]
    static void ShockwaveStart(PhysicalShockwave __instance) => Entities.Damage.Sync(__instance.gameObject);

    [DynamicPatch(typeof(EnemyIdentifier), nameof(EnemyIdentifier.Zap))]
    [Postfix]
    static void ZapHazard(Vector3 position, EnemyIdentifier sourceEid, Water sourceWater)
    {
        if (LobbyController.Offline || !AuthoredByOwnedEnemy(sourceEid)) return;

        int id = sourceWater ? sourceWater.GetInstanceID() : sourceEid.GetInstanceID();
        if (hazards.TryGetValue(id, out float last) && Time.time - last < HAZARD_RATE) return;
        hazards[id] = Time.time;

        if (sourceWater?.waterColliders != null)
            sourceWater.waterColliders.Each(c => SendZapCollider(c));
        else
            SendHazard(position, 3f, 50, 0f, 1f, false, false);
    }

    [DynamicPatch(typeof(SwingCheck2), nameof(SwingCheck2.Update))]
    [Postfix]
    static void SwingHazard(SwingCheck2 __instance)
    {
        if (LobbyController.Offline || !__instance || !__instance.damaging || !AuthoredByOwnedEnemy(__instance.eid)) return;

        int id = __instance.GetInstanceID();
        if (hazards.TryGetValue(id, out float last) && Time.time - last < HAZARD_RATE) return;
        hazards[id] = Time.time;

        float modifier = __instance.eid ? __instance.eid.totalDamageModifier : 1f;
        int damage = Mathf.RoundToInt(__instance.damage * modifier);

        SendSwingCollider(__instance.col, damage, __instance.knockBackForce);
        __instance.additionalColliders?.Each(c => SendSwingCollider(c, damage, __instance.knockBackForce));
    }

    [DynamicPatch(typeof(ThrownSword), nameof(ThrownSword.Update))]
    [Postfix]
    static void ThrownSwordHazard(ThrownSword __instance)
    {
        if (LobbyController.Offline || !__instance || !__instance.active || __instance.friendly || !AuthoredByOwnedEnemy(__instance.thrownBy)) return;

        int id = __instance.GetInstanceID();
        if (hazards.TryGetValue(id, out float last) && Time.time - last < HAZARD_RATE) return;
        hazards[id] = Time.time;

        SendSwingCollider(__instance.col, 30, 0f);
    }

    [DynamicPatch(typeof(MassSpear), nameof(MassSpear.Update))]
    [Postfix]
    static void MassSpearHazard(MassSpear __instance)
    {
        if (LobbyController.Offline || !__instance || __instance.deflected || __instance.beenStopped || !AuthoredByOwnedEnemy(__instance.mass?.eid)) return;

        int id = __instance.GetInstanceID();
        if (hazards.TryGetValue(id, out float last) && Time.time - last < HAZARD_RATE) return;
        hazards[id] = Time.time;

        int damage = Mathf.RoundToInt(25f * __instance.damageMultiplier);
        SendSwingCollider(__instance.GetComponent<Collider>(), damage, 0f);
    }

    [DynamicPatch(typeof(ContinuousBeam), nameof(ContinuousBeam.FixedUpdate))]
    [Postfix]
    static void BeamUpdate(ContinuousBeam __instance, LineRenderer ___lr)
    {
        if (LobbyController.Offline || !__instance || !___lr || !__instance.enemy || !__instance.canHitPlayer || __instance.off) return;

        int id = __instance.GetInstanceID();
        if (beams.TryGetValue(id, out float last) && Time.time - last < BEAM_RATE) return;
        beams[id] = Time.time;

        SendBeam(BeamPoint(___lr, 0), BeamPoint(___lr, 1), __instance.damage, __instance.beamWidth, __instance.ignoreInvincibility, __instance.safeEnemyType);
    }

    [DynamicPatch(typeof(BeamgunBeam), nameof(BeamgunBeam.Update))]
    [Postfix]
    static void BeamgunUpdate(BeamgunBeam __instance)
    {
        if (LobbyController.Offline || !__instance || !__instance.active || !__instance.canHitPlayer || !__instance.line || !AuthoredByOwnedEnemy(__instance)) return;

        int id = __instance.GetInstanceID();
        if (beams.TryGetValue(id, out float last) && Time.time - last < BEAM_RATE) return;
        beams[id] = Time.time;

        SendBeam(BeamPoint(__instance.line, 0), BeamPoint(__instance.line, 1), 10f, __instance.beamWidth, false, default);
    }

    [DynamicPatch(typeof(MinosPrime), nameof(MinosPrime.AirRaycastAttack))]
    [Postfix]
    static void MinosRaycast(MinosPrime __instance, Vector3 direction)
    {
        if (LobbyController.Offline || !__instance || !__instance.aimingBone || !AuthoredByOwnedEnemy(__instance.eid)) return;

        SendRaycastHazard(__instance.aimingBone.position, direction, __instance.eid);
    }

    [DynamicPatch(typeof(SisyphusPrime), nameof(SisyphusPrime.DropAttackActivate))]
    [Postfix]
    static void SisyphusRaycast(SisyphusPrime __instance)
    {
        if (LobbyController.Offline || !__instance || !__instance.aimingBone || !AuthoredByOwnedEnemy(__instance.eid)) return;

        SendRaycastHazard(__instance.aimingBone.position, Vector3.down, __instance.eid);
    }

    [DynamicPatch(typeof(VirtueInsignia), nameof(VirtueInsignia.Explode))]
    [Postfix]
    static void VirtueBeam(VirtueInsignia __instance)
    {
        if (LobbyController.Offline || !__instance || !(__instance.parentEnemy || __instance.parentDrone || __instance.hadParent)) return;

        var effect = __instance.explosion ? __instance.explosion.transform : __instance.transform;
        var length = Mathf.Max(__instance.explosionLength, 1f);
        var width = Mathf.Max(__instance.explosionWidth, .5f);
        var direction = effect.up;
        var center = effect.position;

        SendBeam(center - direction * length * .5f, center + direction * length * .5f, __instance.damage, width, false, default);
    }

    [DynamicPatch(typeof(BlackHoleProjectile), nameof(BlackHoleProjectile.Update))]
    [Postfix]
    static void BlackHoleHazard(BlackHoleProjectile __instance)
    {
        if (LobbyController.Offline || !__instance || !__instance.enemy || !__instance.activated || __instance.collapsing) return;

        int id = __instance.GetInstanceID();
        if (hazards.TryGetValue(id, out float last) && Time.time - last < HAZARD_RATE) return;
        hazards[id] = Time.time;

        float radius = 1f;
        if (__instance.TryGetComponent(out Collider collider) && collider.enabled)
            radius = Mathf.Max(radius, collider.bounds.extents.magnitude);

        SendHazard(__instance.transform.position, radius, 10, 0f, HAZARD_HURT_COOLDOWN, true, false);
    }

    [DynamicPatch(typeof(BlackHoleProjectile), nameof(BlackHoleProjectile.Explode))]
    [Prefix]
    static void BlackHoleExplode(BlackHoleProjectile __instance)
    {
        if (LobbyController.Offline || !__instance || !__instance.enemy) return;

        Send(EntityType.Blastwave, __instance.transform.position, __instance.transform.eulerAngles,
            true, false, false, false, false, true, false, false,
            10, 5f, 5f, 1f, 1f, 10, __instance.safeType);
    }

    static bool HazardBounds(Collider collider, out Vector3 center, out float radius)
    {
        center = default;
        radius = 0f;

        if (!collider || !collider.enabled) return false;

        var bounds = collider.bounds;
        center = bounds.center;
        radius = Mathf.Max(bounds.extents.magnitude, .5f);
        return true;
    }

    static bool AuthoredByOwnedEnemy(Component component)
    {
        if (!component) return false;

        var eid = component.GetComponentInParent<EnemyIdentifier>();
        if (!eid) eid = component.GetComponentInParent<EnemyIdentifierIdentifier>()?.eid;
        return AuthoredByOwnedEnemy(eid);
    }

    static bool AuthoredByOwnedEnemy(EnemyIdentifier eid) => eid && eid.TryGetEntity(out NetEnemy enemy) && enemy.IsOwner;

    static void SendRaycastHazard(Vector3 start, Vector3 direction, EnemyIdentifier eid)
    {
        if (!eid || direction.sqrMagnitude <= .001f) return;

        direction = direction.normalized;
        var end = Physics.Raycast(start, direction, out var hit, 250f, EnvMask) ? hit.point : start + direction * 250f;
        SendBeam(start, end, Mathf.RoundToInt(30f * eid.totalDamageModifier), 5f, false, eid.enemyType);
    }

    static void SendZapCollider(Collider collider)
    {
        if (HazardBounds(collider, out var center, out var radius))
            SendHazard(center, radius, 50, 0f, 1f, false, false);
    }

    static void SendSwingCollider(Collider collider, int damage, float force)
    {
        if (HazardBounds(collider, out var center, out var radius))
            SendHazard(center, radius, damage, force, HAZARD_HURT_COOLDOWN, false, false);
    }

    #endregion
}

namespace Jaket.Net.Types;

using ULTRAKILL.Enemy;
using UnityEngine;

using Jaket.Content;
using Jaket.Harmony;
using Jaket.IO;

/// <summary> Tangible entity of the shell or any projectile type. </summary>
public class Shell : Projectile
{
    Agent agent;
    global::Projectile proj;
    Float damage, speed, enemyDamageMultiplier;
    byte flags;
    EnemyType safeEnemyType;

    public Shell(uint id, EntityType type) : base(id, type, true, true, false) { }

    #region snapshot

    public override int BufferSize => 30;

    public override void Write(Writer w)
    {
        bool owned = IsOwner;
        WriteOwner(ref w);

        if (owned)
        {
            w.Vector(agent.Position);
            CaptureProjectile();
        }
        else
            w.Floats(x, y, z);

        w.Float(damage.Next);
        w.Float(speed.Next);
        w.Float(enemyDamageMultiplier.Next);
        w.Byte(flags);
        w.Byte((byte)safeEnemyType);
    }

    public override void Read(Reader r)
    {
        if (ReadOwner(ref r)) return;

        r.Floats(ref x, ref y, ref z);
        damage.Set(r.Float());
        speed.Set(r.Float());
        enemyDamageMultiplier.Set(r.Float());
        flags = r.Byte();
        safeEnemyType = (EnemyType)r.Byte();

        ApplyProjectile();
    }

    #endregion
    #region logic

    public override void Paint(Renderer renderer)
    {
        if (Type != EntityType.Shell) return;

        base.Paint(renderer);
        if (renderer is MeshRenderer m) m.material.mainTexture = null;
        if (renderer is TrailRenderer t) t.startColor = t.startColor with { a = 1f };
    }

    public override void Assign(Agent agent)
    {
        base.Assign(this.agent = agent);

        agent.Get(out proj);
        agent.Rem<FloatingPointErrorPreventer>();
        agent.Rem<DestroyOnCheckpointRestart>();
        agent.Rem<RemoveOnTime>();
        agent.Run(MasterKill, 15f);

        if (IsOwner) CaptureProjectile();
        else ApplyProjectile();
    }

    public override void Update(float delta)
    {
        proj.enabled = IsOwner || !proj.friendly && !proj.playerBullet;
        base.Update(delta);
    }

    protected override bool LocalCollision(Collider other) => !proj.friendly && !proj.playerBullet && other.GetComponentInParent<NewMovement>() == NewMovement.Instance;

    private void CaptureProjectile()
    {
        if (!proj) return;

        damage.Set(proj.damage);
        speed.Set(proj.speed);
        enemyDamageMultiplier.Set(proj.enemyDamageMultiplier);
        safeEnemyType = proj.safeEnemyType;
        flags = (byte)(
            (proj.friendly      ? 1 << 0 : 0) |
            (proj.playerBullet  ? 1 << 1 : 0) |
            (proj.explosive     ? 1 << 2 : 0) |
            (proj.bigExplosion  ? 1 << 3 : 0) |
            (proj.undeflectable ? 1 << 4 : 0) |
            (proj.unparryable   ? 1 << 5 : 0)
        );
    }

    private void ApplyProjectile()
    {
        if (!proj) return;

        proj.damage = damage.Next;
        proj.speed = speed.Next;
        proj.enemyDamageMultiplier = enemyDamageMultiplier.Next;
        proj.safeEnemyType = safeEnemyType;
        proj.friendly = (flags & 1 << 0) != 0;
        proj.playerBullet = (flags & 1 << 1) != 0;
        proj.explosive = (flags & 1 << 2) != 0;
        proj.bigExplosion = (flags & 1 << 3) != 0;
        proj.undeflectable = (flags & 1 << 4) != 0;
        proj.unparryable = (flags & 1 << 5) != 0;
    }

    public override void Killed(Reader r, int left)
    {
        base.Killed(r, left); r.Skip(12);

        if (left >= 13)
        {
            if (r.Bool())
            {
                proj.boosted = Type == EntityType.Shell;
                proj.explosionEffect = Entities.Vendor.Prefabs[(byte)EntityType.ShotgunExplosion];
            }
            proj.CreateExplosionEffect();
        }
    }

    #endregion
    #region harmony

    [DynamicPatch(typeof(global::Projectile), nameof(global::Projectile.Start))]
    [Prefix]
    static void Start(global::Projectile __instance)
    {
        if (__instance) Entities.Projectiles.Sync(__instance.gameObject);
    }

    [DynamicPatch(typeof(global::Projectile), nameof(global::Projectile.OnDestroy))]
    [Prefix]
    static void Break(global::Projectile __instance) => Kill<Shell>(__instance, e =>
    {
        e.Kill();
    }, true);

    [DynamicPatch(typeof(global::Projectile), nameof(global::Projectile.CreateExplosionEffect))]
    [DynamicPatch(typeof(global::Projectile), nameof(global::Projectile.Explode))]
    [Prefix]
    static bool Death(global::Projectile __instance) => Kill<Shell>(__instance, e =>
    {
        e.Kill(13, w => { w.Vector(e.agent.Position); w.Bool(__instance.parried); });
    }, true);

    [DynamicPatch(typeof(Punch), nameof(Punch.ParryProjectile))]
    [Prefix]
    static void Parry(global::Projectile proj) => Kill<Shell>(proj, e =>
    {
        e.TakeOwnage();

        // prevent speed from skyrocketing
        if (proj.parried) proj.speed /= 2f;
    });

    [DynamicPatch(typeof(ProjectileSpread), nameof(ProjectileSpread.Start))]
    [Postfix]
    static void Spread(ProjectileSpread __instance)
    {
        __instance.projectile.name += "(Clone)";
        Entities.Projectiles.Sync(__instance.projectile);
    }

    [DynamicPatch(typeof(global::Projectile), nameof(global::Projectile.Collided))]
    [Prefix]
    static bool Damage(global::Projectile __instance, Collider other) => Deal<Shell>(__instance, (eid, tid, ally, e) =>
    {
        if (__instance.friendly ? ally : EnemyIdentifier.CheckHurtException(__instance.safeEnemyType, eid.enemyType, (TargetHandle)null)) return false;

        Entities.Damage.Deal(tid, __instance.damage * __instance.enemyDamageMultiplier / (__instance.friendly || __instance.playerBullet ? 4f : 10f));
        return true;
    }, other: other);

    #endregion
}

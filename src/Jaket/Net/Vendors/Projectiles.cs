namespace Jaket.Net.Vendors;

using UnityEngine;

using Jaket.Assets;
using Jaket.Content;
using Jaket.Net.Types;

using static Entities;

/// <summary> Vendor responsible for projectiles. </summary>
public class Projectiles : Vendor
{
    public void Load()
    {
        EntityType counter = EntityType.Shell;
        GameAssets.Projectiles.Each(w =>
        {
            byte index = (byte)counter++;
            GameAssets.Prefab(w, p => Vendor.Prefabs[index] = p);
        });

        for (EntityType i = EntityType.Shell;          i <= EntityType.Shell;          i++) Vendor.Suppliers[(byte)i] = (id, type) => new Shell      (id, type);
        for (EntityType i = EntityType.Core;           i <= EntityType.Core;           i++) Vendor.Suppliers[(byte)i] = (id, type) => new Core       (id, type);
        for (EntityType i = EntityType.NailCommon;     i <= EntityType.NailHeated;     i++) Vendor.Suppliers[(byte)i] = (id, type) => new Nail       (id, type);
        for (EntityType i = EntityType.SawbladeCommon; i <= EntityType.SawbladeHeated; i++) Vendor.Suppliers[(byte)i] = (id, type) => new Sawblade   (id, type);
        for (EntityType i = EntityType.Magnet;         i <= EntityType.Magnet;         i++) Vendor.Suppliers[(byte)i] = (id, type) => new Magnet     (id, type);
        for (EntityType i = EntityType.Screwdriver;    i <= EntityType.Screwdriver;    i++) Vendor.Suppliers[(byte)i] = (id, type) => new Screwdriver(id, type);
        for (EntityType i = EntityType.Rocket;         i <= EntityType.Rocket;         i++) Vendor.Suppliers[(byte)i] = (id, type) => new Rocket     (id, type);
        for (EntityType i = EntityType.Cannonball;     i <= EntityType.Cannonball;     i++) Vendor.Suppliers[(byte)i] = (id, type) => new Cannon     (id, type);
        for (EntityType i = EntityType.ProjectileHell; i <= EntityType.ProjectileExpl; i++) Vendor.Suppliers[(byte)i] = (id, type) => new Shell      (id, type);

        Events.OnTeamChange += () => Networking.Entities.Alive<Projectile>(p => p.UpdateIgnore());
    }

    public EntityType Type(GameObject obj)
    {
        var type = Vendor.Find
        (
            EntityType.Shell,
            EntityType.ProjectileExpl,
            p => p.name.Length == obj?.name.Length - 7 && (obj?.name.Contains(p.name) ?? false)
        );
        if (type != EntityType.None || !obj) return type;

        if (obj.TryGetComponent(out Grenade grenade))
            return grenade.rocket ? EntityType.Rocket : EntityType.Core;

        if (obj.TryGetComponent(out global::Nail nail))
        {
            if (nail.sawblade)
                return nail.heated ? EntityType.SawbladeHeated : nail.fodderDamageBoost ? EntityType.SawbladeFodder : EntityType.SawbladeCommon;

            return nail.heated ? EntityType.NailHeated : nail.fodderDamageBoost ? EntityType.NailFodder : EntityType.NailCommon;
        }

        if (obj.TryGetComponent(out Cannonball ball) && ball.physicsCannonball)
            return EntityType.Cannonball;

        if (obj.TryGetComponent(out global::Projectile projectile) && !projectile.decorative)
            return projectile.explosive ? EntityType.ProjectileExpl : EntityType.ProjectileHell;

        return EntityType.None;
    }

    public GameObject Make(EntityType type, Vector3 position = default, Transform parent = null)
    {
        if (!type.IsProjectile()) return null;

        var obj = Inst(Vendor.Prefabs[(byte)type], position);

        return obj;
    }

    public void Sync(GameObject obj, params bool[] args)
    {
        var type = Type(obj);
        if (type == EntityType.None || obj.GetComponent<Entity.Agent>()) return;

        var entity = Supply(type);

        entity.Owner = AccId;
        entity.Assign(obj.AddComponent<Entity.Agent>());
        entity.Push();
    }
}

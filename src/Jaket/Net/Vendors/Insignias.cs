namespace Jaket.Net.Vendors;

using UnityEngine;

using Jaket.Content;
using Jaket.Harmony;
using Jaket.IO;
using Jaket.Net.Types;

using static Entities;

/// <summary> Vendor responsible for telegraphed AoE markers, e.g. virtue insignias. </summary>
public class Insignias : Vendor
{
    /// <summary> Name given to remotely spawned markers so that they are not synced back. </summary>
    public const string FAKE = "fake!";

    /// <summary> Insignia prefab, extracted from the virtue prefab as the asset itself is not listed anywhere. </summary>
    static GameObject prefab;

    public void Load() { }

    public EntityType Type(GameObject obj) => EntityType.None;

    public GameObject Make(EntityType type, Vector3 position = default, Transform parent = null) => null;

    public void Make(Reader r)
    {
        prefab ??= Vendor.Prefabs[(byte)EntityType.Virtue]?.GetComponentInChildren<Drone>(true)?.projectile?.ToAsset();
        if (prefab == null) return;

        var obj = Inst(prefab, r.Vector());

        obj.name = FAKE;
        obj.transform.eulerAngles = r.Vector();
        obj.transform.localScale  = r.Vector();

        var vi = obj.GetComponent<VirtueInsignia>();

        vi.windUpSpeedMultiplier = r.Float();
        vi.explosionLength       = r.Float();
        vi.damage                = r.Byte();
        vi.charges               = r.Byte();

        byte flags = r.Byte();
        vi.predictive = (flags & 1) != 0;
        vi.noTracking = (flags & 2) != 0;

        uint target = r.Id();
        vi.target = target == AccId
            ? EnemyTarget.TrackPlayerIfAllowed()
            : Networking.Entities[target] is RemotePlayer p ? p.Target : null;

        // the insignia is unable to follow an unknown target, so it activates right where it spawned
        if (vi.target == null)
        {
            vi.noTracking = true;
            vi.predictive = false;
        }
    }

    public void Sync(GameObject obj, params bool[] args)
    {
        // markers that came from the network and local copies of self-replicating markers are spawned on
        // every machine on their own, broadcasting them would double the amount of insignias on remote screens
        if (obj.name.StartsWith(FAKE) || obj.name.EndsWith("(Clone)(Clone)") || !obj.TryGetComponent(out VirtueInsignia vi)) return;

        uint target = 0u;
        if (vi.target != null)
        {
            if (vi.target.isPlayer)
                target = AccId;
            else
            {
                var agent = vi.target.targetTransform?.GetComponentInParent<Entity.Agent>();
                if (agent && agent.Patron is RemotePlayer p) target = p.Id;
            }
        }

        Networking.Send(PacketType.Insignia, 51, w =>
        {
            w.Vector(obj.transform.position);
            w.Vector(obj.transform.eulerAngles);
            w.Vector(obj.transform.localScale);

            w.Float(vi.windUpSpeedMultiplier);
            w.Float(vi.explosionLength);

            w.Byte((byte)Mathf.Clamp(vi.damage, 0, byte.MaxValue));
            w.Byte((byte)Mathf.Clamp(vi.charges, 0, byte.MaxValue));
            w.Byte((byte)((vi.predictive ? 1 : 0) | (vi.noTracking ? 2 : 0)));

            w.Id(target);
        });
    }

    #region harmony

    [DynamicPatch(typeof(VirtueInsignia), nameof(VirtueInsignia.Start))]
    [Prefix]
    static void Start(VirtueInsignia __instance)
    {
        if (__instance) Entities.Insignias.Sync(__instance.gameObject);
    }

    #endregion
}

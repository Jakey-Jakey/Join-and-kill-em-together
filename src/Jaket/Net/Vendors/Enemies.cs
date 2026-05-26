namespace Jaket.Net.Vendors;

using Sandbox;
using System;
using UnityEngine;

using Jaket.Assets;
using Jaket.Content;
using Jaket.Net.Types;
using Jaket.World;

using static Entities;

/// <summary> Vendor responsible for enemies. </summary>
public class Enemies : Vendor
{
    public void Load()
    {
        EntityType counter = EntityType.Filth;
        GameAssets.Enemies.Each(w =>
        {
            byte index = (byte)counter++;
            GameAssets.Prefab(w, p => Vendor.Prefabs[index] = p);
        });

        Events.Post
        (
            () => Vendor.Prefabs[(byte)EntityType.Malicious],
            () => Vendor.Prefabs[(byte)EntityType.Malicious] = Vendor.Prefabs[(byte)EntityType.Malicious].transform.Find("Body").gameObject
        );

        for (EntityType i = EntityType.Filth; i <= EntityType.Sisyphus; i++)
            Vendor.Suppliers[(byte)i] = (id, type) => new GenericEnemy(id, type);

        Events.OnLoad += SyncScene;
        Events.OnLobbyEnter += SyncScene;
    }

    private void SyncScene()
    {
        if (LobbyController.Online)
        {
            ResFind<EnemySpawnableInstance>().Each(IsReal, Imdt);
            ResFind<SpiderLegLines        >().Each(IsReal, Imdt);
            ResFind<SpiderLegsController  >().Each(IsReal, Imdt);
            ResFind<EnemyIdentifier       >().Each(IsReal, e => Sync(e.gameObject, e.IsSandboxEnemy));
        }
    }

    public EntityType Type(GameObject obj)
    {
        if (obj?.TryGetComponent(out EnemyIdentifier enemyId) ?? false)
        {
            var type = Vendor.Find
            (
                EntityType.Filth,
                EntityType.Sisyphus,
                p => p && p.TryGetComponent(out EnemyIdentifier e)
                       && e.enemyType        == enemyId.enemyType
                       && e.overrideFullName == enemyId.overrideFullName
                       && e.weakPoint?.name  == enemyId.weakPoint?.name
            );
            if (type != EntityType.None) return type;

            if (Enum.TryParse<EntityType>(enemyId.enemyType.ToString(), ignoreCase: true, out type) && type.IsEnemy())
                return type;
        }
        else return EntityType.None;

        return EntityType.None;
    }

    public GameObject Make(EntityType type, Vector3 position = default, Transform parent = null) => Make(type, position, (string)null);

    public GameObject Make(EntityType type, Vector3 position, string identity)
    {
        if (!type.IsEnemy()) return null;

        var existing = FindSceneInstance(type, position, identity);
        if (existing) return existing;

        var prefab = Vendor.Prefabs[(byte)type];
        if (!prefab) return null;

        var obj = Inst(prefab, position);

        return obj;
    }

    private GameObject FindSceneInstance(EntityType type, Vector3 position, string identity)
    {
        GameObject best = null;
        float score = float.PositiveInfinity;

        ResFind<EnemyIdentifier>().Each(e =>
        {
            if (!IsReal(e) || e.dead || e.GetComponent<Entity.Agent>()) return;
            if (Type(e.gameObject) != type) return;

            if (!string.IsNullOrEmpty(identity) && SceneIdentity(e.transform) == identity)
            {
                best = e.gameObject;
                score = float.NegativeInfinity;
                return;
            }

            float next = (e.transform.position - position).sqrMagnitude;
            if (next >= score) return;

            score = next;
            best = e.gameObject;
        });

        return best;
    }

    private static string SceneIdentity(Transform transform)
    {
        if (!transform) return "";

        var stack = new Transform[64];
        int count = 0;

        for (var t = transform; t && count < stack.Length; t = t.parent)
            stack[count++] = t;

        string path = "";
        for (int i = count - 1; i >= 0; i--)
        {
            string next = path.Length == 0 ? stack[i].name : $"{path}/{stack[i].name}";
            if (next.Length > 240) break;
            path = next;
        }
        return path;
    }

    public void Sync(GameObject obj, params bool[] args)
    {
        var type = Type(obj);
        if (type == EntityType.None || obj.GetComponent<Entity.Agent>()) return;

        if (Gameflow.Mode.NoCommonEnemies()) Imdt(obj);
        else
        if (obj.activeSelf && obj.TryGetComponent(out EnemyIdentifier enemyId) && !enemyId.dead)
        {
            if (LobbyController.IsOwner || args[0])
            {
                var entity = Supply(type);

                entity.Owner = AccId;
                entity.Assign(obj.AddComponent<Entity.Agent>());
                entity.Push();
            }
            else obj.SetActive(false);
        }
    }
}

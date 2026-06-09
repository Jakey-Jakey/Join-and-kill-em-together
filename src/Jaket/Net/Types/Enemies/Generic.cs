namespace Jaket.Net.Types;

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

using Jaket.Content;
using Jaket.IO;

/// <summary> Tangible entity of any enemy type that has no specific implementation yet. </summary>
public class Generic : Enemy
{
    Agent agent;
    Float x, y, z, r;
    EnemyIdentifier enemyId;
    NavMeshAgent nma;
    Animator animator;
    Rigidbody rb;
    Behaviour[] brain;

    /// <summary> Whether the rigidbody is kinematic by its nature. </summary>
    bool kinematic;
    /// <summary> Hash of the animator state, synced so that animations are visible to everyone. </summary>
    int state, lastState;
    /// <summary> Whether the enemy is blessed by an idol. </summary>
    bool blessed, lastBlessed;

    public Generic(uint id, EntityType type) : base(id, type) { }

    #region snapshot

    public override int BufferSize => 30;

    public override void Write(Writer w)
    {
        WriteOwner(ref w);

        if (IsOwner)
        {
            w.Vector(agent.Position);
            w.Float(agent.Rotation.y);

            w.Int(animator ? animator.GetCurrentAnimatorStateInfo(0).shortNameHash : 0);
            w.Bool(enemyId.Blessed);
        }
        else
        {
            w.Floats(x, y, z);
            w.Float(r.Next);

            w.Int(state);
            w.Bool(blessed);
        }
    }

    public override void Read(Reader r)
    {
        if (ReadOwner(ref r)) return;

        r.Floats(ref x, ref y, ref z);
        this.r.Set(r.Float());

        state = r.Int();
        blessed = r.Bool();
    }

    #endregion
    #region logic

    public override void Create()
    {
        var obj = Entities.Enemies.Make(Type, new(x.Init, y.Init, z.Init));
        if (obj == null)
        {
            Hidden = true;
            Log.Warning($"[ENTS] Couldn't create an object for an entity of type {Type}");
        }
        else Assign(obj.AddComponent<Agent>());
    }

    public override void Assign(Agent agent)
    {
        base.Assign(this.agent = agent);

        agent.Get(out enemyId);
        agent.Get(out animator, true);
        agent.Get(out nma, true);
        agent.Get(out rb, true);

        // most of the behavior scripts extend the same class, the rest are exceptions
        List<Behaviour> bs = new();

        agent.Get(out EnemyScript[] scripts, true);
        if (scripts != null) bs.AddRange(scripts);

        agent.Get(out global::Idol idol, true);
        agent.Get(out MinotaurChase chase, true);
        agent.Get(out Deathcatcher catcher, true);

        if (idol) bs.Add(idol);
        if (chase) bs.Add(chase);
        if (catcher) bs.Add(catcher);

        brain = bs.ToArray();
        kinematic = rb && rb.isKinematic;
    }

    public override void Update(float delta)
    {
        if (Locked)
        {
            brain.Each(b => { if (b) b.enabled = false; });
            if (nma) nma.enabled = false;
            return;
        }

        brain.Each(b => { if (b) b.enabled = IsOwner; });

        if (IsOwner)
        {
            if (rb && rb.isKinematic && !kinematic) rb.isKinematic = false;
            if (nma && !nma.enabled)
            {
                nma.enabled = true;
                if (NavMesh.SamplePosition(agent.Position, out var hit, 4f, NavMesh.AllAreas)) nma.Warp(hit.position);
            }
            return;
        }

        agent.Position = new(x.GetAware(delta), y.GetAware(delta), z.GetAware(delta));
        agent.Rotation = new(agent.Rotation.x,  r.GetAngle(delta), agent.Rotation.z );

        if (nma) nma.enabled = false;
        if (rb && !rb.isKinematic) rb.isKinematic = true;

        if (animator && state != 0 && lastState != state) animator.CrossFadeInFixedTime(lastState = state, .2f, 0);

        if (lastBlessed != blessed)
        {
            if (lastBlessed = blessed)
                enemyId.Bless();
            else
                enemyId.Unbless();
        }
    }

    #endregion
}

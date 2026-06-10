namespace Jaket.Net.Types;

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

using Jaket.Content;
using Jaket.IO;

/// <summary>
/// Tangible entity of any enemy type, serves as the backbone for the entire roster.
/// Syncs ownership, position, y-rotation and the full state of the animator: current state hashes of all layers
/// plus all of the parameters. Both sides instantiate the same prefab, so index-based encoding is deterministic.
/// </summary>
public class Generic : Enemy
{
    protected static readonly int[] NoInts = new int[0];
    protected static readonly bool[] NoBools = new bool[0];
    protected static readonly float[] NoFloats = new float[0];

    Agent agent;
    Float x, y, z, r;
    EnemyIdentifier enemyId;
    NavMeshAgent nma;
    Animator animator;
    Rigidbody rb;
    Behaviour[] brain;

    /// <summary> Whether the rigidbody is kinematic by its nature. </summary>
    bool kinematic;
    /// <summary> Whether the enemy is blessed by an idol. </summary>
    bool blessed, lastBlessed;

    /// <summary> Identifiers of the animator parameters, in declaration order, triggers and curve-driven ones excluded. </summary>
    int[] boolIds = NoInts, floatIds = NoInts, intIds = NoInts;

    /// <summary> Received state hashes of the animator layers and the last applied ones. </summary>
    int[] states = NoInts, lastStates = NoInts;
    /// <summary> Received values of the animator parameters. </summary>
    bool[] bools = NoBools;
    /// <summary> Received values of the animator parameters. </summary>
    float[] floats = NoFloats;
    /// <summary> Received values of the animator parameters. </summary>
    int[] ints = NoInts;

    public Generic(uint id, EntityType type) : base(id, type) { }

    #region snapshot

    // owner id, position, y-rotation, flags and four parameter counters are always present
    public override int BufferSize => 30 + (animator
        ? 4 * animator.layerCount + 4 * floatIds.Length + 4 * intIds.Length + (boolIds.Length + 7) / 8
        : 0);

    public override void Write(Writer w)
    {
        WriteOwner(ref w);

        if (IsOwner)
        {
            w.Vector(agent.Position);
            w.Float(agent.Rotation.y);

            w.Bools(enemyId.Blessed);
        }
        else
        {
            w.Floats(x, y, z);
            w.Float(r.Next);

            w.Bools(blessed);
        }

        // the animator state is read from the live object on both sides: non-owners apply received
        // values to their animator every frame, so it always holds the latest synced state
        if (animator)
        {
            int layers = animator.layerCount;

            w.Byte((byte)layers);
            w.Byte((byte)boolIds.Length);
            w.Byte((byte)floatIds.Length);
            w.Byte((byte)intIds.Length);

            for (int i = 0; i < layers; i++) w.Int(animator.GetCurrentAnimatorStateInfo(i).fullPathHash);

            byte packed = 0;
            for (int i = 0; i < boolIds.Length; i++)
            {
                if (animator.GetBool(boolIds[i])) packed |= (byte)(1 << (i & 7));
                if ((i & 7) == 7) { w.Byte(packed); packed = 0; }
            }
            if ((boolIds.Length & 7) != 0) w.Byte(packed);

            for (int i = 0; i < floatIds.Length; i++) w.Float(animator.GetFloat(floatIds[i]));
            for (int i = 0; i < intIds.Length;   i++) w.Int(animator.GetInteger(intIds[i]));
        }
        else
        {
            w.Byte(0);
            w.Byte(0);
            w.Byte(0);
            w.Byte(0);
        }
    }

    public override void Read(Reader r)
    {
        if (ReadOwner(ref r)) return;

        r.Floats(ref x, ref y, ref z);
        this.r.Set(r.Float());

        blessed = (r.Byte() & 1) != 0;

        int layers = r.Byte(), bCount = r.Byte(), fCount = r.Byte(), iCount = r.Byte();

        Fit(ref states,     layers);
        Fit(ref lastStates, layers);
        Fit(ref bools,      bCount);
        Fit(ref floats,     fCount);
        Fit(ref ints,       iCount);

        for (int i = 0; i < layers; i++) states[i] = r.Int();

        byte packed = 0;
        for (int i = 0; i < bCount; i++)
        {
            if ((i & 7) == 0) packed = r.Byte();
            bools[i] = (packed & 1 << (i & 7)) != 0;
        }

        for (int i = 0; i < fCount; i++) floats[i] = r.Float();
        for (int i = 0; i < iCount; i++) ints[i]   = r.Int();
    }

    static void Fit<T>(ref T[] array, int size)
    {
        if (array.Length != size) array = new T[size];
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

        // most of the behaviour scripts extend the same class, the rest are exceptions
        List<Behaviour> bs = new();

        agent.Get(out EnemyScript[] scripts, true);
        if (scripts != null) bs.AddRange(scripts);

        agent.Get(out global::Idol idol, true);
        agent.Get(out MinotaurChase chase, true);
        agent.Get(out Deathcatcher catcher, true);
        agent.Get(out global::Wicked wicked, true);
        agent.Get(out DroneFlesh flesh, true);

        if (idol) bs.Add(idol);
        if (chase) bs.Add(chase);
        if (catcher) bs.Add(catcher);
        if (wicked) bs.Add(wicked);
        if (flesh) bs.Add(flesh);

        brain = bs.ToArray();
        kinematic = rb && rb.isKinematic;

        if (animator)
        {
            List<int> bIds = new(), fIds = new(), iIds = new();

            animator.parameters.Each(p =>
            {
                if (animator.IsParameterControlledByCurve(p.nameHash)) return;
                switch (p.type)
                {
                    case AnimatorControllerParameterType.Bool:  bIds.Add(p.nameHash); break;
                    case AnimatorControllerParameterType.Float: fIds.Add(p.nameHash); break;
                    case AnimatorControllerParameterType.Int:   iIds.Add(p.nameHash); break;
                }
            });

            boolIds  = bIds.ToArray();
            floatIds = fIds.ToArray();
            intIds   = iIds.ToArray();
        }
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

        if (animator)
        {
            int layers = Mathf.Min(states.Length, animator.layerCount);
            for (int i = 0; i < layers; i++)
            {
                if (states[i] != 0 && lastStates[i] != states[i]) animator.CrossFadeInFixedTime(lastStates[i] = states[i], .15f, i);
            }

            for (int i = 0; i < Mathf.Min(bools.Length,  boolIds.Length ); i++) animator.SetBool   (boolIds[i],  bools[i] );
            for (int i = 0; i < Mathf.Min(floats.Length, floatIds.Length); i++) animator.SetFloat  (floatIds[i], floats[i]);
            for (int i = 0; i < Mathf.Min(ints.Length,   intIds.Length  ); i++) animator.SetInteger(intIds[i],   ints[i]  );
        }

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

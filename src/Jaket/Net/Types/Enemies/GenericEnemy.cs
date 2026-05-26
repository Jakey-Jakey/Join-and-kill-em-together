namespace Jaket.Net.Types;

using System.Text;
using UnityEngine;
using UnityEngine.AI;

using Jaket.Content;
using Jaket.IO;

/// <summary> Conservative fallback sync for enemy types without custom attack replication yet. </summary>
public class GenericEnemy : Enemy
{
    const int MAX_ANIMATORS = 4;
    const int MAX_LAYERS = 4;
    const int MAX_PARAMETERS = 16;
    const int MAX_STATE_OBJECTS = 64;
    const int MAX_IDENTITY_BYTES = 240;

    Agent agent;
    string identity = "";
    Float x, y, z, rx, ry, rz;
    NavMeshAgent[] agents;
    bool[] agentStates;
    MonoBehaviour[] scripts;
    bool[] scriptStates;
    AnimatorMirror[] animators;
    PendingAnimator[] pendingAnimators;
    StateMirror states;
    PendingState pendingState;
    bool animationDirty;
    bool stateDirty;
    bool? locallyControlled;

    public GenericEnemy(uint id, EntityType type) : base(id, type) { }

    #region snapshot

    public override int BufferSize => 34 + IdentitySize + AnimationBufferSize + StateBufferSize;

    public override void Write(Writer w)
    {
        bool owned = IsOwner;
        WriteOwner(ref w);

        if (owned)
        {
            w.Vector(agent.Position);
            w.Vector(agent.Rotation);
            WriteIdentity(w);
            WriteAnimation(w, true);
            WriteState(w, true);
        }
        else
        {
            w.Floats(x, y, z);
            w.Vector(new(rx.Next, ry.Next, rz.Next));
            WriteIdentity(w);
            WriteAnimation(w, false);
            WriteState(w, false);
        }
    }

    public override void Read(Reader r)
    {
        if (ReadOwner(ref r)) return;

        r.Floats(ref x, ref y, ref z);

        var rot = r.Vector();
        rx.Set(rot.x);
        ry.Set(rot.y);
        rz.Set(rot.z);

        identity = r.String();
        ReadAnimation(r);
        ReadState(r);
    }

    #endregion
    #region logic

    public override void Create()
    {
        var obj = Entities.Enemies.Make(Type, new(x.Init, y.Init, z.Init), identity);
        if (!obj) { Hidden = true; return; }

        obj.SetActive(true);
        obj.transform.eulerAngles = new(rx.Init, ry.Init, rz.Init);
        Assign(obj.AddComponent<Agent>());
    }

    public override void Assign(Agent agent)
    {
        identity = SceneIdentity(agent.transform);
        base.Assign(this.agent = agent);

        agents = agent.GetComponentsInChildren<NavMeshAgent>(true);
        agentStates = new bool[agents.Length];
        for (int i = 0; i < agents.Length; i++) agentStates[i] = agents[i].enabled;

        scripts = agent.GetComponentsInChildren<MonoBehaviour>(true);
        scriptStates = new bool[scripts.Length];
        for (int i = 0; i < scripts.Length; i++) scriptStates[i] = scripts[i].enabled;

        var anims = agent.GetComponentsInChildren<Animator>(true);
        int count = Mathf.Min(anims.Length, MAX_ANIMATORS);
        animators = new AnimatorMirror[count];
        for (int i = 0; i < count; i++) animators[i] = new(anims[i]);

        states = new(agent.transform);

        var transfer = OnTransfer;
        OnTransfer = () =>
        {
            transfer?.Invoke();
            locallyControlled = null;
            UpdateControl();
        };
        UpdateControl();

        ApplyPendingAnimation();
        ApplyPendingState();
    }

    public override void Update(float delta)
    {
        UpdateControl();
        if (Locked || IsOwner) return;

        agent.Position = new(x.GetAware(delta), y.GetAware(delta), z.GetAware(delta));
        agent.Rotation = new(rx.GetAngle(delta), ry.GetAngle(delta), rz.GetAngle(delta));

        if (animationDirty) ApplyAnimation();
        if (stateDirty) ApplyState();
    }

    private void UpdateControl()
    {
        bool control = IsOwner && !Locked;
        if (locallyControlled == control) return;
        locallyControlled = control;

        for (int i = 0; i < agents.Length; i++)
            agents[i].enabled = control && agentStates[i];

        for (int i = 0; i < scripts.Length; i++)
        {
            var s = scripts[i];
            if (s is Agent || s is EnemyIdentifier || s is BossHealthBar) continue;
            if (s.GetType().Name == "EnemyIdentifierIdentifier") continue;

            s.enabled = control && scriptStates[i];
        }
    }

    #endregion
    #region identity

    int IdentitySize
    {
        get
        {
            return 1 + Encoding.ASCII.GetByteCount(identity ?? "");
        }
    }

    private void WriteIdentity(Writer w)
    {
        w.String(identity);
    }

    private static string SceneIdentity(Transform transform)
    {
        if (!transform) return "";

        var sb = new StringBuilder(128);
        var stack = new Transform[64];
        int count = 0;

        for (var t = transform; t && count < stack.Length; t = t.parent)
            stack[count++] = t;

        int used = 0;
        for (int i = count - 1; i >= 0; i--)
        {
            var name = stack[i].name;
            int next = used + name.Length + (used == 0 ? 0 : 1);
            if (next > MAX_IDENTITY_BYTES) break;

            if (used != 0) { sb.Append('/'); used++; }
            sb.Append(name);
            used += name.Length;
        }
        return sb.ToString();
    }

    #endregion
    #region state

    int StateBufferSize => states?.BufferSize ?? PendingState.EmptySize;

    private void WriteState(Writer w, bool capture)
    {
        if (states == null)
        {
            PendingState.WriteEmpty(w);
            return;
        }

        if (capture) states.Capture();
        states.Write(w);
    }

    private void ReadState(Reader r)
    {
        var pending = PendingState.Read(r);
        if (states == null)
        {
            pendingState = pending;
            return;
        }

        states.Read(pending);
        stateDirty = true;
    }

    private void ApplyPendingState()
    {
        if (pendingState == null || states == null) return;

        states.Read(pendingState);
        pendingState = null;
        stateDirty = true;
    }

    private void ApplyState()
    {
        states?.Apply();
        stateDirty = false;
    }

    private class StateMirror
    {
        readonly Transform[] transforms;
        readonly Renderer[] renderers;
        readonly Collider[] colliders;
        readonly bool[] active;
        readonly bool[] visible;
        readonly bool[] solid;

        public StateMirror(Transform root)
        {
            var allTransforms = root.GetComponentsInChildren<Transform>(true);
            int transformCount = Mathf.Min(Mathf.Max(0, allTransforms.Length - 1), MAX_STATE_OBJECTS);
            transforms = new Transform[transformCount];
            for (int i = 0; i < transformCount; i++) transforms[i] = allTransforms[i + 1];

            var allRenderers = root.GetComponentsInChildren<Renderer>(true);
            int rendererCount = Mathf.Min(allRenderers.Length, MAX_STATE_OBJECTS);
            renderers = new Renderer[rendererCount];
            for (int i = 0; i < rendererCount; i++) renderers[i] = allRenderers[i];

            var allColliders = root.GetComponentsInChildren<Collider>(true);
            int colliderCount = Mathf.Min(allColliders.Length, MAX_STATE_OBJECTS);
            colliders = new Collider[colliderCount];
            for (int i = 0; i < colliderCount; i++) colliders[i] = allColliders[i];

            active = new bool[transforms.Length];
            visible = new bool[renderers.Length];
            solid = new bool[colliders.Length];
        }

        public int BufferSize => PackedSize(active.Length) + PackedSize(visible.Length) + PackedSize(solid.Length);

        public void Capture()
        {
            for (int i = 0; i < active.Length; i++) active[i] = transforms[i].gameObject.activeSelf;
            for (int i = 0; i < visible.Length; i++) visible[i] = renderers[i].enabled;
            for (int i = 0; i < solid.Length; i++) solid[i] = colliders[i].enabled;
        }

        public void Write(Writer w)
        {
            WritePacked(w, active);
            WritePacked(w, visible);
            WritePacked(w, solid);
        }

        public void Read(PendingState pending)
        {
            for (int i = 0; i < pending.Active.Length && i < active.Length; i++) active[i] = pending.Active[i];
            for (int i = 0; i < pending.Visible.Length && i < visible.Length; i++) visible[i] = pending.Visible[i];
            for (int i = 0; i < pending.Solid.Length && i < solid.Length; i++) solid[i] = pending.Solid[i];
        }

        public void Apply()
        {
            for (int i = 0; i < active.Length; i++)
                if (transforms[i]) transforms[i].gameObject.SetActive(active[i]);

            for (int i = 0; i < visible.Length; i++)
                if (renderers[i]) renderers[i].enabled = visible[i];

            for (int i = 0; i < solid.Length; i++)
                if (colliders[i]) colliders[i].enabled = solid[i];
        }

        private static int PackedSize(int count) => 1 + ((count + 7) >> 3);

        private static void WritePacked(Writer w, bool[] values)
        {
            w.Byte((byte)values.Length);

            for (int i = 0; i < values.Length; i += 8)
            {
                byte packed = 0;
                for (int j = 0; j < 8 && i + j < values.Length; j++)
                    if (values[i + j]) packed |= (byte)(1 << j);

                w.Byte(packed);
            }
        }
    }

    private class PendingState
    {
        public const int EmptySize = 3;

        public readonly bool[] Active;
        public readonly bool[] Visible;
        public readonly bool[] Solid;

        private PendingState(bool[] active, bool[] visible, bool[] solid)
        {
            Active = active;
            Visible = visible;
            Solid = solid;
        }

        public static void WriteEmpty(Writer w)
        {
            w.Byte(0);
            w.Byte(0);
            w.Byte(0);
        }

        public static PendingState Read(Reader r)
        {
            var active = ReadBools(r);
            var visible = ReadBools(r);
            var solid = ReadBools(r);

            return new(active, visible, solid);
        }

        private static bool[] ReadBools(Reader r)
        {
            int count = r.Byte();
            var values = new bool[count];

            for (int i = 0; i < count; i += 8)
            {
                byte packed = r.Byte();
                for (int j = 0; j < 8 && i + j < count; j++)
                    values[i + j] = (packed & 1 << j) != 0;
            }
            return values;
        }
    }

    #endregion
    #region animation

    int AnimationBufferSize
    {
        get
        {
            if (animators == null) return 1;

            int size = 1;
            for (int i = 0; i < animators.Length; i++) size += animators[i].BufferSize;
            return size;
        }
    }

    private void WriteAnimation(Writer w, bool capture)
    {
        if (animators == null)
        {
            w.Byte(0);
            return;
        }

        w.Byte((byte)animators.Length);
        for (int i = 0; i < animators.Length; i++)
        {
            if (capture) animators[i].Capture();
            animators[i].Write(w);
        }
    }

    private void ReadAnimation(Reader r)
    {
        int count = r.Byte();
        if (animators == null)
        {
            pendingAnimators = new PendingAnimator[count];
            for (int i = 0; i < count; i++) pendingAnimators[i] = PendingAnimator.Read(r);
            return;
        }

        for (int i = 0; i < count; i++)
        {
            var pending = PendingAnimator.Read(r);
            if (i < animators.Length) animators[i].Read(pending);
        }
        animationDirty = true;
    }

    private void ApplyPendingAnimation()
    {
        if (pendingAnimators == null || animators == null) return;

        for (int i = 0; i < pendingAnimators.Length && i < animators.Length; i++)
            animators[i].Read(pendingAnimators[i]);

        pendingAnimators = null;
        animationDirty = true;
    }

    private void ApplyAnimation()
    {
        if (animators == null) return;

        for (int i = 0; i < animators.Length; i++) animators[i].Apply();
        animationDirty = false;
    }

    private class AnimatorMirror
    {
        readonly Animator animator;
        readonly AnimatorControllerParameter[] parameters;
        readonly int layerCount;
        readonly int parameterCount;
        readonly int[] states;
        readonly float[] times;
        readonly float[] weights;
        readonly float[] values;

        bool enabled;
        float speed;

        public AnimatorMirror(Animator animator)
        {
            this.animator = animator;
            layerCount = animator.runtimeAnimatorController ? Mathf.Min(animator.layerCount, MAX_LAYERS) : 0;

            var source = animator.runtimeAnimatorController ? animator.parameters : new AnimatorControllerParameter[0];
            int count = 0;
            for (int i = 0; i < source.Length && count < MAX_PARAMETERS; i++)
                if (source[i].type != AnimatorControllerParameterType.Trigger) count++;

            parameters = new AnimatorControllerParameter[count];
            for (int i = 0, j = 0; i < source.Length && j < count; i++)
                if (source[i].type != AnimatorControllerParameterType.Trigger) parameters[j++] = source[i];

            parameterCount = parameters.Length;
            states = new int[layerCount];
            times = new float[layerCount];
            weights = new float[layerCount];
            values = new float[parameterCount];
        }

        public int BufferSize => 7 + layerCount * 12 + parameterCount * 4;

        public void Capture()
        {
            enabled = animator.enabled;
            speed = animator.speed;

            for (int i = 0; i < layerCount; i++)
            {
                var state = animator.GetCurrentAnimatorStateInfo(i);
                states[i] = state.fullPathHash;
                times[i] = state.normalizedTime;
                weights[i] = animator.GetLayerWeight(i);
            }

            for (int i = 0; i < parameterCount; i++)
            {
                var p = parameters[i];
                values[i] = p.type switch
                {
                    AnimatorControllerParameterType.Bool => animator.GetBool(p.nameHash) ? 1f : 0f,
                    AnimatorControllerParameterType.Int => animator.GetInteger(p.nameHash),
                    _ => animator.GetFloat(p.nameHash),
                };
            }
        }

        public void Write(Writer w)
        {
            w.Bool(enabled);
            w.Float(speed);
            w.Byte((byte)layerCount);

            for (int i = 0; i < layerCount; i++)
            {
                w.Int(states[i]);
                w.Float(times[i]);
                w.Float(weights[i]);
            }

            w.Byte((byte)parameterCount);
            for (int i = 0; i < parameterCount; i++) w.Float(values[i]);
        }

        public void Read(PendingAnimator pending)
        {
            enabled = pending.Enabled;
            speed = pending.Speed;

            for (int i = 0; i < pending.LayerCount && i < layerCount; i++)
            {
                states[i] = pending.States[i];
                times[i] = pending.Times[i];
                weights[i] = pending.Weights[i];
            }

            for (int i = 0; i < pending.ParameterCount && i < parameterCount; i++)
                values[i] = pending.Values[i];
        }

        public void Apply()
        {
            if (!animator || !animator.runtimeAnimatorController) return;

            animator.enabled = enabled;
            animator.speed = speed;

            for (int i = 0; i < parameterCount; i++)
            {
                var p = parameters[i];
                switch (p.type)
                {
                    case AnimatorControllerParameterType.Bool:
                        animator.SetBool(p.nameHash, values[i] > .5f);
                        break;
                    case AnimatorControllerParameterType.Int:
                        animator.SetInteger(p.nameHash, Mathf.RoundToInt(values[i]));
                        break;
                    case AnimatorControllerParameterType.Float:
                        animator.SetFloat(p.nameHash, values[i]);
                        break;
                }
            }

            for (int i = 0; i < layerCount; i++)
            {
                if (states[i] == 0) continue;
                if (i > 0) animator.SetLayerWeight(i, weights[i]);

                var current = animator.GetCurrentAnimatorStateInfo(i);
                if (current.fullPathHash != states[i] || Mathf.Abs(Mathf.Repeat(current.normalizedTime, 1f) - Mathf.Repeat(times[i], 1f)) > .25f)
                    animator.Play(states[i], i, times[i]);
            }
        }
    }

    private readonly struct PendingAnimator
    {
        public readonly bool Enabled;
        public readonly float Speed;
        public readonly int LayerCount;
        public readonly int ParameterCount;
        public readonly int[] States;
        public readonly float[] Times;
        public readonly float[] Weights;
        public readonly float[] Values;

        private PendingAnimator(bool enabled, float speed, int layerCount, int parameterCount, int[] states, float[] times, float[] weights, float[] values)
        {
            Enabled = enabled;
            Speed = speed;
            LayerCount = layerCount;
            ParameterCount = parameterCount;
            States = states;
            Times = times;
            Weights = weights;
            Values = values;
        }

        public static PendingAnimator Read(Reader r)
        {
            bool enabled = r.Bool();
            float speed = r.Float();
            int layerCount = r.Byte();
            var states = new int[layerCount];
            var times = new float[layerCount];
            var weights = new float[layerCount];

            for (int i = 0; i < layerCount; i++)
            {
                states[i] = r.Int();
                times[i] = r.Float();
                weights[i] = r.Float();
            }

            int parameterCount = r.Byte();
            var values = new float[parameterCount];
            for (int i = 0; i < parameterCount; i++) values[i] = r.Float();

            return new(enabled, speed, layerCount, parameterCount, states, times, weights, values);
        }
    }

    #endregion
}

namespace Jaket.Net.Types;

using Jaket.Content;
using Jaket.Harmony;

/// <summary> Tangible entity of the swordsmachine type, builds on the generic backbone and negotiates the phases. </summary>
public class Swordsmachine : Generic
{
    Agent agent;
    SwordsMachine scr;

    public Swordsmachine(uint id, EntityType type) : base(id, type) { }

    #region logic

    public override void Rage(bool enraged)
    {
        base.Rage(enraged);
        if (enraged)
        {
            if (scr.enraged)
                scr.Enrage();
            else
                scr.Knockdown();
        }
        else scr.UnEnrage();
    }

    public override void Assign(Agent agent)
    {
        base.Assign(this.agent = agent);

        agent.Get(out scr);
    }

    public override void Update(float delta)
    {
        base.Update(delta);
        if (Locked) return;

        scr.phaseChangeHealth = scr.firstPhase ? PostHealth / 2f : 0f;
        if (!IsOwner) scr.targetHandle = null;
    }

    public override void Killed(bool explode)
    {
        if (explode)
        {
            if (Boss) ResFind<WeaponPickUp>().Each
            (
                w => IsReal(w) && (w.transform.position - agent.Position).magnitude < 70f,
                w => scr.shotgunPickUp = w.gameObject
            );
            scr.bossVersion = true;

            scr.firstPhase = false;
            scr.EndFirstPhase();
            Hidden = false;
        }
        else base.Killed(explode);
    }

    #endregion
    #region harmony

    [DynamicPatch(typeof(SwordsMachine), nameof(SwordsMachine.EndFirstPhase))]
    [Prefix]
    static void Phase(SwordsMachine __instance)
    {
        if (__instance.TryGetEntity(out Swordsmachine s) && !s.Hidden) s.Kill(2, w => { w.Bool(true); w.Bool(true); });
    }

    [DynamicPatch(typeof(SwordsMachine), nameof(SwordsMachine.TeleportAway))]
    [Prefix]
    static bool Leave(SwordsMachine __instance)
    {
        if (__instance.TryGetEntity(out Swordsmachine s) && !s.Hidden) s.Kill();
        return false;
    }

    [DynamicPatch(typeof(SwordsMachine), nameof(SwordsMachine.Enrage))]
    [Prefix]
    static void Enrage(SwordsMachine __instance)
    {
        if (__instance.TryGetEntity(out Swordsmachine s) && !s.Enraged) s.Enrage(true);
    }

    [DynamicPatch(typeof(SwordsMachine), nameof(SwordsMachine.UnEnrage))]
    [Prefix]
    static void Unrage(SwordsMachine __instance)
    {
        if (__instance.TryGetEntity(out Swordsmachine s) && s.Enraged) s.Enrage(false);
    }

    #endregion
}

namespace Jaket.Net.Types;

using UnityEngine;

using Jaket.Content;
using Jaket.Harmony;

/// <summary> Tangible entity of the cerberus type, builds on the generic backbone and negotiates the rage. </summary>
public class Cerberus : Generic
{
    StatueBoss scr;

    public Cerberus(uint id, EntityType type) : base(id, type) { }

    #region logic

    public override void Rage(bool enraged)
    {
        base.Rage(enraged);
        if (enraged)
            scr.Enrage();
        else
            scr.UnEnrage();
    }

    public override void Assign(Agent agent)
    {
        base.Assign(agent);

        agent.Get(out scr);

        if (Scene == "Level 0-5")
        {
            agent.Scale = Vector3.one * 1.2f;
            agent.Get(out EnemyIdentifier i);

            i.overrideFullName = "CERBERUS, GUARDIAN OF HELL";
        }
    }

    #endregion
    #region harmony

    [DynamicPatch(typeof(StatueBoss), nameof(StatueBoss.Enrage))]
    [Prefix]
    static void Enrage(StatueBoss __instance)
    {
        if (__instance.TryGetEntity(out Cerberus c) && !c.Enraged) c.Enrage(true);
    }

    [DynamicPatch(typeof(StatueBoss), nameof(StatueBoss.UnEnrage))]
    [Prefix]
    static void Unrage(StatueBoss __instance)
    {
        if (__instance.TryGetEntity(out Cerberus c) && c.Enraged) c.Enrage(false);
    }

    #endregion
}

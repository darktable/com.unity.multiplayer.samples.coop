using System;

namespace Unity.Multiplayer.Samples.BossRoom
{
    /// <summary>
    /// List of all Types of Actions. There is a many-to-one mapping of Actions to ActionLogics.
    /// </summary>
    public enum ActionLogic
    {
        Melee,
        RangedTargeted,
        Chase,
        Revive,
        LaunchProjectile,
        Emote,
        RangedFXTargeted,
        AoE,
        Trample,
        ChargedShield,
        Stunned,
        Target,
        ChargedLaunchProjectile,
        StealthMode,
        DashAttack,
        //O__O adding a new ActionLogic branch? Update Action.MakeAction!
    }
}
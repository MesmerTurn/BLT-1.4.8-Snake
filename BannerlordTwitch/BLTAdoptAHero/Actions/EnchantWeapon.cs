using System;
using System.Collections.Generic;
using System.Linq;
using BannerlordTwitch;
using BannerlordTwitch.Helpers;
using BannerlordTwitch.Localization;
using BannerlordTwitch.Rewards;
using BannerlordTwitch.Util;
using JetBrains.Annotations;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace BLTAdoptAHero
{
    /// <summary>
    /// Puts a looping particle effect on the hero's weapon for the rest of the battle.
    ///
    /// The effects are the game's own - nothing here adds new ones - so what is on offer is
    /// whatever the base game already ships: flames of various sizes, sparks, smoke. Each cast
    /// picks one the hero is not already carrying, so using the command again always visibly
    /// changes something rather than appearing to do nothing.
    ///
    /// Mission only. Particles are attached to a live agent, so the effect ends with the battle.
    /// </summary>
    [LocDisplayName("{=enchw001}Enchant Weapon"),
     LocDescription("{=enchw002}Sets a random flame or particle effect on the hero's weapon for the rest of the battle"),
     UsedImplicitly]
    public class EnchantWeapon : ICommandHandler
    {
        public Type HandlerConfigType => null;

        // Hand-picked rather than taken from the full particle list: most entries there are
        // explosions, rain or scenery smoke, none of which read as an enchantment on a blade.
        //
        // The first four are confirmed working in game. The rest are candidates: the engine
        // returns null for a system it does not know, and the game's attach code dereferences
        // that without checking, so an unknown name surfaces as a NullReferenceException. Rather
        // than guess which are real, the command tries one and remembers the ones that throw
        // (see Broken below), so the pool cleans itself up after a few uses.
        private static readonly (string Pfx, string Name)[] Effects =
        {
            ("psys_torch_fire_moving",      "trailing fire"),
            ("psys_game_blacksmith_flame",  "forge-fire"),
            ("psys_game_burning_agent",     "smoke and fire in its wake"),
            ("psys_battleground_fire_small","battlefield embers"),
            ("psys_campfire",               "a steady blaze"),
            ("psys_campfire_sparks",        "showering sparks"),
            ("psys_bug_fly_1",              "a swarm of golden lights"),
            ("psys_game_sparkle_b",         "a faint shimmer"),
            ("psys_haze_1",                 "black smoke"),
            ("psys_blaze_1",                "roaring flames"),
            ("psys_blaze_small_1",          "a low flame"),
            ("psys_blaze_vertical_1_medium","a rising column of fire"),
            ("psys_game_sparkle_a",         "glittering motes"),
        };

        // Names this installation has already proven it cannot attach. Populated at runtime so a
        // viewer is not shown the same failure twice, and so the odds improve as we go.
        private static readonly HashSet<string> Broken = new();

        // What each hero currently has, so the next cast can pick something different and so the
        // previous effect can be stopped instead of stacking one on top of another.
        private static readonly Dictionary<Hero, (AgentPfx Pfx, string Id)> Active = new();

        public void Execute(ReplyContext context, object config)
        {
            var adoptedHero = BLTAdoptAHeroCampaignBehavior.Current.GetAdoptedHero(context.UserName);
            if (adoptedHero == null)
            {
                ActionManager.SendReply(context, AdoptAHero.NoHeroMessage);
                return;
            }

            if (Mission.Current == null)
            {
                ActionManager.SendReply(context, "{=enchw003}You can only enchant a weapon in battle".Translate());
                return;
            }

            var agent = adoptedHero.GetAgent();
            if (agent == null || !agent.IsActive())
            {
                ActionManager.SendReply(context, "{=enchw004}You are not in the battle right now".Translate());
                return;
            }

            Active.TryGetValue(adoptedHero, out var current);

            // Never hand back the effect they already have - the command would look like it did
            // nothing - and never offer one this installation has already failed to attach.
            var choices = Effects
                .Where(e => e.Pfx != current.Id && !Broken.Contains(e.Pfx))
                .OrderBy(_ => MBRandom.RandomInt(1000))
                .ToList();

            if (choices.Count == 0)
            {
                ActionManager.SendReply(context, "{=enchw006}The enchantment failed".Translate());
                return;
            }

            // Walk the shuffled candidates until one actually attaches. Attaching is the only way
            // to find out whether the engine knows a particle system, so a failure here is data:
            // the name is struck off for the rest of the session instead of being rolled again.
            foreach (var candidate in choices)
            {
                AgentPfx pfx = null;
                try
                {
                    pfx = new AgentPfx(agent, new[]
                    {
                        new ParticleEffectDef
                        {
                            Name = candidate.Pfx,
                            AttachPoint = ParticleEffectDef.AttachPointEnum.OnWeapon,
                        }
                    });
                    pfx.Start();
                }
                catch (Exception ex)
                {
                    Broken.Add(candidate.Pfx);
                    Log.Error($"[EnchantWeapon] '{candidate.Pfx}' cannot be attached, dropping it for this session: {ex.Message}");
                    try { pfx?.Stop(); } catch { /* half-attached; nothing useful to do */ }
                    continue;
                }

                // Only drop the previous effect once the new one is definitely on, otherwise a
                // failed roll would leave the weapon bare.
                try { current.Pfx?.Stop(); } catch { /* the old agent may already be gone */ }

                Active[adoptedHero] = (pfx, candidate.Pfx);

                ActionManager.SendReply(context,
                    "{=enchw005}Your weapon is wreathed in {EFFECT}!".Translate(("EFFECT", candidate.Name)));
                return;
            }

            ActionManager.SendReply(context, "{=enchw006}The enchantment failed".Translate());
        }
    }
}

using System;
using System.Collections.Generic;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace ServerAntiCheat
{
    // Tracks player health each tick. When health drops, we know they took
    // damage and flag them as "combat tagged" for X seconds. If they disconnect
    // while tagged, it's a combat log.
    public class CombatLogFeature
    {
        public void Update(IServerPlayer player, bool enabled, int combatTagSeconds, long nowMs, Dictionary<string, float> lastKnownHealth, Dictionary<string, long> combatTaggedUntil)
        {
            if (!enabled) return;
            if (!TryGetEntityHealth(player, out float hp)) return;

            string uid = player.PlayerUID;
            if (lastKnownHealth.TryGetValue(uid, out float oldHp))
            {
                // A health decrease of more than a rounding error means they got hit
                if (hp < oldHp - 0.01f)
                {
                    combatTaggedUntil[uid] = nowMs + (combatTagSeconds * 1000L);
                }
            }
            lastKnownHealth[uid] = hp;
        }

        // The health value lives in a WatchedAttribute tree rather than a
        // direct property, so we have to dig it out manually
        private bool TryGetEntityHealth(IServerPlayer player, out float health)
        {
            health = 0f;
            try
            {
                if (player.Entity == null) return false;

                ITreeAttribute healthTree = player.Entity.WatchedAttributes.GetTreeAttribute("health");
                if (healthTree != null)
                {
                    health = healthTree.GetFloat("currenthealth");
                    return true;
                }
            }
            catch
            {
                // ignored
            }

            return false;
        }
    }
}

using System;
using System.Collections.Generic;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace ServerAntiCheat
{
    // Detects suspicious "ore hopping" — when a player mines rare ore from
    // many geographically separate veins in a short time window, it's a strong
    // signal they're using X-Ray to jump between deposits.
    public class OreVeinFeature
    {
        public bool Track(IServerPlayer player, AntiCheatConfig config, long nowMs, Dictionary<string, List<OreBreakPoint>> recentRareOreBreaks, BlockPos pos, out int clusterCount)
        {
            clusterCount = 0;
            if (!config.EnableOreVeinFingerprint) return false;

            string uid = player.PlayerUID;
            if (!recentRareOreBreaks.ContainsKey(uid)) recentRareOreBreaks[uid] = new List<OreBreakPoint>();

            var list = recentRareOreBreaks[uid];
            list.Add(new OreBreakPoint { Pos = pos.Copy(), Time = nowMs });

            // Expire old entries outside the rolling time window
            long minTime = nowMs - (config.OreVeinWindowSeconds * 1000L);
            list.RemoveAll(x => x.Time < minTime);

            // Cap the list so memory doesn't grow unbounded during long sessions
            int maxKeep = Math.Max(config.OreVeinMinimumRareOres * 3, 20);
            if (list.Count > maxKeep) list.RemoveAt(0);
            if (list.Count < config.OreVeinMinimumRareOres) return false;

            // Cluster the break positions: any two breaks within MinDistance
            // belong to the same vein. If there are too many distinct clusters,
            // the player is hopping between veins suspiciously fast.
            List<BlockPos> centers = new List<BlockPos>();
            foreach (OreBreakPoint hit in list)
            {
                bool matched = false;
                foreach (BlockPos c in centers)
                {
                    double dx = c.X - hit.Pos.X;
                    double dy = c.Y - hit.Pos.Y;
                    double dz = c.Z - hit.Pos.Z;
                    double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                    if (dist <= config.OreVeinMinDistance)
                    {
                        matched = true;
                        break;
                    }
                }
                if (!matched) centers.Add(hit.Pos);
            }

            clusterCount = centers.Count;
            if (clusterCount >= config.OreVeinDistinctVeinsAlert)
            {
                list.Clear();
                return true;
            }

            return false;
        }
    }
}

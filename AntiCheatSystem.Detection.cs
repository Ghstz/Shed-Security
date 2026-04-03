using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace ServerAntiCheat
{
    public partial class AntiCheatSystem
    {
        // Nuker/printer counters reset every second so we measure actions-per-second
        private void OnResetActionCounts(float dt)
        {
            breakCount.Clear();
            placeCount.Clear();
        }

        private void OnPlayerJoin(IServerPlayer player)
        {
            // Give the player 15 seconds before we start measuring movement,
            // otherwise spawn-in jitter or chunk loading can cause false flags
            loginGracePeriod[player.PlayerUID] = sapi.World.ElapsedMilliseconds + 15000;

            // Scan for blacklisted client mods after a short delay so the
            // client has time to send its mod list over the wire
            if (!player.HasPrivilege(Privilege.controlserver) && config.BlacklistedClientMods != null && config.BlacklistedClientMods.Count > 0)
            {
                string uid = player.PlayerUID;
                sapi.Event.RegisterCallback((dt) =>
                {
                    IServerPlayer p = sapi.World.AllOnlinePlayers.FirstOrDefault(x => x.PlayerUID == uid) as IServerPlayer;
                    if (p == null) return;
                    ScanPlayerMods(p);
                }, 5000);
            }

            // Make sure already-vanished admins stay invisible to this new player
            foreach (string vanishedUid in vanishedPlayers.Keys)
            {
                IServerPlayer vanished = sapi.World.AllOnlinePlayers.FirstOrDefault(x => x.PlayerUID == vanishedUid) as IServerPlayer;
                if (vanished != null)
                {
                    HidePlayerEntity(vanished, player);
                }
            }

            movementGracePeriod[player.PlayerUID] = sapi.World.ElapsedMilliseconds + config.TeleportGraceMs;

            if (player.HasPrivilege("shedsecurity.bypass")) return;

            // Throttle rapid reconnects to prevent join-spam abuse
            string uid2 = player.PlayerUID;
            long now = sapi.World.ElapsedMilliseconds;
            if (joinHistory.ContainsKey(uid2))
            {
                if (now - joinHistory[uid2] < 3000)
                {
                    player.Disconnect("[Shed Security] Rejoining too quickly. Please wait.");
                    return;
                }
                joinHistory[uid2] = now;
            }
            else joinHistory.Add(uid2, now);

            if (latestVersion != null && player.HasPrivilege(Privilege.controlserver))
            {
                string msg = $"📢 [Shed Security] Update Available: v{latestVersion} (Current: v{ModVersion})";
                player.SendMessage(GlobalConstants.GeneralChatGroup, msg, EnumChatType.Notification);
            }
        }

        // --- Chat Moderation ---

        private void OnPlayerChat(IServerPlayer player, int channelId, ref string message, ref string data, BoolRef consumed)
        {
            // Frozen players can't do anything, not even chat
            if (frozenPlayers.Contains(player.PlayerUID))
            {
                player.SendMessage(GlobalConstants.GeneralChatGroup, "[Shed Security] You are FROZEN and cannot chat.", EnumChatType.Notification);
                consumed.value = true;
                return;
            }

            if (mutedPlayers.ContainsKey(player.PlayerUID))
            {
                long expiry = mutedPlayers[player.PlayerUID];
                if (DateTime.UtcNow.Ticks < expiry)
                {
                    long remainingMins = (expiry - DateTime.UtcNow.Ticks) / TimeSpan.TicksPerMinute;
                    player.SendMessage(GlobalConstants.GeneralChatGroup, $"[Shed Security] 🔇 You are muted for {remainingMins + 1} more minutes.", EnumChatType.Notification);
                    consumed.value = true;
                    return;
                }
                else
                {
                    mutedPlayers.Remove(player.PlayerUID);
                    SaveMutes();
                }
            }

            // Admins skip the chat filter, spam, and rate checks
            if (player.HasPrivilege(Privilege.controlserver) || player.HasPrivilege("shedsecurity.bypass")) return;

            if (config.BannedWords != null)
            {
                foreach (string badWord in config.BannedWords)
                {
                    if (message.ToLower().Contains(badWord.ToLower()))
                    {
                        player.SendMessage(GlobalConstants.GeneralChatGroup, "[Shed Security] ⚠ Watch your language.", EnumChatType.Notification);
                        consumed.value = true;
                        return;
                    }
                }
            }

            // Block identical consecutive messages
            if (lastChatMessage.ContainsKey(player.PlayerUID))
            {
                if (lastChatMessage[player.PlayerUID].Equals(message, StringComparison.OrdinalIgnoreCase))
                {
                    player.SendMessage(GlobalConstants.GeneralChatGroup, "[Shed Security] Please do not repeat the same message.", EnumChatType.Notification);
                    consumed.value = true;
                    return;
                }
            }
            lastChatMessage[player.PlayerUID] = message;

            // Rate limit: prevent message flooding
            long now = sapi.World.ElapsedMilliseconds;
            int cooldownMs = GetChatCooldownMs(player);
            if (lastChatTime.ContainsKey(player.PlayerUID))
            {
                if (now - lastChatTime[player.PlayerUID] < cooldownMs)
                {
                    player.SendMessage(GlobalConstants.GeneralChatGroup, "[Shed Security] Please slow down.", EnumChatType.Notification);
                    consumed.value = true;
                    return;
                }
            }
            lastChatTime[player.PlayerUID] = now;
        }

        // --- Block Break Detection (Nuker, Reach, X-Ray) ---

        private void OnPlayerBreakBlock(IServerPlayer player, BlockSelection blockSel, ref float dropQuantityMultiplier, ref EnumHandling handling)
        {
            if (player == null || player.Entity == null || blockSel == null) return;
            if (player.HasPrivilege(Privilege.controlserver) || player.HasPrivilege("shedsecurity.bypass")) return;

            if (shadowBannedPlayers.Contains(player.PlayerUID) || frozenPlayers.Contains(player.PlayerUID))
            {
                handling = EnumHandling.PreventDefault;
                return;
            }

            if (IsBlockOutOfReach(player, blockSel.Position))
            {
                handling = EnumHandling.PreventDefault;
                AddStrike(player, "Reach (BlockBreak)");
                return;
            }

            string uid = player.PlayerUID;

            // We need the block info before nuker checks so we can whitelist foliage
            Block block = sapi.World.BlockAccessor.GetBlock(blockSel.Position);
            if (block == null || block.Code == null) return;

            string path = block.Code.Path;

            // Shears can instant-break foliage, so don't count those as nuker hits
            bool isFoliage = path.Contains("leaves") || path.Contains("tallgrass") || path.Contains("vine");

            if (config.EnableAntiNuker && !isFoliage)
            {
                if (!breakCount.ContainsKey(uid)) breakCount[uid] = 0;
                breakCount[uid]++;
                int maxBreaks = GetMaxBlocksBrokenPerSecond(player);
                if (breakCount[uid] > maxBreaks)
                {
                    handling = EnumHandling.PreventDefault;
                    if (breakCount[uid] == maxBreaks + 1)
                    {
                        AddStrike(player, "Nuker / FastBreak");
                    }
                    return;
                }
            }

            if (!playerStats.ContainsKey(uid)) playerStats[uid] = new PlayerMiningStats { LastResetTime = sapi.World.ElapsedMilliseconds };
            var stats = playerStats[uid];

            // Reset counters every 30 minutes so a long session's stale ratios
            // don't skew the X-Ray heuristic
            if (sapi.World.ElapsedMilliseconds - stats.LastResetTime > 1800000)
            {
                stats.OresMined = 0;
                stats.StoneMined = 0;
                stats.LastResetTime = sapi.World.ElapsedMilliseconds;
            }

            bool isRareOre = false;
            foreach (string ore in config.RareOres)
            {
                if (path.Contains(ore))
                {
                    isRareOre = true;
                    HandleOreBatching(player, block, blockSel);

                    bool isDense = false;
                    foreach (string dense in config.DenseOres)
                    {
                        if (path.Contains(dense)) { isDense = true; break; }
                    }

                    if (!isDense)
                    {
                        stats.OresMined++;
                        TrackOreVeinPattern(player, blockSel.Position);
                    }

                    break;
                }
            }

            if (!isRareOre && (path.StartsWith("rock-") || path.StartsWith("soil-") || path.StartsWith("gravel-") || path.StartsWith("sand-")))
            {
                stats.StoneMined++;
            }

            // X-Ray heuristic: if ore-to-stone ratio is suspiciously high,
            // the player is likely using an ore-highlighting mod
            if (config.EnableHeuristics)
            {
                int total = stats.StoneMined + stats.OresMined;

                if (total > config.MinBlocksForRatio)
                {
                    double ratio = (double)stats.OresMined / total;

                    if (ratio > config.XRayRatioThreshold)
                    {
                        string msg = $"🕵️ X-RAY DETECTED: {player.PlayerName} has {ratio:P1} Ore Ratio! ({stats.OresMined} Ores / {total} Blocks)";

                        LogSecurity(msg);
                        SendDiscordAlert(msg);
                        foreach (IServerPlayer p in sapi.World.AllOnlinePlayers)
                        {
                            if (p.HasPrivilege(Privilege.controlserver))
                                p.SendMessage(GlobalConstants.InfoLogChatGroup, msg, EnumChatType.Notification);
                        }

                        // Halve the counters instead of zeroing them —
                        // triggers again quickly if they keep X-Raying
                        stats.OresMined /= 2;
                        stats.StoneMined /= 2;
                    }
                }
            }
        }

        // --- Block Place Detection (Printer, Reach) ---

        private void OnPlayerPlaceBlock(IServerPlayer player, int oldblockId, BlockSelection blockSel, ItemStack withItemStack)
        {
            if (player == null || player.Entity == null) return;
            if (player.HasPrivilege(Privilege.controlserver) || player.HasPrivilege("shedsecurity.bypass")) return;

            if (shadowBannedPlayers.Contains(player.PlayerUID) || frozenPlayers.Contains(player.PlayerUID))
            {
                sapi.World.BlockAccessor.SetBlock(0, blockSel.Position);
                sapi.World.BlockAccessor.MarkBlockDirty(blockSel.Position);
                player.InventoryManager.ActiveHotbarSlot.MarkDirty();
                return;
            }

            if (blockSel != null && IsBlockOutOfReach(player, blockSel.Position))
            {
                sapi.World.BlockAccessor.SetBlock(0, blockSel.Position);
                sapi.World.BlockAccessor.MarkBlockDirty(blockSel.Position);
                player.InventoryManager.ActiveHotbarSlot.MarkDirty();
                AddStrike(player, "Reach (BlockPlace)");
                return;
            }

            if (!player.Entity.Alive) return;

            if (config.EnableAntiPrinter)
            {
                string uid = player.PlayerUID;
                if (!placeCount.ContainsKey(uid)) placeCount[uid] = 0;
                placeCount[uid]++;
                int maxPlaces = GetMaxBlocksPlacedPerSecond(player);

                if (placeCount[uid] > maxPlaces)
                {
                    sapi.World.BlockAccessor.SetBlock(0, blockSel.Position);
                    sapi.World.BlockAccessor.MarkBlockDirty(blockSel.Position);
                    player.InventoryManager.ActiveHotbarSlot.MarkDirty();
                    AddStrike(player, "FastPlace / Printer");
                }
            }
        }

        // --- Movement Detection (Speed, NoClip, Freeze) ---
        // Runs every 500ms. We compare each player's current position to their
        // last known position and flag anything that looks like speed hacking,
        // flying, or noclipping through walls.
        private void OnCheckPlayerMovement(float dt)
        {
            ProcessOreQueue();

            // If the server is lagging hard, dt balloons and makes everyone
            // look like they're teleporting. Bail out and just snapshot positions.
            double estimatedTPS = 30.0 * (0.5 / dt);
            if (estimatedTPS < config.MinServerTPS || dt > 1.0)
            {
                foreach (IServerPlayer p in sapi.World.AllOnlinePlayers) UpdateLastPos(p);
                return;
            }

            foreach (IServerPlayer player in sapi.World.AllOnlinePlayers)
            {
                if (player.Entity == null) continue;

                UpdateCombatTag(player);

                if (!lastPositions.ContainsKey(player.PlayerUID))
                {
                    UpdateLastPos(player);
                    continue;
                }

                // Frozen means frozen — rubber-band them back every tick
                if (frozenPlayers.Contains(player.PlayerUID))
                {
                    SafeTeleport(player, lastPositions[player.PlayerUID]);
                    continue;
                }

                // Let players settle in after login before checking
                if (loginGracePeriod.ContainsKey(player.PlayerUID))
                {
                    if (sapi.World.ElapsedMilliseconds < loginGracePeriod[player.PlayerUID]) { UpdateLastPos(player); continue; }
                    else { loginGracePeriod.Remove(player.PlayerUID); }
                }

                // Teleport cooldown — don't flag someone who was just /tp'd
                if (movementGracePeriod.ContainsKey(player.PlayerUID) && sapi.World.ElapsedMilliseconds < movementGracePeriod[player.PlayerUID])
                {
                    UpdateLastPos(player);
                    continue;
                }

                // Skip anyone who legitimately can move however they want
                if (player.HasPrivilege(Privilege.controlserver) || player.HasPrivilege("shedsecurity.bypass")) { UpdateLastPos(player); continue; }
                if (player.Entity.MountedOn != null) { UpdateLastPos(player); continue; }
                if (player.WorldData.CurrentGameMode == EnumGameMode.Creative ||
                    player.WorldData.CurrentGameMode == EnumGameMode.Spectator ||
                    player.Entity.ServerControls.IsFlying)
                {
                    UpdateLastPos(player);
                    continue;
                }

                EntityPos lastPos = lastPositions[player.PlayerUID];
                EntityPos currentPos = EntityPosAccessor.GetPos(player.Entity);

                // Sanity check: if last pos was near world origin, something went wrong
                // (probably a fresh spawn or void). Also ignore tiny movements and huge
                // jumps that look like a server-side teleport rather than actual hacking.
                if (lastPos.SquareDistanceTo(0, 0, 0) < 25.0) { UpdateLastPos(player); continue; }
                double rawDist = currentPos.SquareDistanceTo(lastPos);
                if (rawDist > (config.TeleportGraceDistance * config.TeleportGraceDistance))
                {
                    movementGracePeriod[player.PlayerUID] = sapi.World.ElapsedMilliseconds + config.TeleportGraceMs;
                    UpdateLastPos(player);
                    continue;
                }
                if (rawDist < 0.01) { UpdateLastPos(player); continue; }

                // NoClip check: see if the player's head is inside a fully solid block.
                // We whitelist partial blocks (slabs, trapdoors, etc.) since their collision
                // boxes don't fill the full block space.
                if (config.EnableNoClipCheck)
                {
                    Block blockHead = sapi.World.BlockAccessor.GetBlock(currentPos.XYZ.Add(0, player.Entity.LocalEyePos.Y, 0).AsBlockPos);
                    if (blockHead.SideSolid.All && !blockHead.IsLiquid() && blockHead.Id != 0)
                    {
                        string headPath = blockHead.Code?.Path ?? "";
                        bool isPartialBlock = headPath.Contains("trapdoor") || headPath.Contains("slab") || headPath.Contains("stairs") || headPath.Contains("fence") || headPath.Contains("door");
                        if (!isPartialBlock)
                        {
                            SafeTeleport(player, lastPos);
                            continue;
                        }
                    }
                }

                // After taking damage, mob knockback can fling players fast enough
                // to trip the speed check. Give them 1.5s of grace.
                if (knockbackGracePeriod.TryGetValue(player.PlayerUID, out long kbExpiry) && sapi.World.ElapsedMilliseconds < kbExpiry)
                {
                    UpdateLastPos(player);
                    continue;
                }

                // Speed calculations: compare horizontal and vertical speed against
                // what's physically possible with the player's current stats, sprint
                // state, and the block they're standing on.
                if (config.EnableSpeedChecks)
                {
                    double distH = Math.Sqrt(Math.Pow(currentPos.X - lastPos.X, 2) + Math.Pow(currentPos.Z - lastPos.Z, 2));
                    double speedH = distH / dt;

                    double distV = currentPos.Y - lastPos.Y;
                    double speedV = distV / dt;

                    double allowedSpeed = player.Entity.Stats.GetBlended("walkspeed");
                    if (player.Entity.Controls.Sprint) allowedSpeed *= 2.0;

                    // Some blocks (paths, roads) have a walk-speed multiplier
                    BlockPos belowPos = currentPos.AsBlockPos.DownCopy();
                    Block blockBelow = sapi.World.BlockAccessor.GetBlock(belowPos);
                    if (blockBelow != null && blockBelow.WalkSpeedMultiplier > 0)
                    {
                        allowedSpeed *= blockBelow.WalkSpeedMultiplier;
                    }

                    double limitH = (allowedSpeed * 1.2) + GetMaxHorizontalSpeed(player);

                    double limitV = GetMaxVerticalSpeed(player) + 3.0;
                    if (player.Entity.Controls.Jump) limitV += 5.0;

                    // Running up stairs/slopes can spike vertical speed, so when
                    // the player is on the ground, the vertical limit should be at
                    // least as generous as the horizontal one
                    if (player.Entity.OnGround && limitV < limitH) limitV = limitH;

                    if (speedH > limitH || (speedV > limitV && distV > 0))
                    {
                        SafeTeleport(player, lastPos);
                        AddStrike(player, "Unfair Advantage (Speed/Fly)");
                    }
                }

                UpdateLastPos(player);
            }
        }

        // --- Ore Batching ---
        // Instead of spamming an alert for every single ore block, we batch
        // consecutive breaks of the same ore type and report them together
        // after a short delay (OreAlertBatchSeconds).
        private void ProcessOreQueue()
        {
            long now = sapi.World.ElapsedMilliseconds;
            List<string> toRemove = new List<string>();

            foreach (var entry in miningSessions.ToList())
            {
                if (now >= entry.Value.ReportTime)
                {
                    string uid = entry.Key;
                    IServerPlayer player = sapi.World.AllOnlinePlayers.FirstOrDefault(p => p.PlayerUID == uid) as IServerPlayer;

                    if (player != null)
                    {
                        FlushMiningSession(player, entry.Value);
                    }
                    toRemove.Add(uid);
                }
            }

            foreach (string uid in toRemove) miningSessions.Remove(uid);
        }

        private void FlushMiningSession(IServerPlayer player, MiningSession session)
        {
            string msg = $"💎 MINING: {player.PlayerName} found {session.Count}x {session.OreName}";

            LogSecurity(msg);
            SendDiscordAlert(msg);

            foreach (IServerPlayer p in sapi.World.AllOnlinePlayers)
            {
                if (p.HasPrivilege(Privilege.controlserver))
                    p.SendMessage(GlobalConstants.InfoLogChatGroup, msg, EnumChatType.Notification);
            }
        }

        private void HandleOreBatching(IServerPlayer player, Block block, BlockSelection blockSel)
        {
            string miningUid = player.PlayerUID;
            string cleanName = block.GetPlacedBlockName(sapi.World, blockSel.Position);

            if (miningSessions.ContainsKey(miningUid))
            {
                var session = miningSessions[miningUid];
                if (session.OreName != cleanName)
                {
                    FlushMiningSession(player, session);
                    miningSessions[miningUid] = new MiningSession
                    {
                        OreName = cleanName,
                        Count = 1,
                        ReportTime = sapi.World.ElapsedMilliseconds + (long)(config.OreAlertBatchSeconds * 1000)
                    };
                }
                else
                {
                    session.Count++;
                }
            }
            else
            {
                miningSessions[miningUid] = new MiningSession
                {
                    OreName = cleanName,
                    Count = 1,
                    ReportTime = sapi.World.ElapsedMilliseconds + (long)(config.OreAlertBatchSeconds * 1000)
                };
            }
        }
    }
}

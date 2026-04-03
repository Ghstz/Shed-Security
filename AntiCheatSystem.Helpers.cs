using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace ServerAntiCheat
{
    public partial class AntiCheatSystem
    {
        // Reuse a single HttpClient for Discord webhooks — creating one per request
        // leaks sockets and eventually causes failures under load
        private static readonly HttpClient discordClient = new HttpClient();

        // --- Strike System ---

        private void AddStrike(IServerPlayer player, string reason)
        {
            string playerName = player.PlayerName;
            if (string.IsNullOrEmpty(playerName)) playerName = "Unknown Player";

            string uid = player.PlayerUID;

            if (player.HasPrivilege(Privilege.controlserver) || player.HasPrivilege("shedsecurity.bypass")) return;

            if (!violationStrikes.ContainsKey(uid))
            {
                violationStrikes[uid] = new StrikeInfo { Username = playerName, Count = 0 };
            }

            violationStrikes[uid].Username = playerName;
            violationStrikes[uid].Count++;

            int currentStrikes = violationStrikes[uid].Count;
            int maxStrikes = GetMaxStrikesBeforeKick(player);

            SaveStrikes();

            player.SendMessage(GlobalConstants.GeneralChatGroup, $"[Shed Security] ⚠ Abnormal behavior detected! ({currentStrikes}/{maxStrikes})", EnumChatType.Notification);

            LogSecurity($"Warn: {playerName} - {reason} ({currentStrikes})");

            if (currentStrikes >= maxStrikes)
            {
                if (config.EnableAutoKick)
                {
                    string msg = $"🔒 Shed Security: {playerName} was removed for {reason}.";
                    LogSecurity(msg);
                    SendDiscordAlert(msg);

                    sapi.Event.RegisterCallback((dt) =>
                    {
                        if (player.ConnectionState == EnumClientState.Connected || player.ConnectionState == EnumClientState.Playing)
                        {
                            player.Disconnect($"[Shed Security] Kicked: {reason}");
                        }
                    }, 50);

                    violationStrikes[uid].Count = 0;
                    SaveStrikes();
                }
                else
                {
                    string alertMsg = $"⚠️ [ADMIN ALERT] {playerName} has exceeded Max Strikes ({currentStrikes}) for {reason}. Auto-Kick is DISABLED.";
                    LogSecurity(alertMsg);
                    SendDiscordAlert(alertMsg);

                    foreach (IServerPlayer p in sapi.World.AllOnlinePlayers)
                    {
                        if (p.HasPrivilege(Privilege.controlserver))
                            p.SendMessage(GlobalConstants.GeneralChatGroup, alertMsg, EnumChatType.Notification);
                    }
                }
            }
        }

        // --- Discord Integration ---

        private void SendDiscordAlert(string message)
        {
            if (!config.EnableDiscordAlerts || string.IsNullOrEmpty(config.DiscordWebhookUrl)) return;

            Task.Run(async () =>
            {
                try
                {
                    string json = JsonSerializer.Serialize(new { content = message });
                    var content = new StringContent(json, Encoding.UTF8, "application/json");
                    await discordClient.PostAsync(config.DiscordWebhookUrl, content);
                }
                catch { }
            });
        }

        // --- Persistence (Strikes, Mutes, State) ---
        // These all run file I/O. Saves are offloaded to Task.Run so the game
        // thread doesn't block on disk writes.

        private void LoadStrikes()
        {
            try
            {
                if (File.Exists(strikesFilePath))
                {
                    string json = File.ReadAllText(strikesFilePath);
                    violationStrikes = JsonSerializer.Deserialize<Dictionary<string, StrikeInfo>>(json)
                                    ?? new Dictionary<string, StrikeInfo>();
                }
            }
            catch (Exception e)
            {
                sapi.Logger.Warning("[Shed Security] Failed to load strikes: " + e.Message);
                violationStrikes = new Dictionary<string, StrikeInfo>();
            }
        }

        // Snapshot-then-write pattern: copy the dictionary so the background
        // thread isn't racing with the game thread mutating the live data
        private void SaveStrikes()
        {
            var snapshot = new Dictionary<string, StrikeInfo>(violationStrikes);
            string path = strikesFilePath;
            Task.Run(() =>
            {
                try
                {
                    string json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(path, json);
                }
                catch (Exception e)
                {
                    sapi.Logger.Warning("[Shed Security] Failed to save strikes: " + e.Message);
                }
            });
        }

        private void LoadMutes()
        {
            try
            {
                if (File.Exists(mutesFilePath))
                {
                    string json = File.ReadAllText(mutesFilePath);
                    mutedPlayers = JsonSerializer.Deserialize<Dictionary<string, long>>(json) ?? new Dictionary<string, long>();
                }
            }
            catch { }
        }

        private void SaveMutes()
        {
            var snapshot = new Dictionary<string, long>(mutedPlayers);
            string path = mutesFilePath;
            Task.Run(() =>
            {
                try
                {
                    string json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(path, json);
                }
                catch { }
            });
        }

        // Vanished players are stored as <uid, int> because EnumGameMode
        // serialises cleanly as an int but not as a named enum in JSON
        private void LoadState()
        {
            var state = statePersistence.Load(stateFilePath);
            if (state == null) return;
            shadowBannedPlayers = state.ShadowBanned != null ? new HashSet<string>(state.ShadowBanned) : new HashSet<string>();
            frozenPlayers = state.Frozen != null ? new HashSet<string>(state.Frozen) : new HashSet<string>();
            if (state.VanishedPlayers != null)
            {
                vanishedPlayers = new Dictionary<string, EnumGameMode>();
                foreach (var kvp in state.VanishedPlayers)
                    vanishedPlayers[kvp.Key] = (EnumGameMode)kvp.Value;
            }
        }

        private void SaveState()
        {
            var vanishInt = new Dictionary<string, int>();
            foreach (var kvp in vanishedPlayers)
                vanishInt[kvp.Key] = (int)kvp.Value;

            var state = new PersistedState
            {
                ShadowBanned = shadowBannedPlayers.ToList(),
                Frozen = frozenPlayers.ToList(),
                VanishedPlayers = vanishInt
            };
            statePersistence.Save(stateFilePath, state);
        }

        // --- Player Override Getters ---
        // Each of these checks the per-player config overrides first, then
        // falls back to the global config value. This lets admins give
        // specific players custom limits (e.g. higher reach for builders).

        private PlayerOverrideConfig GetPlayerOverride(IServerPlayer player)
        {
            return playerOverrideFeature.Resolve(config, player);
        }

        private int GetMaxBlocksBrokenPerSecond(IServerPlayer player)
        {
            var ov = GetPlayerOverride(player);
            return ov?.MaxBlocksBrokenPerSecond ?? config.MaxBlocksBrokenPerSecond;
        }

        private int GetMaxBlocksPlacedPerSecond(IServerPlayer player)
        {
            var ov = GetPlayerOverride(player);
            return ov?.MaxBlocksPlacedPerSecond ?? config.MaxBlocksPlacedPerSecond;
        }

        private int GetMaxStrikesBeforeKick(IServerPlayer player)
        {
            var ov = GetPlayerOverride(player);
            return ov?.MaxStrikesBeforeKick ?? config.MaxStrikesBeforeKick;
        }

        private int GetChatCooldownMs(IServerPlayer player)
        {
            var ov = GetPlayerOverride(player);
            return ov?.ChatCooldownMs ?? config.ChatCooldownMs;
        }

        private double GetMaxHorizontalSpeed(IServerPlayer player)
        {
            var ov = GetPlayerOverride(player);
            return ov?.MaxHorizontalSpeed ?? config.MaxHorizontalSpeed;
        }

        private double GetMaxVerticalSpeed(IServerPlayer player)
        {
            var ov = GetPlayerOverride(player);
            return ov?.MaxVerticalSpeed ?? config.MaxVerticalSpeed;
        }

        private double GetMaxBlockReach(IServerPlayer player)
        {
            var ov = GetPlayerOverride(player);
            return ov?.MaxBlockReach ?? config.MaxBlockReach;
        }

        // --- Utility Helpers ---

        private void AuditAdminAction(TextCommandCallingArgs args, string action)
        {
            if (!config.EnableAdminAuditLog) return;
            string admin = args.Caller?.Player?.PlayerName ?? "Console";
            LogSecurity($"[AUDIT] {admin}: {action}");
        }

        private void RotateLogIfNeeded()
        {
            logMaintenance.Rotate(logFilePath, config.MaxLogFileSizeMB, config.MaxLogBackups);
        }

        // Measures distance from the player's feet to the center of the target
        // block. Comparing against their configured max reach.
        private bool IsBlockOutOfReach(IServerPlayer player, BlockPos pos)
        {
            Vec3d p = EntityPosAccessor.GetPos(player.Entity).XYZ;
            double cx = pos.X + 0.5;
            double cy = pos.Y + 0.5;
            double cz = pos.Z + 0.5;
            double dx = p.X - cx;
            double dy = p.Y - cy;
            double dz = p.Z - cz;
            double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            return dist > GetMaxBlockReach(player);
        }

        // Don't teleport someone to world origin — that's almost certainly a
        // corrupted position and would just make things worse
        private void SafeTeleport(IServerPlayer player, EntityPos target)
        {
            if (target.SquareDistanceTo(0, 0, 0) < 5.0)
            {
                UpdateLastPos(player);
            }
            else
            {
                player.Entity.TeleportTo(target);
            }
        }

        private void UpdateLastPos(IServerPlayer player)
        {
            if (lastPositions.ContainsKey(player.PlayerUID))
                lastPositions[player.PlayerUID] = EntityPosAccessor.GetPos(player.Entity).Copy();
            else
                lastPositions.Add(player.PlayerUID, EntityPosAccessor.GetPos(player.Entity).Copy());
        }

        // --- Feature Delegators ---
        // Thin wrappers that forward to the feature classes.
        // Keeps the partial class focused on orchestration.

        private void UpdateCombatTag(IServerPlayer player)
        {
            float oldHp = 0f;
            lastKnownHealth.TryGetValue(player.PlayerUID, out oldHp);

            combatLogFeature.Update(
                player,
                config.EnableCombatLog,
                config.CombatTagSeconds,
                sapi.World.ElapsedMilliseconds,
                lastKnownHealth,
                combatTaggedUntil
            );

            // Health dropped this tick? The player just got hit — give them
            // a 1.5s grace window so knockback velocity doesn't trip the speed check
            if (lastKnownHealth.TryGetValue(player.PlayerUID, out float newHp) && newHp < oldHp - 0.01f)
            {
                knockbackGracePeriod[player.PlayerUID] = sapi.World.ElapsedMilliseconds + 1500;
            }
        }

        private void TrackOreVeinPattern(IServerPlayer player, BlockPos pos)
        {
            if (oreVeinFeature.Track(player, config, sapi.World.ElapsedMilliseconds, recentRareOreBreaks, pos, out int clusters))
            {
                string msg = $"🧭 ORE VEIN PATTERN: {player.PlayerName} mined across {clusters} distinct rare-ore clusters quickly.";
                LogSecurity(msg);
                SendDiscordAlert(msg);
                AddStrike(player, "Suspicious multi-vein ore pathing");
            }
        }

        private void ScanPlayerMods(IServerPlayer player)
        {
            stealthNetworkFeature.ScanPlayerMods(
                sapi,
                player,
                config.BlacklistedClientMods,
                LogSecurity,
                SendDiscordAlert,
                (msg) =>
                {
                    foreach (IServerPlayer p in sapi.World.AllOnlinePlayers)
                    {
                        if (p.HasPrivilege(Privilege.controlserver))
                            p.SendMessage(GlobalConstants.GeneralChatGroup, msg, EnumChatType.Notification);
                    }
                }
            );
        }

        private void HidePlayerEntity(IServerPlayer admin)
        {
            stealthNetworkFeature.HidePlayerEntity(sapi, admin);
        }

        private void HidePlayerEntity(IServerPlayer admin, IServerPlayer viewer)
        {
            stealthNetworkFeature.HidePlayerEntity(sapi, admin, viewer);
        }

        private void ShowPlayerEntity(IServerPlayer admin)
        {
            stealthNetworkFeature.ShowPlayerEntity(sapi, admin);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace ServerAntiCheat
{
    public partial class AntiCheatSystem
    {
        private TextCommandResult OnCmdReload(TextCommandCallingArgs args)
        {
            AuditAdminAction(args, "/shed reload");
            LoadConfig();
            LogSecurity("Configuration reloaded by admin.");
            return TextCommandResult.Success("[Shed Security] Config reloaded.");
        }

        private TextCommandResult OnCmdRestart(TextCommandCallingArgs args)
        {
            AuditAdminAction(args, "/shed restart");
            LogSecurity("Admin initiated /shed restart sequence.");

            // Kick everyone else first, then fire the stop command after a short
            // delay so the disconnect packets actually reach clients before shutdown
            foreach (IServerPlayer player in sapi.World.AllOnlinePlayers)
            {
                if (player.PlayerUID != args.Caller.Player?.PlayerUID)
                {
                    player.Disconnect("[Shed Security] Server is restarting. Saving data...");
                }
            }

            sapi.Event.RegisterCallback((dt) =>
            {
                sapi.InjectConsole("/stop");
            }, 1000);

            return TextCommandResult.Success("Restart sequence initiated. Kicking players...");
        }

        private TextCommandResult OnCmdMute(TextCommandCallingArgs args)
        {
            AuditAdminAction(args, "/shed mute");
            string name = args.Parsers[0].GetValue() as string;
            int minutes = (args.Parsers[1].GetValue() as int?) ?? 0;
            if (minutes <= 0) minutes = 5;

            IServerPlayer player = sapi.World.AllOnlinePlayers.FirstOrDefault(p => p.PlayerName == name) as IServerPlayer;
            if (player == null) return TextCommandResult.Error("Player not found online.");

            // Use UTC ticks for expiry so mutes survive server restarts with correct timing
            long expiry = DateTime.UtcNow.Ticks + ((long)minutes * 60 * TimeSpan.TicksPerSecond);
            mutedPlayers[player.PlayerUID] = expiry;
            SaveMutes();

            player.SendMessage(GlobalConstants.GeneralChatGroup, $"[Shed Security] 🔇 You have been MUTED for {minutes} minutes.", EnumChatType.Notification);
            LogSecurity($"Admin muted {name} for {minutes} minutes.");

            return TextCommandResult.Success($"Muted {name} for {minutes}m.");
        }

        private TextCommandResult OnCmdUnmute(TextCommandCallingArgs args)
        {
            AuditAdminAction(args, "/shed unmute");
            string name = args.Parsers[0].GetValue() as string;
            IServerPlayer player = sapi.World.AllOnlinePlayers.FirstOrDefault(p => p.PlayerName == name) as IServerPlayer;

            if (player == null) return TextCommandResult.Error("Player not found online.");

            if (mutedPlayers.ContainsKey(player.PlayerUID))
            {
                mutedPlayers.Remove(player.PlayerUID);
                SaveMutes();
                player.SendMessage(GlobalConstants.GeneralChatGroup, "[Shed Security] 🔊 You have been UNMUTED.", EnumChatType.Notification);
                LogSecurity($"Admin unmuted {name}.");
                return TextCommandResult.Success($"Unmuted {name}.");
            }
            return TextCommandResult.Error($"{name} is not muted.");
        }

        private TextCommandResult OnCmdWipeInv(TextCommandCallingArgs args)
        {
            AuditAdminAction(args, "/shed wipeinv");
            string name = args.Parsers[0].GetValue() as string;
            IServerPlayer player = sapi.World.AllOnlinePlayers.FirstOrDefault(p => p.PlayerName == name) as IServerPlayer;
            if (player == null) return TextCommandResult.Error("Player not found online.");

            void Wipe(string invClassName)
            {
                IInventory inv = player.InventoryManager.GetOwnInventory(invClassName);
                if (inv != null)
                {
                    for (int i = 0; i < inv.Count; i++)
                    {
                        inv[i].Itemstack = null;
                        inv[i].MarkDirty();
                    }
                }
            }

            Wipe(GlobalConstants.hotBarInvClassName);
            Wipe(GlobalConstants.backpackInvClassName);
            Wipe(GlobalConstants.characterInvClassName);

            LogSecurity($"Admin WIPED INVENTORY of {name}.");
            return TextCommandResult.Success($"Inventory wiped for {name}.");
        }

        private TextCommandResult OnCmdWhoIs(TextCommandCallingArgs args)
        {
            AuditAdminAction(args, "/shed whois");
            string name = args.Parsers[0].GetValue() as string;
            IServerPlayer player = sapi.World.AllOnlinePlayers.FirstOrDefault(p => p.PlayerName == name) as IServerPlayer;
            if (player == null) return TextCommandResult.Error("Player not found online.");

            string ip = player.IpAddress;
            // Only expose the first two octets — enough for geo-lookup but not enough to ID someone
            string maskedIp = "Unknown";
            if (!string.IsNullOrEmpty(ip) && ip.Contains("."))
            {
                string[] parts = ip.Split('.');
                if (parts.Length >= 2)
                {
                    maskedIp = $"{parts[0]}.{parts[1]}.x.x";
                }
            }

            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"\n👮 [WhoIs] Report for: {player.PlayerName}");
            sb.AppendLine($"--------------------------------");
            sb.AppendLine($"IP Address: {maskedIp}");
            sb.AppendLine($"Ping: {player.Ping}ms");
            sb.AppendLine($"Gamemode: {player.WorldData.CurrentGameMode}");
            sb.AppendLine($"Health: {player.Entity.Alive} | Pos: {EntityPosAccessor.GetPos(player.Entity).AsBlockPos}");

            if (violationStrikes.ContainsKey(player.PlayerUID))
                sb.AppendLine($"Strikes: {violationStrikes[player.PlayerUID].Count}");
            else
                sb.AppendLine($"Strikes: 0");

            return TextCommandResult.Success(sb.ToString());
        }

        private TextCommandResult OnCmdKickAll(TextCommandCallingArgs args)
        {
            AuditAdminAction(args, "/shed kickall");
            IPlayer admin = args.Caller.Player;
            int count = 0;

            foreach (IServerPlayer player in sapi.World.AllOnlinePlayers)
            {
                if (admin != null && player.PlayerUID == admin.PlayerUID) continue;
                player.Disconnect("[Shed Security] Server is restarting for maintenance. Please wait.");
                count++;
            }

            LogSecurity($"Admin executed /kickall. Removed {count} players.");
            return TextCommandResult.Success($"Kicked {count} players. You are the last one remaining.");
        }

        private TextCommandResult OnCmdVanish(TextCommandCallingArgs args)
        {
            AuditAdminAction(args, "/shed vanish");
            IServerPlayer player = args.Caller.Player as IServerPlayer;

            if (player == null) return TextCommandResult.Error("You must be a player to use this.");

            if (vanishedPlayers.ContainsKey(player.PlayerUID))
            {
                // Restore the game mode they had before vanishing
                EnumGameMode originalMode = vanishedPlayers[player.PlayerUID];
                vanishedPlayers.Remove(player.PlayerUID);
                SaveState();

                player.WorldData.CurrentGameMode = originalMode;
                player.BroadcastPlayerData(true);

                ShowPlayerEntity(player);

                string joinMsg = $"Player {player.PlayerName} joined the game";
                sapi.SendMessageToGroup(GlobalConstants.GeneralChatGroup, joinMsg, EnumChatType.Notification);

                return TextCommandResult.Success("You are now VISIBLE.");
            }
            else
            {
                // Remember their current mode so we can put them back later
                vanishedPlayers[player.PlayerUID] = player.WorldData.CurrentGameMode;
                SaveState();

                player.WorldData.CurrentGameMode = EnumGameMode.Spectator;
                player.BroadcastPlayerData(true);

                HidePlayerEntity(player);

                string leaveMsg = $"Player {player.PlayerName} left the game";
                sapi.SendMessageToGroup(GlobalConstants.GeneralChatGroup, leaveMsg, EnumChatType.Notification);

                return TextCommandResult.Success("You are now VANISHED. Hidden from /list and player lists.");
            }
        }

        private TextCommandResult OnCmdInvSee(TextCommandCallingArgs args)
        {
            AuditAdminAction(args, "/shed invsee");
            string name = args.Parsers[0].GetValue() as string;
            IServerPlayer target = sapi.World.AllOnlinePlayers.FirstOrDefault(p => p.PlayerName == name) as IServerPlayer;

            if (target == null) return TextCommandResult.Error("Player not found online.");

            StringBuilder report = new StringBuilder();
            report.AppendLine($"--- Hotbar of {target.PlayerName} ---");

            IInventory inv = target.InventoryManager.GetOwnInventory(GlobalConstants.hotBarInvClassName);

            if (inv != null)
            {
                for (int i = 0; i < inv.Count; i++)
                {
                    ItemSlot slot = inv[i];
                    if (!slot.Empty)
                    {
                        report.AppendLine($"[{i}] x{slot.StackSize} {slot.GetStackName()}");
                    }
                }
            }
            else
            {
                report.AppendLine("Could not access inventory.");
            }

            return TextCommandResult.Success(report.ToString());
        }

        private TextCommandResult OnCmdClear(TextCommandCallingArgs args)
        {
            AuditAdminAction(args, "/shed clear");
            string name = args.Parsers[0].GetValue() as string;
            IServerPlayer player = sapi.World.AllOnlinePlayers.FirstOrDefault(p => p.PlayerName == name) as IServerPlayer;

            if (player == null) return TextCommandResult.Error("Player not found online.");

            if (violationStrikes.ContainsKey(player.PlayerUID))
            {
                violationStrikes[player.PlayerUID].Count = 0;
                SaveStrikes();
            }
            if (shadowBannedPlayers.Contains(player.PlayerUID)) shadowBannedPlayers.Remove(player.PlayerUID);
            SaveState();

            LogSecurity($"Admin cleared strikes/bans for {player.PlayerName}.");
            return TextCommandResult.Success($"[Shed Security] Cleared strikes and shadowban for {player.PlayerName}.");
        }

        private TextCommandResult OnCmdShadowban(TextCommandCallingArgs args)
        {
            AuditAdminAction(args, "/shed shadowban");
            string name = args.Parsers[0].GetValue() as string;
            IServerPlayer player = sapi.World.AllOnlinePlayers.FirstOrDefault(p => p.PlayerName == name) as IServerPlayer;

            if (player == null) return TextCommandResult.Error("Player not found online.");

            if (shadowBannedPlayers.Contains(player.PlayerUID))
            {
                shadowBannedPlayers.Remove(player.PlayerUID);
                SaveState();
                LogSecurity($"Admin un-shadowbanned {player.PlayerName}.");
                return TextCommandResult.Success($"[Shed Security] Un-shadowbanned {player.PlayerName}.");
            }
            else
            {
                shadowBannedPlayers.Add(player.PlayerUID);
                SaveState();
                LogSecurity($"Admin SHADOWBANNED {player.PlayerName}.");
                return TextCommandResult.Success($"[Shed Security] Shadowbanned {player.PlayerName}. They deal 0 damage now.");
            }
        }

        private TextCommandResult OnCmdFreeze(TextCommandCallingArgs args)
        {
            AuditAdminAction(args, "/shed freeze");
            string name = args.Parsers[0].GetValue() as string;
            IServerPlayer player = sapi.World.AllOnlinePlayers.FirstOrDefault(p => p.PlayerName == name) as IServerPlayer;

            if (player == null) return TextCommandResult.Error("Player not found online.");

            if (frozenPlayers.Contains(player.PlayerUID))
            {
                frozenPlayers.Remove(player.PlayerUID);
                SaveState();
                player.SendMessage(GlobalConstants.GeneralChatGroup, "[Shed Security] You have been unfrozen.", EnumChatType.Notification);
                LogSecurity($"Admin unfroze {player.PlayerName}.");
                return TextCommandResult.Success($"[Shed Security] Unfroze {player.PlayerName}.");
            }
            else
            {
                frozenPlayers.Add(player.PlayerUID);
                SaveState();
                player.SendMessage(GlobalConstants.GeneralChatGroup, "[Shed Security] ⚠ YOU HAVE BEEN FROZEN BY ADMIN. DO NOT LOG OUT.", EnumChatType.Notification);
                LogSecurity($"Admin FROZE {player.PlayerName}.");
                return TextCommandResult.Success($"[Shed Security] Froze {player.PlayerName}.");
            }
        }

        private TextCommandResult OnCmdStatus(TextCommandCallingArgs args)
        {
            AuditAdminAction(args, "/shed status");

            string report = statusReportFeature.BuildStatusReport(
                ModVersion,
                sapi.World.AllOnlinePlayers.Count(),
                frozenPlayers.Count,
                shadowBannedPlayers.Count,
                vanishedPlayers.Count,
                mutedPlayers.Count,
                combatTaggedUntil.Values.Count(v => v > sapi.World.ElapsedMilliseconds),
                violationStrikes.Values.Sum(v => v.Count),
                config
            );

            return TextCommandResult.Success(report);
        }

        private TextCommandResult OnCmdStrikes(TextCommandCallingArgs args)
        {
            AuditAdminAction(args, "/shed strikes");
            return TextCommandResult.Success(statusReportFeature.BuildStrikesReport(violationStrikes));
        }

        private TextCommandResult OnCmdStaffChat(TextCommandCallingArgs args)
        {
            string message = args.RawArgs.PopAll();

            if (string.IsNullOrEmpty(message))
                return TextCommandResult.Error("Usage: /shedchat [message]");

            string format = $"🔒 [STAFF] {args.Caller.Player.PlayerName}: {message}";

            foreach (IServerPlayer p in sapi.World.AllOnlinePlayers)
            {
                if (p.HasPrivilege(Privilege.controlserver))
                {
                    p.SendMessage(GlobalConstants.GeneralChatGroup, format, EnumChatType.Notification);
                }
            }

            return TextCommandResult.Success();
        }

        private TextCommandResult OnCmdAlert(TextCommandCallingArgs args)
        {
            AuditAdminAction(args, "/shed alert");
            string message = args.Parsers[0].GetValue() as string;
            if (string.IsNullOrEmpty(message)) message = args.RawArgs.PopAll();

            if (string.IsNullOrEmpty(message)) return TextCommandResult.Error("Usage: /shed alert [message]");

            string broadcast = $"📢 [SERVER ALERT] {message}";
            sapi.SendMessageToGroup(GlobalConstants.GeneralChatGroup, broadcast, EnumChatType.Notification);

            return TextCommandResult.Success("Alert sent.");
        }

        private TextCommandResult OnCmdReport(TextCommandCallingArgs args)
        {
            string fullInput = args.RawArgs.PopAll();

            if (string.IsNullOrWhiteSpace(fullInput))
                return TextCommandResult.Error("Usage: /sreport [player] [reason]");

            string[] parts = fullInput.Trim().Split(new char[] { ' ' }, 2);

            if (parts.Length < 2)
                return TextCommandResult.Error("Usage: /sreport [player] [reason] (Reason is required!)");

            string targetName = parts[0];
            string reason = parts[1];
            string reporter = args.Caller.Player?.PlayerName ?? "Server Console";

            string msg = $"🚨 REPORT: {reporter} reported {targetName} for: {reason}";
            LogSecurity(msg);
            SendDiscordAlert(msg);

            foreach (IServerPlayer p in sapi.World.AllOnlinePlayers)
            {
                if (p.HasPrivilege(Privilege.controlserver))
                    p.SendMessage(GlobalConstants.InfoLogChatGroup, msg, EnumChatType.Notification);
            }

            return TextCommandResult.Success("Report sent to admins.");
        }

        private TextCommandResult OnCmdListOverride(TextCommandCallingArgs args)
        {
            IPlayer caller = args.Caller.Player;
            bool isCallerAdmin = caller != null && caller.HasPrivilege(Privilege.controlserver);

            List<string> visiblePlayers = new List<string>();

            foreach (IServerPlayer p in sapi.World.AllOnlinePlayers)
            {
                if (vanishedPlayers.ContainsKey(p.PlayerUID) && !isCallerAdmin)
                {
                    continue;
                }

                string name = p.PlayerName;
                if (vanishedPlayers.ContainsKey(p.PlayerUID)) name += " [VANISHED]";

                visiblePlayers.Add(name);
            }

            string report = $"Online Players ({visiblePlayers.Count}): {string.Join(", ", visiblePlayers)}";
            return TextCommandResult.Success(report);
        }
    }
}

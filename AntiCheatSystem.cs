using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace ServerAntiCheat
{
    public partial class AntiCheatSystem : ModSystem
    {
        private const string ModVersion = "1.4.9";
        private string latestVersion = null;
        private string updateUrl = "https://mods.vintagestory.at/api/mod/shedsecurity";

        private ICoreServerAPI sapi;
        private AntiCheatConfig config;

        // Each feature is its own class so the main partial stays focused on orchestration
        private readonly StatePersistenceFeature statePersistence = new StatePersistenceFeature();
        private readonly LogMaintenanceFeature logMaintenance = new LogMaintenanceFeature();
        private readonly PlayerOverrideFeature playerOverrideFeature = new PlayerOverrideFeature();
        private readonly CombatLogFeature combatLogFeature = new CombatLogFeature();
        private readonly OreVeinFeature oreVeinFeature = new OreVeinFeature();
        private readonly StealthNetworkFeature stealthNetworkFeature = new StealthNetworkFeature();
        private readonly StatusReportFeature statusReportFeature = new StatusReportFeature();

        private string logFilePath;
        private string strikesFilePath;
        private string mutesFilePath;
        private string stateFilePath;

        // --- Per-player tracking state (keyed by PlayerUID) ---
        // All of these get cleaned up in OnPlayerDisconnect so we don't leak memory.

        private Dictionary<string, EntityPos> lastPositions = new Dictionary<string, EntityPos>();
        private Dictionary<string, long> movementGracePeriod = new Dictionary<string, long>();
        private Dictionary<string, long> loginGracePeriod = new Dictionary<string, long>();

        private Dictionary<string, long> lastChatTime = new Dictionary<string, long>();
        private Dictionary<string, string> lastChatMessage = new Dictionary<string, string>();

        private Dictionary<string, int> breakCount = new Dictionary<string, int>();
        private Dictionary<string, int> placeCount = new Dictionary<string, int>();
        private Dictionary<string, MiningSession> miningSessions = new Dictionary<string, MiningSession>();
        private Dictionary<string, PlayerMiningStats> playerStats = new Dictionary<string, PlayerMiningStats>();
        private Dictionary<string, List<OreBreakPoint>> recentRareOreBreaks = new Dictionary<string, List<OreBreakPoint>>();

        private Dictionary<string, float> lastKnownHealth = new Dictionary<string, float>();
        private Dictionary<string, long> combatTaggedUntil = new Dictionary<string, long>();
        // Briefly suppresses speed checks after a player takes damage so mob knockback doesn't false-flag
        private Dictionary<string, long> knockbackGracePeriod = new Dictionary<string, long>();

        // --- Moderation state (persisted across restarts via shed-state.json) ---
        private Dictionary<string, long> mutedPlayers = new Dictionary<string, long>();
        private Dictionary<string, long> joinHistory = new Dictionary<string, long>();
        private Dictionary<string, StrikeInfo> violationStrikes = new Dictionary<string, StrikeInfo>();
        private HashSet<string> frozenPlayers = new HashSet<string>();
        private HashSet<string> shadowBannedPlayers = new HashSet<string>();
        // Stores the player's original game mode so we can restore it when they un-vanish
        private Dictionary<string, EnumGameMode> vanishedPlayers = new Dictionary<string, EnumGameMode>();

        public class StrikeInfo
        {
            public string Username { get; set; }
            public int Count { get; set; }
        }

        private class MiningSession
        {
            public string OreName;
            public int Count;
            public long ReportTime;
        }

        private class PlayerMiningStats
        {
            public int StoneMined;
            public int OresMined;
            public long LastResetTime;
        }

        public override bool ShouldLoad(EnumAppSide forSide)
        {
            return forSide == EnumAppSide.Server;
        }

        public override void StartServerSide(ICoreServerAPI api)
        {
            this.sapi = api;

            // Must happen before anything touches Entity.Pos — builds a compiled
            // delegate that works whether Pos is a field (1.19) or property (1.20+)
            EntityPosAccessor.Init();
            LoadConfig();

            strikesFilePath = Path.Combine(api.GetOrCreateDataPath("ModData"), "shed-strikes.json");
            LoadStrikes();
            mutesFilePath = Path.Combine(api.GetOrCreateDataPath("ModData"), "shed-mutes.json");
            LoadMutes();
            stateFilePath = Path.Combine(api.GetOrCreateDataPath("ModData"), "shed-state.json");
            LoadState();

            logFilePath = Path.Combine(api.GetOrCreateDataPath("Logs"), "shed-security.log");
            LogSecurity($"Shed Security Started!. Version {ModVersion}");
            api.Logger.Notification("Shed Security Active: Admin Tools & Anti-Cheat");

            CheckForUpdates();

            // Movement checks run every 500ms; action count resets every 1s (nuker/printer)
            api.Event.RegisterGameTickListener(OnCheckPlayerMovement, 500);
            api.Event.RegisterGameTickListener(OnResetActionCounts, 1000);

            api.Event.BreakBlock += OnPlayerBreakBlock;
            api.Event.DidPlaceBlock += OnPlayerPlaceBlock;
            api.Event.PlayerChat += OnPlayerChat;
            api.Event.PlayerJoin += OnPlayerJoin;
            api.Event.PlayerDisconnect += OnPlayerDisconnect;

            RegisterCommands();
        }

        private void RegisterCommands()
        {
            sapi.ChatCommands.Create("shed")
                .WithDescription("Shed Security Admin Tools")
                .RequiresPrivilege(Privilege.controlserver)
                .BeginSubCommand("reload").HandleWith(OnCmdReload).EndSubCommand()
                .BeginSubCommand("clear").WithArgs(sapi.ChatCommands.Parsers.Word("playername")).HandleWith(OnCmdClear).EndSubCommand()
                .BeginSubCommand("shadowban").WithArgs(sapi.ChatCommands.Parsers.Word("playername")).HandleWith(OnCmdShadowban).EndSubCommand()
                .BeginSubCommand("freeze").WithArgs(sapi.ChatCommands.Parsers.Word("playername")).HandleWith(OnCmdFreeze).EndSubCommand()
                .BeginSubCommand("invsee").WithArgs(sapi.ChatCommands.Parsers.Word("playername")).HandleWith(OnCmdInvSee).EndSubCommand()
                .BeginSubCommand("alert").WithArgs(sapi.ChatCommands.Parsers.Unparsed("message")).HandleWith(OnCmdAlert).EndSubCommand()
                .BeginSubCommand("mute").WithArgs(sapi.ChatCommands.Parsers.Word("player"), sapi.ChatCommands.Parsers.OptionalInt("minutes")).HandleWith(OnCmdMute).EndSubCommand()
                .BeginSubCommand("unmute").WithArgs(sapi.ChatCommands.Parsers.Word("player")).HandleWith(OnCmdUnmute).EndSubCommand()
                .BeginSubCommand("wipeinv").WithArgs(sapi.ChatCommands.Parsers.Word("player")).HandleWith(OnCmdWipeInv).EndSubCommand()
                .BeginSubCommand("whois").WithArgs(sapi.ChatCommands.Parsers.Word("player")).HandleWith(OnCmdWhoIs).EndSubCommand()
                .BeginSubCommand("vanish").HandleWith(OnCmdVanish).EndSubCommand()
                .BeginSubCommand("kickall").HandleWith(OnCmdKickAll).EndSubCommand()
                .BeginSubCommand("restart").HandleWith(OnCmdRestart).EndSubCommand()
                .BeginSubCommand("status").HandleWith(OnCmdStatus).EndSubCommand()
                .BeginSubCommand("strikes").HandleWith(OnCmdStrikes).EndSubCommand();

            sapi.ChatCommands.Create("shedchat")
                .WithDescription("Private Staff Chat")
                .RequiresPrivilege(Privilege.controlserver)
                .WithArgs(sapi.ChatCommands.Parsers.Unparsed("message"))
                .HandleWith(OnCmdStaffChat);

            sapi.ChatCommands.Create("sreport")
                .WithDescription("Report a player")
                .RequiresPrivilege(Privilege.chat)
                .WithArgs(sapi.ChatCommands.Parsers.Unparsed("report_details"))
                .HandleWith(OnCmdReport);

            sapi.ChatCommands.Create("online")
                .WithDescription("Shows online players (hides vanished)")
                .RequiresPrivilege(Privilege.chat)
                .HandleWith(OnCmdListOverride);
        }

        // Tear down every per-player tracking entry so we don't leak memory.
        // Also checks for combat-log violations (disconnecting mid-fight).
        private void OnPlayerDisconnect(IServerPlayer player)
        {
            string uid = player.PlayerUID;

            if (playerStats.ContainsKey(uid)) playerStats.Remove(uid);
            if (lastPositions.ContainsKey(uid)) lastPositions.Remove(uid);
            if (lastChatTime.ContainsKey(uid)) lastChatTime.Remove(uid);
            if (breakCount.ContainsKey(uid)) breakCount.Remove(uid);
            if (placeCount.ContainsKey(uid)) placeCount.Remove(uid);
            if (loginGracePeriod.ContainsKey(uid)) loginGracePeriod.Remove(uid);
            // Flush any pending ore alert before removing the session
            if (miningSessions.ContainsKey(uid)) { FlushMiningSession(player, miningSessions[uid]); miningSessions.Remove(uid); }
            if (movementGracePeriod.ContainsKey(uid)) movementGracePeriod.Remove(uid);
            if (lastChatMessage.ContainsKey(uid)) lastChatMessage.Remove(uid);
            if (lastKnownHealth.ContainsKey(uid)) lastKnownHealth.Remove(uid);
            if (knockbackGracePeriod.ContainsKey(uid)) knockbackGracePeriod.Remove(uid);
            if (recentRareOreBreaks.ContainsKey(uid)) recentRareOreBreaks.Remove(uid);
            if (joinHistory.ContainsKey(uid)) joinHistory.Remove(uid);

            // If they quit while combat tagged, that's a combat log — flag it
            if (combatTaggedUntil.ContainsKey(uid) && combatTaggedUntil[uid] > sapi.World.ElapsedMilliseconds)
            {
                string msg = $"\u2694\ufe0f COMBAT LOG: {player.PlayerName} disconnected while combat tagged.";
                LogSecurity(msg);
                SendDiscordAlert(msg);
            }

            if (combatTaggedUntil.ContainsKey(uid)) combatTaggedUntil.Remove(uid);
        }

        private void LogSecurity(string message)
        {
            sapi.Logger.Notification($"[Shed Security] {message}");
            try
            {
                RotateLogIfNeeded();
                string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                File.AppendAllText(logFilePath, $"[{timestamp}] {message}{Environment.NewLine}");
            }
            catch { }
        }

        // Loads the config from disk, backfills missing fields with sane defaults,
        // and writes it back out if anything changed. Server owners can delete
        // individual keys and they'll get re-populated on the next load.
        private void LoadConfig()
        {
            try
            {
                config = sapi.LoadModConfig<AntiCheatConfig>("anticheat-config.json");

                if (config == null)
                {
                    config = new AntiCheatConfig();
                    config.ConfigVersion = ModVersion;

                    config.EnableAutoKick = false;
                    config.EnableSpeedChecks = true;
                    config.EnableNoClipCheck = true;
                    config.EnableAntiNuker = true;
                    config.EnableAntiPrinter = true;
                    config.OreAlertBatchSeconds = 5.0;

                    config.RareOres = new List<string> {
                        "nativegold", "nativesilver", "meteoriciron", "cassiterite",
                        "bismuthinite", "borax", "quartz", "chromite",
                        "nativecopper", "malachite", "tetrahedrite",
                        "hematite", "magnetite", "limonite",
                        "lignite", "bituminouscoal", "anthracite"
                    };
                    config.DenseOres = new List<string> {
                        "quartz",
                        "nativecopper", "malachite", "tetrahedrite",
                        "hematite", "magnetite", "limonite",
                        "lignite", "bituminouscoal", "anthracite"
                    };
                    config.BannedWords = new List<string> { "badword1", "cheat", "hack" };
                    config.BlacklistedClientMods = new List<string> { "blockoverlay", "xray", "freecam" };

                    sapi.StoreModConfig(config, "anticheat-config.json");
                    return;
                }

                bool dirty = false;

                if (config.OreAlertBatchSeconds < 0) { config.OreAlertBatchSeconds = 5.0; dirty = true; }
                if (config.ConfigVersion != ModVersion) { config.ConfigVersion = ModVersion; dirty = true; }

                if (config.RareOres == null || config.RareOres.Count == 0)
                {
                    config.RareOres = new List<string> { "nativegold", "nativesilver", "meteoriciron", "cassiterite", "bismuthinite", "borax", "quartz", "chromite" };
                    dirty = true;
                }
                else
                {
                    int countBefore = config.RareOres.Count;
                    config.RareOres = config.RareOres.Distinct().ToList();
                    if (config.RareOres.Count != countBefore) dirty = true;
                }

                if (config.BannedWords == null || config.BannedWords.Count == 0)
                {
                    config.BannedWords = new List<string> { "badword1", "cheat", "hack" };
                    dirty = true;
                }
                else
                {
                    int countBefore = config.BannedWords.Count;
                    config.BannedWords = config.BannedWords.Distinct().ToList();
                    if (config.BannedWords.Count != countBefore) dirty = true;
                }

                if (config.DenseOres == null || config.DenseOres.Count == 0)
                {
                    config.DenseOres = new List<string> { "quartz", "coal", "iron", "copper" };
                    dirty = true;
                }

                if (config.BlacklistedClientMods == null || config.BlacklistedClientMods.Count == 0)
                {
                    config.BlacklistedClientMods = new List<string> { "blockoverlay", "xray", "freecam" };
                    dirty = true;
                }

                if (config.ChatCooldownMs <= 0) { config.ChatCooldownMs = 1000; dirty = true; }
                if (config.MaxBlockReach <= 0) { config.MaxBlockReach = 6.5; dirty = true; }
                if (config.MaxLogFileSizeMB < 1.0) { config.MaxLogFileSizeMB = 5.0; dirty = true; }
                if (config.MaxLogBackups <= 0) { config.MaxLogBackups = 3; dirty = true; }
                if (config.CombatTagSeconds <= 0) { config.CombatTagSeconds = 20; dirty = true; }
                if (config.TeleportGraceMs < 0) { config.TeleportGraceMs = 3000; dirty = true; }
                if (config.TeleportGraceDistance <= 0) { config.TeleportGraceDistance = 24.0; dirty = true; }
                if (config.OreVeinWindowSeconds <= 0) { config.OreVeinWindowSeconds = 180; dirty = true; }
                if (config.OreVeinMinDistance <= 0) { config.OreVeinMinDistance = 10.0; dirty = true; }
                if (config.OreVeinDistinctVeinsAlert <= 1) { config.OreVeinDistinctVeinsAlert = 4; dirty = true; }
                if (config.OreVeinMinimumRareOres <= 1) { config.OreVeinMinimumRareOres = 8; dirty = true; }
                if (config.PlayerOverrides == null) { config.PlayerOverrides = new Dictionary<string, PlayerOverrideConfig>(); dirty = true; }

                if (dirty)
                {
                    sapi.StoreModConfig(config, "anticheat-config.json");
                    LogSecurity($"Configuration file auto-updated to v{ModVersion}");
                }
            }
            catch
            {
                config = new AntiCheatConfig();
                config.ConfigVersion = ModVersion;
                config.OreAlertBatchSeconds = 5.0;
                config.RareOres = new List<string> { "nativegold", "nativesilver", "meteoriciron", "cassiterite", "bismuthinite", "borax", "quartz", "chromite" };
                config.DenseOres = new List<string> { "quartz", "coal", "iron", "copper" };
                config.BannedWords = new List<string> { "badword1", "cheat", "hack" };
                config.BlacklistedClientMods = new List<string> { "blockoverlay", "xray", "freecam" };
                config.ChatCooldownMs = 1000;
                config.MaxBlockReach = 6.5;
                config.MaxLogFileSizeMB = 5.0;
                config.MaxLogBackups = 3;
                config.EnableCombatLog = true;
                config.CombatTagSeconds = 20;
                config.TeleportGraceMs = 3000;
                config.TeleportGraceDistance = 24.0;
                config.EnableOreVeinFingerprint = true;
                config.OreVeinWindowSeconds = 180;
                config.OreVeinMinDistance = 10.0;
                config.OreVeinDistinctVeinsAlert = 4;
                config.OreVeinMinimumRareOres = 8;
                config.EnableAdminAuditLog = true;
                config.PlayerOverrides = new Dictionary<string, PlayerOverrideConfig>();
                sapi.StoreModConfig(config, "anticheat-config.json");
            }
        }

        // Hits the ModDB API in the background to see if there's a newer version.
        // If there is, we stash it in latestVersion so admins get a notification on join.
        private void CheckForUpdates()
        {
            Task.Run(async () =>
            {
                try
                {
                    using (HttpClient client = new HttpClient())
                    {
                        string json = await client.GetStringAsync(updateUrl);

                        using (JsonDocument doc = JsonDocument.Parse(json))
                        {
                            if (doc.RootElement.TryGetProperty("mod", out JsonElement modElement) &&
                                modElement.TryGetProperty("modversion", out JsonElement versionElement))
                            {
                                string webVersion = versionElement.GetString();
                                if (!string.IsNullOrEmpty(webVersion))
                                {
                                    Version vWeb = new Version(webVersion);
                                    Version vLocal = new Version(ModVersion);

                                    if (vWeb > vLocal)
                                    {
                                        latestVersion = webVersion;
                                        sapi.Logger.Notification($"[Shed Security] Update Available! v{webVersion} (Current: v{ModVersion})");
                                    }
                                }
                            }
                        }
                    }
                }
                catch { }
            });
        }
    }
}

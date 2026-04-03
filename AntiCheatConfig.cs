using System.Collections.Generic;

namespace ServerAntiCheat
{
    // Per-player limit overrides. Any field left null falls through to the global config.
    public class PlayerOverrideConfig
    {
        public double? MaxHorizontalSpeed;
        public double? MaxVerticalSpeed;
        public int? MaxStrikesBeforeKick;
        public int? MaxBlocksBrokenPerSecond;
        public int? MaxBlocksPlacedPerSecond;
        public int? ChatCooldownMs;
        public double? MaxBlockReach;
    }

    public class AntiCheatConfig
    {
        public string ConfigVersion = "";

        // Feature toggles
        public bool EnableHeuristics = true;
        public bool EnableAutoKick = false;
        public bool EnableSpeedChecks = true;
        public bool EnableNoClipCheck = true;
        public bool EnableAntiNuker = true;
        public bool EnableAntiPrinter = true;
        public double OreAlertBatchSeconds = -1.0;

        // X-Ray heuristic thresholds
        public double XRayRatioThreshold = 0.15;
        public int MinBlocksForRatio = 100;

        // Movement and action limits
        public double MaxHorizontalSpeed = 6.0;
        public double MaxVerticalSpeed = 2.5;
        public int MaxStrikesBeforeKick = 10;
        public int MaxBlocksBrokenPerSecond = 75;
        public int MaxBlocksPlacedPerSecond = 8;
        public double MinServerTPS = 15.0;
        public double MaxBlockReach = 6.5;

        // Discord webhook integration
        public string DiscordWebhookUrl = "";
        public bool EnableDiscordAlerts = false;

        // Lists (populated with defaults on first load)
        public List<string> RareOres = new List<string>();
        public List<string> DenseOres = new List<string>();
        public List<string> BannedWords = new List<string>();
        public List<string> BlacklistedClientMods = new List<string>();

        // Chat and log management
        public int ChatCooldownMs = 1000;
        public double MaxLogFileSizeMB = 5.0;
        public int MaxLogBackups = 3;

        // Combat tagging, teleport grace, and ore vein fingerprinting
        public bool EnableCombatLog = true;
        public int CombatTagSeconds = 20;
        public int TeleportGraceMs = 3000;
        public double TeleportGraceDistance = 24.0;
        public bool EnableOreVeinFingerprint = true;
        public int OreVeinWindowSeconds = 180;
        public double OreVeinMinDistance = 10.0;
        public int OreVeinDistinctVeinsAlert = 4;
        public int OreVeinMinimumRareOres = 8;

        // Audit log and per-player limit overrides
        public bool EnableAdminAuditLog = true;
        public Dictionary<string, PlayerOverrideConfig> PlayerOverrides = new Dictionary<string, PlayerOverrideConfig>();
    }
}

using Vintagestory.API.Server;

namespace ServerAntiCheat
{
    // Resolves per-player config overrides by checking UID first, then
    // player name (case-sensitive then lowercase). Returns null if no
    // override exists, so callers fall back to global defaults.
    public class PlayerOverrideFeature
    {
        public PlayerOverrideConfig Resolve(AntiCheatConfig config, IServerPlayer player)
        {
            if (config.PlayerOverrides == null || config.PlayerOverrides.Count == 0) return null;

            if (config.PlayerOverrides.TryGetValue(player.PlayerUID, out var byUid)) return byUid;
            if (config.PlayerOverrides.TryGetValue(player.PlayerName, out var byName)) return byName;
            if (config.PlayerOverrides.TryGetValue(player.PlayerName.ToLowerInvariant(), out var byLower)) return byLower;
            return null;
        }
    }
}

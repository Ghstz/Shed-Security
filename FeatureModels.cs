using Vintagestory.API.MathTools;

namespace ServerAntiCheat
{
    public class OreBreakPoint
    {
        public BlockPos Pos;
        public long Time;
    }

    public class PersistedState
    {
        public System.Collections.Generic.List<string> ShadowBanned { get; set; } = new System.Collections.Generic.List<string>();
        public System.Collections.Generic.List<string> Frozen { get; set; } = new System.Collections.Generic.List<string>();
        public System.Collections.Generic.Dictionary<string, int> VanishedPlayers { get; set; } = new System.Collections.Generic.Dictionary<string, int>();
    }
}

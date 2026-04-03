using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ServerAntiCheat
{
    public class StatusReportFeature
    {
        public string BuildStatusReport(
            string modVersion,
            int online,
            int frozen,
            int shadowbanned,
            int vanished,
            int muted,
            int combatTagged,
            int totalStrikes,
            AntiCheatConfig config
        )
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"Shed Security v{modVersion}");
            sb.AppendLine($"Online: {online} | Strikes(total): {totalStrikes}");
            sb.AppendLine($"Frozen: {frozen} | Shadowbanned: {shadowbanned} | Vanished: {vanished}");
            sb.AppendLine($"Muted: {muted} | CombatTagged: {combatTagged}");
            sb.AppendLine($"Checks: Speed={config.EnableSpeedChecks}, NoClip={config.EnableNoClipCheck}, Nuker={config.EnableAntiNuker}, Printer={config.EnableAntiPrinter}, Heuristics={config.EnableHeuristics}");
            return sb.ToString();
        }

        public string BuildStrikesReport(Dictionary<string, AntiCheatSystem.StrikeInfo> strikes)
        {
            var top = strikes
                .Where(kv => kv.Value != null && kv.Value.Count > 0)
                .OrderByDescending(kv => kv.Value.Count)
                .Take(20)
                .ToList();

            if (top.Count == 0) return "No active strikes.";

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Top strike counts:");
            foreach (var kv in top)
            {
                string name = string.IsNullOrEmpty(kv.Value.Username) ? kv.Key : kv.Value.Username;
                sb.AppendLine($"- {name}: {kv.Value.Count}");
            }

            return sb.ToString();
        }
    }
}

using System;
using System.IO;

namespace ServerAntiCheat
{
    // Simple rolling-file rotation. When the log exceeds the configured size,
    // we shift existing backups up by one and move the current log to .1.
    public class LogMaintenanceFeature
    {
        public void Rotate(string logFilePath, double maxSizeMb, int maxBackups)
        {
            try
            {
                if (string.IsNullOrEmpty(logFilePath) || !File.Exists(logFilePath)) return;
                long maxBytes = (long)(Math.Max(1.0, maxSizeMb) * 1024 * 1024);
                FileInfo info = new FileInfo(logFilePath);
                if (info.Length < maxBytes) return;

                int backups = Math.Max(1, maxBackups);
                for (int i = backups; i >= 1; i--)
                {
                    string src = i == 1 ? logFilePath : logFilePath + "." + (i - 1);
                    string dst = logFilePath + "." + i;
                    if (File.Exists(dst)) File.Delete(dst);
                    if (File.Exists(src)) File.Move(src, dst);
                }
            }
            catch
            {
                // ignored
            }
        }
    }
}

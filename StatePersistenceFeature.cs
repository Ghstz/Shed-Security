using System.IO;
using System.Text.Json;

namespace ServerAntiCheat
{
    public class StatePersistenceFeature
    {
        public PersistedState Load(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                string json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<PersistedState>(json);
            }
            catch
            {
                return null;
            }
        }

        public void Save(string path, PersistedState state)
        {
            try
            {
                string json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(path, json);
            }
            catch
            {
                // ignored
            }
        }
    }
}

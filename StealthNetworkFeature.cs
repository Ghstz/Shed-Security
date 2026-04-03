using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Vintagestory.API.Server;

namespace ServerAntiCheat
{
    // Handles two things via reflection into VS internals:
    //  1. Mod scanning — reads the client's installed mod list from the
    //     ConnectedClient object and checks it against the blacklist.
    //  2. True vanish — removes/adds the admin's entity ID from other
    //     players' tracked-entity sets so their client never renders them.
    //
    // Heads up: this is all reflection-heavy because VS doesn't expose
    // these internals through the public API. Field names may change
    // between VS versions, so the code is written defensively.
    public class StealthNetworkFeature
    {
        // Digs into the player's ConnectedClient to pull out the list of
        // mods reported by their client, then checks each one against
        // the server's blacklist.
        public void ScanPlayerMods(
            ICoreServerAPI sapi,
            IServerPlayer player,
            List<string> blacklist,
            Action<string> logSecurity,
            Action<string> sendDiscordAlert,
            Action<string> notifyAdmins
        )
        {
            try
            {
                Type playerType = player.GetType();
                object clientObj = null;

                foreach (FieldInfo f in playerType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (f.FieldType.Name.Contains("ConnectedClient") ||
                        (f.Name.Equals("client", StringComparison.OrdinalIgnoreCase) && !f.FieldType.IsPrimitive))
                    {
                        clientObj = f.GetValue(player);
                        if (clientObj != null) break;
                    }
                }

                if (clientObj == null)
                {
                    sapi.Logger.Debug("[Shed Security] Mod scan: Could not locate ConnectedClient field.");
                    return;
                }

                // Walk the ConnectedClient's fields looking for anything
                // that looks like a mod list (field name contains "mod")
                List<string> clientModIds = new List<string>();

                foreach (FieldInfo f in clientObj.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (!f.Name.ToLower().Contains("mod")) continue;

                    object modsObj = f.GetValue(clientObj);
                    if (modsObj == null) continue;

                    if (modsObj is IEnumerable<string> strList)
                    {
                        foreach (string s in strList) clientModIds.Add(s.ToLower());
                    }
                    else if (modsObj is IEnumerable enumerable)
                    {
                        foreach (object item in enumerable)
                        {
                            if (item == null) continue;
                            string modId = null;

                            foreach (string name in new[] { "ModID", "modid", "ModId", "Id", "id" })
                            {
                                var prop = item.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                if (prop != null) { modId = prop.GetValue(item)?.ToString(); break; }

                                var fld = item.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                if (fld != null) { modId = fld.GetValue(item)?.ToString(); break; }
                            }

                            if (string.IsNullOrEmpty(modId)) modId = item.ToString();
                            if (!string.IsNullOrEmpty(modId)) clientModIds.Add(modId.ToLower());
                        }
                    }

                    if (clientModIds.Count > 0) break;
                }

                if (clientModIds.Count == 0)
                {
                    sapi.Logger.Debug($"[Shed Security] Mod scan: No client mods found for {player.PlayerName} (field structure may differ in this VS version).");
                    return;
                }

                foreach (string blacklisted in blacklist)
                {
                    string bl = blacklisted.ToLower();
                    foreach (string clientMod in clientModIds)
                    {
                        if (clientMod.Contains(bl))
                        {
                            string alertMsg = $"\u26a0\ufe0f [SOFT ANTI-CHEAT] {player.PlayerName} has blacklisted mod: {clientMod} (matched rule: {blacklisted})";
                            logSecurity(alertMsg);
                            sendDiscordAlert(alertMsg);
                            notifyAdmins(alertMsg);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                sapi.Logger.Debug($"[Shed Security] Mod scan reflection failed for {player.PlayerName}: {ex.Message}");
            }
        }

        // Removes the admin's entity from every other player's tracked
        // entity set so their client stops rendering them entirely
        public void HidePlayerEntity(ICoreServerAPI sapi, IServerPlayer admin)
        {
            foreach (IServerPlayer other in sapi.World.AllOnlinePlayers)
            {
                if (other.PlayerUID == admin.PlayerUID) continue;
                HidePlayerEntity(sapi, admin, other);
            }
        }

        // Targeted version: hide the admin from one specific viewer
        public void HidePlayerEntity(ICoreServerAPI sapi, IServerPlayer admin, IServerPlayer viewer)
        {
            try
            {
                long entityId = admin.Entity.EntityId;
                Type viewerType = viewer.GetType();
                object clientObj = null;

                foreach (FieldInfo f in viewerType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (f.FieldType.Name.Contains("ConnectedClient") ||
                        (f.Name.Equals("client", StringComparison.OrdinalIgnoreCase) && !f.FieldType.IsPrimitive))
                    {
                        clientObj = f.GetValue(viewer);
                        if (clientObj != null) break;
                    }
                }

                if (clientObj == null) return;

                foreach (FieldInfo f in clientObj.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    string fn = f.Name.ToLower();
                    if (!(fn.Contains("entit") || fn.Contains("tracked") || fn.Contains("loaded"))) continue;

                    object trackObj = f.GetValue(clientObj);
                    if (trackObj == null) continue;

                    if (trackObj is HashSet<long> hashSet) { hashSet.Remove(entityId); continue; }

                    var removeMethod = trackObj.GetType().GetMethod("Remove", new[] { typeof(long) });
                    if (removeMethod != null) { removeMethod.Invoke(trackObj, new object[] { entityId }); continue; }

                    var tryRemove = trackObj.GetType().GetMethod("TryRemove");
                    if (tryRemove != null)
                    {
                        var parms = tryRemove.GetParameters();
                        if (parms.Length >= 1 && parms[0].ParameterType == typeof(long))
                        {
                            object[] args = parms.Length == 2 ? new object[] { entityId, null } : new object[] { entityId };
                            tryRemove.Invoke(trackObj, args);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                sapi.Logger.Debug($"[Shed Security] HidePlayerEntity reflection failed: {ex.Message}");
            }
        }

        // Reverses the hide — re-adds the entity ID to everyone's tracked set
        // so the admin becomes visible again
        public void ShowPlayerEntity(ICoreServerAPI sapi, IServerPlayer admin)
        {
            try
            {
                long entityId = admin.Entity.EntityId;

                foreach (IServerPlayer other in sapi.World.AllOnlinePlayers)
                {
                    if (other.PlayerUID == admin.PlayerUID) continue;

                    Type viewerType = other.GetType();
                    object clientObj = null;

                    foreach (FieldInfo f in viewerType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    {
                        if (f.FieldType.Name.Contains("ConnectedClient") ||
                            (f.Name.Equals("client", StringComparison.OrdinalIgnoreCase) && !f.FieldType.IsPrimitive))
                        {
                            clientObj = f.GetValue(other);
                            if (clientObj != null) break;
                        }
                    }

                    if (clientObj == null) continue;

                    foreach (FieldInfo f in clientObj.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    {
                        string fn = f.Name.ToLower();
                        if (!(fn.Contains("entit") || fn.Contains("tracked") || fn.Contains("loaded"))) continue;

                        object trackObj = f.GetValue(clientObj);
                        if (trackObj == null) continue;

                        if (trackObj is HashSet<long> hashSet) { hashSet.Add(entityId); continue; }

                        var addMethod = trackObj.GetType().GetMethod("TryAdd")
                                     ?? trackObj.GetType().GetMethod("Add", new[] { typeof(long) });
                        if (addMethod != null)
                        {
                            try { addMethod.Invoke(trackObj, new object[] { entityId }); } catch { }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                sapi.Logger.Debug($"[Shed Security] ShowPlayerEntity reflection failed: {ex.Message}");
            }
        }
    }
}

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx.Bootstrap;
using Newtonsoft.Json.Linq;

namespace Fums.Customs.Client
{
    // MapVariants offers Customs Fums or Vanilla only when its maps.json lists Customs
    // (bigmap) with this plugin. A MapVariants install or update can ship a maps.json
    // without it, so on every start:
    //  - add the bigmap entry to maps.json if it is missing (the other entries stay);
    //  - when it was missing, also hand the entry to MapVariants now, since it read
    //    maps.json before this plugin loaded;
    //  - copy the map screen preview tiles (tiles/bigmap-backport.png and
    //    bigmap-original.png, shipped next to this DLL) into MapVariants' tiles folder.
    // With CustomsClassic installed, FumsCustoms stands down unless MapVariants and
    // CustomsClassic both support several mods per map (see StandDown).
    internal static class MapVariantsEntry
    {
        private const string MapVariantsGuid = "com.lennoxp90.mapvariants";
        private const string CustomsClassicGuid = "com.lennoxp90.customsclassic";
        private const string LocationId = "bigmap";

        private static JObject Entry() => new JObject
        {
            ["locationIds"] = new JArray(LocationId),
            ["pluginGuid"] = Plugin.Guid,
            ["displayName"] = "Customs",
            ["backportName"] = "Fums",
            ["originalName"] = "Vanilla",
            ["standDown"] = "Fums.Customs.Client.BundleSceneLoader:ReleaseIfUnused",
        };

        // MapVariants 1.2.0 holds several mods' variants of one map: FumsCustoms stays the
        // backport (maps.json) and an updated CustomsClassic joins as a source, so Customs
        // offers Vanilla, Fums and Classic. With an older MapVariants or CustomsClassic a map
        // belongs to one mod only, so with CustomsClassic installed FumsCustoms stands down:
        // Customs offers CustomsClassic's versions, and this plugin patches nothing (the server
        // mod decides the same way). Its maps.json entry is removed, because MapVariants would
        // otherwise give Customs to FumsCustoms on the next start and refuse CustomsClassic's
        // own registration. Returns true when standing down.
        internal static bool StandDown()
        {
            if (!Chainloader.PluginInfos.TryGetValue(CustomsClassicGuid, out var classic) || classic?.Instance == null) return false;
            if (Together(classic.Instance.GetType().Assembly))
            {
                Plugin.Log.LogInfo("CustomsClassic is installed too: Customs offers Vanilla, Fums and Classic");
                return false;
            }
            var removed = false;
            try
            {
                if (Chainloader.PluginInfos.TryGetValue(MapVariantsGuid, out var info) && info?.Location != null)
                    removed = RemoveFromMapsFile(Path.Combine(Path.GetDirectoryName(info.Location), "maps.json"));
            }
            catch (Exception error)
            {
                Plugin.Log.LogError("Could not remove Customs from MapVariants' maps.json: " + error.Message +
                                    ". Remove its FumsCustoms entry by hand, or CustomsClassic cannot offer Customs.");
            }
            Plugin.Log.LogWarning("CustomsClassic is installed, so FumsCustoms is off: Customs offers CustomsClassic's versions. " +
                                  "Update MapVariants (1.2.0 or later) and CustomsClassic to offer both, or remove CustomsClassic to play Fums Customs." +
                                  (removed ? " FumsCustoms was MapVariants' Customs this session: restart the game before playing Customs." : ""));
            return true;
        }

        // Takes FumsCustoms' entry out of MapVariants' maps.json, for a stand-down decided elsewhere
        // (ServerStatus: the server has Fums Customs off). True when an entry was removed.
        internal static bool RemoveEntry()
        {
            try
            {
                return Chainloader.PluginInfos.TryGetValue(MapVariantsGuid, out var info) && info?.Location != null
                    && RemoveFromMapsFile(Path.Combine(Path.GetDirectoryName(info.Location), "maps.json"));
            }
            catch (Exception error)
            {
                Plugin.Log.LogError("Could not remove Customs from MapVariants' maps.json: " + error.Message);
                return false;
            }
        }

        // Both sides support sources: MapVariants' public API and CustomsClassic's bridge to it
        // each have RegisterSource.
        private static bool Together(Assembly classic)
        {
            try
            {
                if (!Chainloader.PluginInfos.TryGetValue(MapVariantsGuid, out var info) || info?.Instance == null) return false;
                const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
                return info.Instance.GetType().Assembly.GetType("MapVariants.Client.Api.Maps")?.GetMethod("RegisterSource", any) != null
                    && classic.GetType("CustomsClassic.Client.MapVariantsApi")?.GetMethod("RegisterSource", any) != null;
            }
            catch
            {
                return false;
            }
        }

        internal static void Ensure()
        {
            if (!Chainloader.PluginInfos.TryGetValue(MapVariantsGuid, out var info) || info?.Location == null)
            {
                Plugin.Log.LogInfo("MapVariants is not installed, so Customs is always Fums Customs. Install MapVariants to choose Fums or Vanilla on the map screen.");
                return;
            }
            var dir = Path.GetDirectoryName(info.Location);
            try { CopyTiles(dir); }
            catch (Exception error) { Plugin.Log.LogWarning("MapVariants preview tiles not copied: " + error.Message); }
            try
            {
                if (AddToMapsFile(Path.Combine(dir, "maps.json")))
                    RegisterNow(info.Instance?.GetType().Assembly);
            }
            catch (Exception error)
            {
                Plugin.Log.LogError("Could not add Customs to MapVariants' maps.json, so the map screen will not offer Fums Customs: " + error.Message);
            }
        }

        private static void CopyTiles(string mapVariantsDir)
        {
            var from = Path.Combine(Plugin.Root, "tiles");
            if (!Directory.Exists(from)) return;
            var to = Path.Combine(mapVariantsDir, "tiles");
            Directory.CreateDirectory(to);
            foreach (var source in Directory.GetFiles(from, LocationId + "-*.png"))
            {
                var target = Path.Combine(to, Path.GetFileName(source));
                if (File.Exists(target) && new FileInfo(target).Length == new FileInfo(source).Length) continue;
                File.Copy(source, target, true);
            }
        }

        // True when FumsCustoms' entry was in maps.json and has been removed.
        private static bool RemoveFromMapsFile(string path)
        {
            if (!File.Exists(path)) return false;
            var root = JObject.Parse(File.ReadAllText(path));
            if (!(root["maps"] is JArray maps)) return false;
            var ours = maps.OfType<JObject>().Where(m => string.Equals((string)m["pluginGuid"], Plugin.Guid, StringComparison.OrdinalIgnoreCase)).ToList();
            if (ours.Count == 0) return false;
            foreach (var entry in ours) entry.Remove();
            File.WriteAllText(path, root.ToString() + Environment.NewLine);
            Plugin.Log.LogInfo("Removed Customs from MapVariants' maps.json while Fums Customs is off");
            return true;
        }

        // True when the entry was missing and has been written.
        private static bool AddToMapsFile(string path)
        {
            var root = File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : new JObject();
            if (!(root["maps"] is JArray maps)) root["maps"] = maps = new JArray();
            var existing = maps.OfType<JObject>().FirstOrDefault(m => m["locationIds"] is JArray ids &&
                ids.Any(id => string.Equals((string)id, LocationId, StringComparison.OrdinalIgnoreCase)));
            if (existing != null)
            {
                var owner = (string)existing["pluginGuid"];
                if (!string.Equals(owner, Plugin.Guid, StringComparison.OrdinalIgnoreCase))
                    Plugin.Log.LogWarning($"MapVariants' maps.json gives Customs to '{owner}', not FumsCustoms: the map screen will not offer " +
                                          "Fums Customs. Remove that maps.json entry.");
                return false;
            }
            maps.Add(Entry());
            File.WriteAllText(path, root.ToString() + Environment.NewLine);
            Plugin.Log.LogInfo("Added Customs to MapVariants' maps.json");
            return true;
        }

        // MapVariants' own maps.json parser and registration, as at its start.
        private static void RegisterNow(Assembly mapVariants)
        {
            var mapsFile = mapVariants?.GetType("MapVariants.Client.MapsFile");
            var clientMaps = mapVariants?.GetType("MapVariants.Client.ClientMaps");
            var parse = mapsFile?.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static);
            var add = clientMaps?.GetMethod("Add", BindingFlags.NonPublic | BindingFlags.Static);
            var set = clientMaps?.GetField("Set", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
            var isManaged = set?.GetType().GetMethod("IsManaged", new[] { typeof(string) });
            if (parse == null || add == null || isManaged == null)
            {
                Plugin.Log.LogWarning("Customs was added to MapVariants' maps.json; restart the game to choose Fums or Vanilla Customs");
                return;
            }
            // Already managed: by FumsCustoms, or by another mod. MapVariants 1.2.0 takes the
            // backport into a map that only sources (CustomsClassic) offer so far, so only then
            // is it added now; anything else waits for the next start.
            if ((bool)isManaged.Invoke(set, new object[] { LocationId }))
            {
                var existing = set.GetType().GetMethod("For", new[] { typeof(string) })?.Invoke(set, new object[] { LocationId });
                var hasBackport = existing?.GetType().GetProperty("HasBackport")?.GetValue(existing);
                if (!(hasBackport is bool has) || has) return;
            }
            var json = new JObject { ["maps"] = new JArray(Entry()) }.ToString();
            foreach (var map in (Array)parse.Invoke(null, new object[] { json }))
                add.Invoke(null, new[] { map });
        }
    }
}

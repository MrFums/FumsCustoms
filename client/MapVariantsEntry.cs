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
    // With CustomsClassic installed, FumsCustoms stands down instead (see StandDown).
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

        // MapVariants 1.1.0 manages a map for one mod only, so with CustomsClassic installed
        // FumsCustoms stands down: Customs offers CustomsClassic's versions, and this plugin
        // patches nothing (the server mod stands down too). Its maps.json entry is removed,
        // because MapVariants would otherwise give Customs to FumsCustoms on the next start
        // and refuse CustomsClassic's own registration. Returns true when standing down.
        internal static bool StandDown()
        {
            if (!Chainloader.PluginInfos.TryGetValue(CustomsClassicGuid, out var classic) || classic?.Instance == null) return false;
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
                                  "Remove CustomsClassic to play Fums Customs." +
                                  (removed ? " FumsCustoms was MapVariants' Customs this session: restart the game before playing Customs." : ""));
            return true;
        }

        internal static void Ensure()
        {
            if (!Chainloader.PluginInfos.TryGetValue(MapVariantsGuid, out var info) || info?.Location == null) return;
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
            Plugin.Log.LogInfo("Removed Customs from MapVariants' maps.json while CustomsClassic is installed");
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
            if ((bool)isManaged.Invoke(set, new object[] { LocationId })) return;
            var json = new JObject { ["maps"] = new JArray(Entry()) }.ToString();
            foreach (var map in (Array)parse.Invoke(null, new object[] { json }))
                add.Invoke(null, new[] { map });
        }
    }
}

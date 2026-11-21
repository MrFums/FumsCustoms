using System;
using System.Collections.Generic;
using EFT.Interactive;
using UnityEngine.SceneManagement;

namespace Fums.Customs.Client
{
    // The 1.3 custom_DesignMain renamed Customs' exfils (ex_customs_pmc_*, ...).
    // Quests (Dorms V-Ex, customs_sniper_exit), SPT's car and co-op extract
    // rules and other mods all use the 0.16.9 names, so every 1.3 exfil that
    // has a 0.16.9 twin is given the old name back. The server mod does the
    // same to its 1.3 exit list (CustomsExitsMod), so names still match.
    // Exfils new in 1.3 keep their 1.3 names.
    internal static class ExitNames
    {
        // 1.3 name -> 0.16.9 name. Same table as server db/exits_1x.json "rename".
        internal static readonly Dictionary<string, string> ToOld = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ex_customs_pmc_crossroads"] = "Crossroads",
            ["ex_customs_pmc_trailerpark"] = "Trailer Park",
            ["ex_customs_pmc_zb1011"] = "ZB-1011",
            ["ex_customs_pmc_gasstation"] = "Old Gas Station",
            ["ex_customs_pmc_auto"] = "Dorms V-Ex",
            ["ex_customs_pmc_flare"] = "customs_sniper_exit",
            ["ex_customs_pmc_zb1013"] = "EXFIL_ZB013",
            ["ex_customs_coop_boiler"] = "Custom_scav_pmc",
            ["ex_customs_secret_voron_boat"] = "customs_secret_voron_boat",
            ["ex_customs_secret_voron_bunker"] = "customs_secret_voron_bunker",
            ["ex_customs_scav_admin"] = "Administration Gate",
            ["ex_customs_scav_milbase_cp"] = "Shack",
            ["ex_customs_scav_rail_milbase"] = "Railroad To Military Base",
            ["ex_customs_scav_rail_port"] = "Railroad To Port",
            ["ex_customs_scav_rail_tarkov"] = "Railroad To Tarkov",
            ["ex_customs_scav_gasstation"] = "Old Azs Gate",
            ["ex_customs_scav_trailerpark"] = "Trailer Park Workers Shack",
            ["ex_customs_scav_zb1011"] = "Factory Far Corner",
        };

        internal static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (!BundleSceneLoader.Owns(scene.path)) return;
            try
            {
                var renamed = new List<string>();
                var kept = new List<string>();
                foreach (var root in scene.GetRootGameObjects())
                    foreach (var point in root.GetComponentsInChildren<ExfiltrationPoint>(true))
                    {
                        var name = point.Settings?.Name;
                        if (name == null) continue;
                        if (ToOld.TryGetValue(name, out var old))
                        {
                            point.Settings.Name = old;
                            renamed.Add(name + " -> " + old);
                        }
                        else kept.Add(name);
                    }
                if (renamed.Count + kept.Count == 0) return;
                Plugin.Verbose($"Exits in {scene.name}: {renamed.Count} given their 0.16.9 names ({string.Join(", ", renamed)}); " +
                                   $"{kept.Count} kept ({string.Join(", ", kept)})");
            }
            catch (Exception error)
            {
                Plugin.Log.LogError("Exit renaming failed for " + scene.name + ": " + error);
            }
        }
    }
}

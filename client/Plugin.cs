using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using SPT.Reflection.Patching;
using UnityEngine.SceneManagement;

namespace Fums.Customs.Client
{
    // Required: WTT-ContentBackport (the 1.x items the map uses). Optional: MapVariants
    // (Customs Fums or Vanilla on the map screen; without it Customs is always Fums).
    // MapVariants, Fika and Waypoints load first when present, so maps.json is read and
    // the navmesh and raid patches attach after theirs.
    // SPT 4.1.6 is the minimum: older SPT client files (for example 4.1.2) lack the
    // reverse-patch attribute this plugin uses, and Customs would never finish loading.
    // BepInEx skips the plugin with an error instead, and Customs loads vanilla.
    [BepInPlugin(Guid, DisplayName, Version)]
    [BepInDependency("com.SPT.core", "4.1.6")]
    [BepInDependency("com.lennoxp90.mapvariants", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("com.wtt.contentbackport", BepInDependency.DependencyFlags.HardDependency)]
    [BepInDependency("com.fika.core", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("xyz.drakia.waypoints", BepInDependency.DependencyFlags.SoftDependency)]
    // With CustomsClassic installed, FumsCustoms stands down and patches nothing
    // (MapVariantsEntry.StandDown). Loading after it lets Awake see it reliably.
    [BepInDependency("com.lennoxp90.customsclassic", BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.fums.customs";
        public const string DisplayName = "FumsCustoms";
        public const string Version = "1.0.6";

        // Geometry scenes come from the live backport, plus Custom_Sound (1.3 audio
        // graph) and custom_DesignMain (1.3 exfils, quest zones, triggers; it needs the
        // server mod's 1.3 exit names, config use13Exits). custom_Scripts and custom_AI
        // stay vanilla (0.16.9's own weather, sky and AI data; custom_Scripts gets the
        // 1.3 changes from scripts_patch.json). custom_Terrain also stays vanilla: the
        // heightmap is identical, and the live terrain's material is assigned
        // by its MicroSplat component, which the strip pass removed.
        internal const string DefaultReplacedScenes =
            "custom_mazuto,custom_Garage,custom_Tamozhnya,custom_TrailerPark," +
            "custom_factoryStorageZone,custom_Obshezhitie,custom_Obshezhitie_1_indoor,custom_Obshezhitie_2_indoor," +
            "custom_AZS,custom_city,custom_multiScene,custom_Road,custom_Abandoned_Lab,custom_Abandoned_Plant," +
            "Custom_ChemicalFactory,custom_AZS_old,Custom_Construction_Factory,custom_Pump_Station,Custom_RepairBox," +
            "Custom_Expansion,custom_background,custom_Light,custom_DesignStuff,custom_Culling,Custom_Sound,custom_DesignMain";

        internal static ManualLogSource Log;
        internal static string Root;
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<string> ReplacedScenes;
        internal static ConfigEntry<bool> RebindShaders;
        internal static ConfigEntry<bool> HideDecals;
        internal static ConfigEntry<bool> UseLiveNavMesh;
        internal static ConfigEntry<bool> OcclusionCulling;
        internal static ConfigEntry<bool> VerboseLogs;

        private void Awake()
        {
            Log = Logger;
            Root = Path.GetDirectoryName(Info.Location);
            if (MapVariantsEntry.StandDown()) return;
            // The server mod decides too (ServerStatus): a server with Fums Customs off means this
            // client must not load the 1.3 scenes, or loot binds to the wrong map.
            if (ServerStatus.StandDownAtStart()) return;
            Enabled = Config.Bind("General", "Enabled", true,
                "Load the backported live Customs scenes in place of the vanilla ones.");
            ReplacedScenes = Config.Bind("General", "ReplacedScenes", DefaultReplacedScenes,
                "Comma-separated vanilla Customs scene names to replace with their backported _BP versions. " +
                "Scenes not listed load vanilla.");
            RebindShaders = Config.Bind("Rendering", "RebindShaders", true,
                "Point backported materials at the game's own shaders with the same name.");
            HideDecals = Config.Bind("Rendering", "HideDecals", false,
                "Hide backported decals (cracks, dirt, puddles). Debug switch only: decals draw as plain " +
                "mesh renderers, as in vanilla.");
            UseLiveNavMesh = Config.Bind("AI", "UseLiveNavMesh", true,
                "Replace vanilla Customs' bot navigation mesh with the backport's, baked from the 1.3 map with live's settings; it covers the new areas. " +
                "Bot zones, spawns and patrols stay vanilla.");
            OcclusionCulling = Config.Bind("Rendering", "OcclusionCulling", true,
                "Use the live 1.3 occlusion culling bake for the backport scenes. Turn off if geometry goes missing.");
            VerboseLogs = Config.Bind("Diagnostics", "VerboseLogs", false,
                "Extra log lines for testing: per-scene census, shader, audio, navmesh and exit reports, material keyword " +
                "checks, fire audio environment changes, sources the ambient range gate mutes. Off: only problems and startup lines. " +
                "The in-raid debug keys are in the separate Fums-Debug mod.");

            MapVariantsEntry.Ensure();
            EnablePatch(new LoadPresetPatch());
            EnablePatch(new LoadPresetReversePatch());
            EnablePatch(new LoadScenePatch());
            EnablePatch(new BrokenLootGuard());
            EnablePatch(new DoorLinkGuard());
            EnablePatch(new LockedDoorCarvers());
            EnablePatch(new GrenadePlaceGuard());
            EnablePatch(new CullingSidecarPatch());
            EnablePatch(new BarbedWireSoundGuard());
            EnablePatch(new TriggerHandlerGuard());
            EnablePatch(new DecalParentGuard());
            EnablePatch(new AreaLightFix());
            EnablePatch(new AcousticGeometryRedirectPatch());
            EnablePatch(new AcousticMapRedirectPatch());
            EnablePatch(new SpatialBakeRedirectPatch());
            EnablePatch(new LiveNavMeshInitPatch());
            gameObject.AddComponent<CensusTicker>();
            gameObject.AddComponent<PadlockWatcher>();
            PadlockSync.Init();
            CullingPack.Begin(this);
            SceneManager.sceneLoaded += CullingSwitch.OnSceneLoaded;
            StartupDiagnostics.Enable();
            SceneManager.sceneLoaded += ShaderRebinder.OnSceneLoaded;
            SceneManager.sceneLoaded += SceneFixups.OnSceneLoaded;
            SceneManager.sceneLoaded += LocationSceneLists.OnSceneLoaded;
            SceneManager.sceneLoaded += ExitNames.OnSceneLoaded;
            SceneManager.sceneLoaded += SeasonalMountains.OnSceneLoaded;
            SceneManager.sceneLoaded += MixerBinder.OnSceneLoaded;
            SceneManager.sceneLoaded += EnvironmentAudio.OnSceneLoaded;
            SceneManager.sceneLoaded += CurveWrapFix.OnSceneLoaded;
            SceneManager.sceneLoaded += AmbientRangeGate.OnSceneLoaded;
            gameObject.AddComponent<AmbientRangeGate>();
            SceneManager.sceneLoaded += RoomToneVolume.OnSceneLoaded;
            SceneManager.sceneLoaded += VanillaScriptsPatch.OnSceneLoaded;
            SceneManager.sceneLoaded += Census.OnSceneLoaded;
            SceneManager.sceneUnloaded += BundleSceneLoader.OnSceneUnloaded;
            Log.LogInfo($"{DisplayName} {Version} loaded; bundle present: {File.Exists(BundleSceneLoader.BundlePath)}");
        }

        // Routine per-raid reports: silent unless Diagnostics/VerboseLogs is on.
        internal static void Verbose(string message)
        {
            if (VerboseLogs != null && VerboseLogs.Value) Log.LogInfo(message);
        }

        // One failing patch must not stop the others from attaching.
        private static void EnablePatch(ModulePatch patch)
        {
            try { patch.Enable(); }
            catch (Exception error) { Log.LogError($"Patch {patch.GetType().Name} failed: {error}"); }
        }
    }
}

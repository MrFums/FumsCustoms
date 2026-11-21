using System.Reflection;
using HarmonyLib;
using JetBrains.Annotations;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Services.InRaid;

namespace Fums.Customs.Server;

// Keeps other mods' bot spawns on Customs, in Fums and Vanilla raids alike.
//
// CustomsExitsMod gives Customs its own copy of the base (MapVariants swaps SPT's
// base back in for Vanilla raids). Two things went wrong with spawn mods:
//  - RUAF Come Home and Black Division add their patrols and hunts at load. ABPS
//    loads later (priority 1069420) and replaces the whole boss list, so those
//    entries were gone until a raid had ended and both mods added them again.
//  - ABPS wrote only the copy, so Vanilla Customs kept SPT's table without ABPS.
// After ABPS has loaded, this re-runs both mods' own spawn setup (each removes
// its entries before adding them, as at every raid end) and, while Customs keeps
// SPT's table (a bot spawn mod is loaded), points both bases at the same boss and
// wave lists. ABPS and the faction mods replace those lists on the live base at
// raid end; at raid start the newer lists are copied to the other base, so
// whichever variant is chosen gets them.
[Injectable(TypePriority = OnLoadOrder.PostLoad + 400000), UsedImplicitly]
public class SpawnModSync(IServiceProvider services, LocationTable locationTable, ISptLogger<SpawnModSync> logger) : IOnLoad
{
    private static LocationBase? _original;
    private static LocationBase? _backport;
    private static bool _share;
    private static List<BossLocationSpawn>? _bosses;
    private static List<Wave>? _waves;

    // The faction mods' own spawn setup: type, parameterless method.
    private static readonly (string Type, string Method, string Mod)[] FactionSpawns =
    [
        ("RUAFComeHomeServer.Controllers.RUAFSpawnController", "AdjustAllRuafSpawns", "RUAF Come Home"),
        ("BlackDivServer.Controllers.SpawnController", "AdjustAllSpawns", "Black Division"),
    ];

    internal static void Track(LocationBase original, LocationBase backport, bool share)
    {
        _original = original;
        _backport = backport;
        _share = share;
    }

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        if (ClassicCoexist.FumsOff) return Task.CompletedTask; // CustomsClassic installed
        Table = locationTable;
        var rerun = new List<string>();
        foreach (var (typeName, methodName, mod) in FactionSpawns)
        {
            try
            {
                var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(typeName, false)).FirstOrDefault(t => t != null);
                if (type == null) continue;
                var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes);
                var instance = services.GetService(type);
                if (method == null || instance == null)
                {
                    logger.Warning($"[FumsCustoms] {mod} found, but its spawn setup could not be called; its spawns return after the first raid");
                    continue;
                }
                method.Invoke(instance, null);
                rerun.Add(mod);
            }
            catch (Exception e)
            {
                logger.Warning($"[FumsCustoms] {mod} spawn setup failed: {(e is TargetInvocationException t ? t.InnerException?.Message : e.Message)}");
            }
        }

        if (_share && _original != null && _backport != null)
        {
            _original.BossLocationSpawn = _backport.BossLocationSpawn;
            _original.Waves = _backport.Waves;
            _bosses = _backport.BossLocationSpawn;
            _waves = _backport.Waves;
            try { new SpawnModSyncPatch().Enable(); }
            catch (Exception e) { logger.Warning($"[FumsCustoms] spawn sync at raid start not enabled: {e.Message}"); }
        }
        if (rerun.Count > 0)
            logger.Info($"[FumsCustoms] spawns of {string.Join(", ", rerun)} added again after the bot spawn mods loaded");
        return Task.CompletedTask;
    }

    // Before a Customs raid is generated: copy whichever base's lists changed since
    // the last sync to the other base.
    internal static void SyncBeforeRaid(LocationBase? live)
    {
        if (_original == null || _backport == null || live == null) return;
        var other = ReferenceEquals(live, _backport) ? _original : ReferenceEquals(live, _original) ? _backport : null;
        if (other == null) return;
        _bosses = Pick(live.BossLocationSpawn, other.BossLocationSpawn, _bosses);
        _waves = Pick(live.Waves, other.Waves, _waves);
        if (_bosses != null) live.BossLocationSpawn = other.BossLocationSpawn = _bosses;
        if (_waves != null) live.Waves = other.Waves = _waves;
    }

    private static List<T>? Pick<T>(List<T>? live, List<T>? other, List<T>? last) =>
        !ReferenceEquals(live, last) ? live : !ReferenceEquals(other, last) ? other : last;

    internal static LocationTable? Table;
}

[IgnoreAutoPatch]
public class SpawnModSyncPatch : AbstractPatch
{
    protected override MethodBase? GetTargetMethod() =>
        AccessTools.Method(typeof(LocationLifecycleService), nameof(LocationLifecycleService.GenerateLocationAndLoot));

    [PatchPrefix]
    public static void Prefix(string name)
    {
        if (!string.Equals(name, "bigmap", StringComparison.OrdinalIgnoreCase)) return;
        try { SpawnModSync.SyncBeforeRaid(SpawnModSync.Table?.Bigmap?.Base); }
        catch { /* spawns stay as they are */ }
    }
}

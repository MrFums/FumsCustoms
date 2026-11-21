using System.Reflection;
using HarmonyLib;
using JetBrains.Annotations;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Services.InRaid;

namespace Fums.Customs.Server;

// Logs the Customs boss table each raid is generated with (after every mod's changes):
// boss, chance, escorts, zones and spawn modes, plus the bot cap and wave count. It
// shows which bosses and faction spawns (Goons, RUAF, Black Division, ISB) a raid can
// actually get. Read-only. Off unless config.json has "logRaidSpawns": true.
[Injectable(TypePriority = OnLoadOrder.PostLoad + 103), UsedImplicitly]
public class RaidSpawnLog(ISptLogger<RaidSpawnLog> logger) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        if (ClassicCoexist.FumsOff) return Task.CompletedTask; // CustomsClassic installed
        try
        {
            var cfg = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(typeof(RaidSpawnLog).Assembly.Location)!, "config.json");
            if (!File.Exists(cfg) || !System.Text.Json.JsonDocument.Parse(File.ReadAllText(cfg)).RootElement
                    .TryGetProperty("logRaidSpawns", out var on) || on.ValueKind != System.Text.Json.JsonValueKind.True)
                return Task.CompletedTask;
            RaidSpawnLogPatch.Log = logger;
            new RaidSpawnLogPatch().Enable();
        }
        catch (Exception e)
        {
            logger.Warning($"[FumsCustoms] raid spawn log not enabled: {e.Message}");
        }
        return Task.CompletedTask;
    }
}

[IgnoreAutoPatch]
public class RaidSpawnLogPatch : AbstractPatch
{
    internal static ISptLogger<RaidSpawnLog>? Log;

    protected override MethodBase? GetTargetMethod() =>
        AccessTools.Method(typeof(LocationLifecycleService), nameof(LocationLifecycleService.GenerateLocationAndLoot));

    [PatchPostfix]
    public static void Postfix(string name, LocationBase __result)
    {
        try
        {
            if (Log == null || __result == null || !string.Equals(name, "bigmap", StringComparison.OrdinalIgnoreCase)) return;
            var bosses = __result.BossLocationSpawn ?? [];
            var rows = bosses.Select(b =>
                $"{b.BossName} {b.BossChance}%{(b.BossEscortAmount is { Length: > 0 } e && b.BossEscortType is { Length: > 0 } et ? $" +{et}({e})" : "")} " +
                $"zones [{b.BossZone}] time {b.Time} modes [{string.Join(",", b.SpawnMode ?? [])}]{(b.TriggerId is { Length: > 0 } t ? " trigger " + t : "")}");
            Log.Info($"[FumsCustoms] Customs raid spawns: bot max {__result.BotMax}, waves {__result.Waves?.Count ?? 0}, " +
                     $"boss entries {bosses.Count}:\n  " + string.Join("\n  ", rows));
        }
        catch (Exception e)
        {
            Log?.Warning($"[FumsCustoms] raid spawn log failed: {e.Message}");
        }
    }
}

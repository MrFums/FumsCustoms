using System.Text.Json.Serialization;
using JetBrains.Annotations;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Utils;

namespace Fums.Customs.Server;

// The body of a raid start. Only the location is read.
public sealed class ClassicRaidStartRequest : IRequestData
{
    [JsonPropertyName("location")] public string Location { get; set; } = "";
}

// With CustomsClassic beside FumsCustoms (ClassicCoexist.Together), a Classic raid serves
// CustomsClassic's own loose loot, which sets no loot multiplier. Fums' loot sets config.json's
// multipliers and SPT's own loot sets SPT's, each as it is read, so the last Fums read (a Fums
// raid, or a loot index at start) would carry over into Classic. At raid start, before SPT
// generates the raid, SPT's multipliers are put back whenever Customs is not serving Fums' loot.
// A Fums raid is untouched: its own loot sets the multipliers again when it is read.
[Injectable(TypePriority = OnLoadOrder.Routers - 2), UsedImplicitly]
public class ClassicLootMultipliers(JsonUtil jsonUtil, LocationTable locationTable, LocationConfig locationConfig)
    : StaticRouter(jsonUtil,
    [
        new RouteAction<ClassicRaidStartRequest>("/client/raid/configuration",
            (url, info, sessionId, output, cancellationToken) => Reset(info, output ?? "", locationTable, locationConfig)),
        new RouteAction<ClassicRaidStartRequest>("/client/match/local/start",
            (url, info, sessionId, output, cancellationToken) => Reset(info, output ?? "", locationTable, locationConfig)),
    ])
{
    private const string Map = "bigmap";
    private static object? _fumsLoose;
    private static double _stockLoose, _stockStatic;

    internal static void Track(object? fumsLoose, double stockLoose, double stockStatic)
    {
        _fumsLoose = fumsLoose;
        _stockLoose = stockLoose;
        _stockStatic = stockStatic;
    }

    private static ValueTask<string> Reset(ClassicRaidStartRequest? info, string output, LocationTable locationTable, LocationConfig locationConfig)
    {
        try
        {
            if (_fumsLoose is null || !string.Equals(info?.Location?.Trim(), Map, StringComparison.OrdinalIgnoreCase)) return ValueTask.FromResult(output);
            if (ReferenceEquals(locationTable.Bigmap?.LooseLoot, _fumsLoose)) return ValueTask.FromResult(output);
            locationConfig.LooseLootMultiplier[Map] = _stockLoose;
            locationConfig.StaticLootMultiplier[Map] = _stockStatic;
        }
        catch
        {
            // Never in the way of a raid start: the worst case is the previous raid's multipliers.
        }
        return ValueTask.FromResult(output);
    }
}

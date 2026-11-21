using JetBrains.Annotations;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;

namespace Fums.Customs.Server;

// FumsCustoms and CustomsClassic both offer a Customs, and MapVariants 1.1.0 manages a map
// for one mod only. With CustomsClassic installed, FumsCustoms stands down: it changes no
// game data (loot, exits, raid settings, spawn sync, container types, bunkhouse key), so
// CustomsClassic's Customs and Vanilla Customs run exactly as without FumsCustoms. The
// client plugin stands down the same way. Nothing is declared incompatible: in SPT 4.1 one
// incompatibility stops every mod from loading.
// Once MapVariants lets a second mod add its own variant to a map, both can be offered.
[Injectable(TypePriority = OnLoadOrder.Preload), UsedImplicitly]
public class ClassicCoexist(ISptLogger<ClassicCoexist> logger) : IOnLoad
{
    private const string ClassicAssembly = "CustomsClassic.Server";
    private static bool? _off;

    // True when CustomsClassic's server mod is loaded. Mod assemblies are all loaded before
    // any OnLoad runs, so this is valid from the first one.
    internal static bool FumsOff => _off ??= AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == ClassicAssembly);

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        if (FumsOff)
            logger.Warning("[FumsCustoms] CustomsClassic is installed, so FumsCustoms is off: Customs offers CustomsClassic's versions. " +
                           "Remove CustomsClassic to play Fums Customs.");
        return Task.CompletedTask;
    }
}

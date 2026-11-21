using System.Reflection;
using JetBrains.Annotations;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;

namespace Fums.Customs.Server;

// FumsCustoms and CustomsClassic both offer a Customs.
// - MapVariants 1.2.0 and up holds several mods' variants of one map: FumsCustoms stays the
//   backport and CustomsClassic joins as source "classic", so Customs offers Vanilla, Fums
//   and Classic. That needs CustomsClassic's MapVariants-source update too.
// - With an older MapVariants or CustomsClassic, a map belongs to one mod only, so
//   FumsCustoms stands down: it changes no game data (loot, exits, raid settings, spawn sync,
//   container types, bunkhouse key), and CustomsClassic's Customs and Vanilla Customs run
//   exactly as without FumsCustoms. The client plugin decides the same way.
// Nothing is declared incompatible: in SPT 4.1 one incompatibility stops every mod loading.
[Injectable(TypePriority = OnLoadOrder.Preload), UsedImplicitly]
public class ClassicCoexist(ISptLogger<ClassicCoexist> logger) : IOnLoad
{
    private const string ClassicAssembly = "CustomsClassic.Server";
    private static bool? _present, _together;

    // Mod assemblies are all loaded before any OnLoad runs, so these are valid from the first one.
    internal static bool ClassicPresent => _present ??= AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == ClassicAssembly);

    // Both sides support sources: MapVariants' Providers and CustomsClassic's bridge each have RegisterSource.
    internal static bool Together => _together ??= ClassicPresent
        && HasMethod("MapVariants.Server.Variants.Providers", "RegisterSource")
        && HasMethod("CustomsClassic.Server.MapVariantsBridge", "RegisterSource");

    // True when CustomsClassic is installed and the two cannot both be offered.
    internal static bool FumsOff => ClassicPresent && !Together;

    private static bool HasMethod(string typeName, string method) => AppDomain.CurrentDomain.GetAssemblies()
        .Select(a => a.GetType(typeName, false))
        .Any(t => t?.GetMethod(method, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) != null);

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        if (FumsOff)
            logger.Warning("[FumsCustoms] CustomsClassic is installed, so FumsCustoms is off: Customs offers CustomsClassic's versions. " +
                           "Update MapVariants (1.2.0 or later) and CustomsClassic to offer both, or remove CustomsClassic to play Fums Customs.");
        else if (Together)
            logger.Info("[FumsCustoms] CustomsClassic is installed too: Customs offers Vanilla, Fums and Classic");
        return Task.CompletedTask;
    }
}

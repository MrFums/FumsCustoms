using JetBrains.Annotations;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;

namespace Fums.Customs.Server;

// FumsCustoms and CustomsClassic both offer a Customs. MapVariants 1.1.0 manages a map for one mod
// only, so there, with CustomsClassic installed, FumsCustoms stands down: it changes no
// game data (loot, exits, raid settings, spawn sync, container types, bunkhouse key), so
// CustomsClassic's Customs and Vanilla Customs run exactly as without FumsCustoms. The
// client plugin stands down the same way. Nothing is declared incompatible: in SPT 4.1 one
// incompatibility stops every mod from loading.
// MapVariants 1.2.0 lets a second mod add its own variant to a map, and CustomsClassic 1.1.0 does: with
// both, FumsCustoms stays on and Customs offers Vanilla, Fums and Classic.
// It also declares its client plugin to MapVariants 1.2.0, so a client with another mod's Customs
// backport is caught on the map screen instead of loading its scenes over this mod's data.
[Injectable(TypePriority = OnLoadOrder.Preload), UsedImplicitly]
public class ClassicCoexist(IServiceProvider provider, ISptLogger<ClassicCoexist> logger) : IOnLoad
{
    private const string ClassicAssembly = "CustomsClassic.Server";
    private const string ProvidersType = "MapVariants.Server.Variants.Providers";
    private const string ClientPluginGuid = "com.fums.customs";
    private static bool? _off;

    // True when CustomsClassic's server mod is loaded and the two cannot share Customs: an older
    // MapVariants (no RegisterSource) or an older CustomsClassic (before 1.1.0, still Customs' only
    // backport). Mod assemblies are all loaded before any OnLoad runs, so this is valid from the first one.
    internal static bool FumsOff => _off ??= Off();

    private static bool Off()
    {
        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
        var classic = assemblies.FirstOrDefault(a => a.GetName().Name == ClassicAssembly);
        if (classic == null) return false;
        var sources = Providers(assemblies)?.GetMethod("RegisterSource") != null;
        return !sources || classic.GetName().Version < new Version(1, 1, 0);
    }

    private static Type? Providers(IEnumerable<System.Reflection.Assembly> assemblies)
        => assemblies.Select(a => a.GetType(ProvidersType, false)).FirstOrDefault(t => t != null);

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        if (FumsOff)
        {
            logger.Warning("[FumsCustoms] CustomsClassic is installed, so FumsCustoms is off: Customs offers CustomsClassic's versions. " +
                           "Update MapVariants to 1.2.0 and CustomsClassic to 1.1.0 to have both, or remove CustomsClassic.");
            return Task.CompletedTask;
        }
        Declare();
        return Task.CompletedTask;
    }

    // MapVariants 1.2.0 and up: names this mod's client plugin for Customs, so the map screen can tell
    // a client with another mod's Customs backport. Absent before that, and then nothing is compared.
    private void Declare()
    {
        try
        {
            var type = Providers(AppDomain.CurrentDomain.GetAssemblies());
            var declare = type?.GetMethod("DeclarePlugin", [typeof(string), typeof(string)]);
            var providers = type == null ? null : provider.GetService(type);
            if (declare == null || providers == null) return;
            declare.Invoke(providers, ["bigmap", ClientPluginGuid]);
            logger.Info($"[FumsCustoms] declared {ClientPluginGuid} as Customs' client plugin to MapVariants");
        }
        catch (Exception error)
        {
            logger.Warning("[FumsCustoms] could not declare the client plugin to MapVariants: " + error.Message);
        }
    }
}

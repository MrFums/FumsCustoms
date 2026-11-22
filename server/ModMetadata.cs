using JetBrains.Annotations;
using SPTarkov.Server.Core.Models.Spt.Mod;

namespace Fums.Customs.Server;

[UsedImplicitly]
public record ModMetadata : IModMetadata
{
    public string ModGuid { get; init; } = "com.fums.customs";
    public string Name { get; init; } = "FumsCustoms";
    public string Author { get; init; } = "Fums";
    public List<string>? Contributors { get; init; }
    public SemanticVersioning.Version Version { get; init; } = new(typeof(ModMetadata).Assembly.GetName().Version!.ToString(3));
    public SemanticVersioning.Range SptVersion { get; init; } = new("~4.1.6"); // 4.1.6 minimum, as the client plugin
    public bool HasPrepatcher { get; init; } = false;
    // None. Never list a mod here: in SPT 4.1 one incompatibility stops EVERY mod from loading.
    // With CustomsClassic installed, FumsCustoms stands down instead (ClassicCoexist).
    public List<string>? Incompatibilities { get; init; }
    // WTT-ContentBackport: the 1.x items (keys, loot) the map and loot use.
    // MapVariants is optional: with it, Customs Fums or Vanilla is picked on the map
    // screen; without it, Customs is always Fums.
    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; } = new()
    {
        ["com.wtt.contentbackport"] = new(">=2.0.7"),
    };
    public string? Url { get; init; }
    public string License { get; init; } = "MIT";
}

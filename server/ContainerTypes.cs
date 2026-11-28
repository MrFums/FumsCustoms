using System.Text.Json;
using JetBrains.Annotations;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Utils;
using SysPath = System.IO.Path;

namespace Fums.Customs.Server;

// Adds the 1.x container types Customs' 1.x loot uses (locked tech/med/food crates,
// USEC corpse, rich gun safe) unless another mod already defines them (Manimal's
// Lighthouse ships four of them). Built on the 0.16.9 type each one replaced; names
// come from that type's locale text.
// SPT snapshots the item database when profiles load (SaveCallbacks) and stops the
// server if an item appears after that, so this runs just before the snapshot and
// after every other mod that adds items earlier.
[Injectable(TypePriority = OnLoadOrder.SaveCallbacks - 1), UsedImplicitly]
public class ContainerTypes(
    TemplateTable templateTable,
    LocaleTable localeTable,
    JsonUtil jsonUtil,
    ISptLogger<ContainerTypes> logger)
    : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        if (ClassicCoexist.FumsOff) return Task.CompletedTask; // CustomsClassic installed
        try
        {
            var dir = SysPath.GetDirectoryName(typeof(ContainerTypes).Assembly.Location)!;
            var path = SysPath.Combine(dir, "db", "container_templates.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var names = new Dictionary<string, string>();   // new tpl -> 0.16.9 tpl for locale text
            int added = 0, existing = 0;
            foreach (var entry in doc.RootElement.EnumerateObject())
            {
                var tpl = new MongoId(entry.Name);
                names[entry.Name] = entry.Value.GetProperty("localeFrom").GetString()!;
                if (templateTable.Items.ContainsKey(tpl)) { existing++; continue; }
                var item = jsonUtil.Deserialize<TemplateItem>(entry.Value.GetProperty("template").GetRawText());
                if (item is null) continue;
                templateTable.Items[tpl] = item;
                added++;
            }
            foreach (var locale in localeTable.Global.Values)
                locale.AddTransformer(d =>
                {
                    if (d is null) return d;
                    foreach (var (newTpl, oldTpl) in names)
                        foreach (var suffix in new[] { " Name", " ShortName", " Description" })
                            if (!d.ContainsKey(newTpl + suffix) && d.TryGetValue(oldTpl + suffix, out var text))
                                d[newTpl + suffix] = text;
                    return d;
                });
            logger.Debug($"[FumsCustoms] 1.x container types: {added} added, {existing} already defined by another mod");
        }
        catch (Exception e)
        {
            logger.Error($"[FumsCustoms] new container types not added (their containers are skipped): {e.Message}");
        }
        return Task.CompletedTask;
    }
}

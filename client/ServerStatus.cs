using System;
using Newtonsoft.Json.Linq;
using SPT.Common.Http;

namespace Fums.Customs.Client
{
    // Asks the server mod whether this server serves Fums Customs (GET /fums/customs/status,
    // server StatusRoute.cs). The 1.3 scenes load only when it says yes.
    // Why: the server and every client (Fika headless included) decide on their own whether
    // FumsCustoms runs. With CustomsClassic on the server only, the server switched itself off
    // and served CustomsClassic's loot, while the clients still loaded the 1.3 scenes: loot binds
    // to scene containers by Id, so most containers got nothing and opened like doors. A server
    // without FumsCustoms (or one older than this check) has no route, so the answer is "no"
    // there too, and Customs loads vanilla.
    internal static class ServerStatus
    {
        private const string Route = "/fums/customs/status";

        internal enum Answer { On, Off, NoAnswer }

        // At plugin start: a definite "off" stands the plugin down, as CustomsClassic on the client
        // does (MapVariantsEntry.StandDown), so the map screen stops offering Fums Customs. No
        // answer (server not reachable yet, or no FumsCustoms on it) changes nothing here; the raid
        // start asks again and decides. Returns true when standing down.
        internal static bool StandDownAtStart()
        {
            var answer = Ask(out var reason, out _);
            if (answer != Answer.Off) return false;
            var removed = MapVariantsEntry.RemoveEntry();
            Plugin.Log.LogWarning("The server has Fums Customs switched off" + Because(reason) +
                                  ", so this client loads vanilla Customs and patches nothing. Install the same Customs mods on the server and every client." +
                                  (removed ? " FumsCustoms was MapVariants' Customs this session: restart the game before playing Customs." : ""));
            return true;
        }

        // At every Customs raid start, before the 1.3 scenes load: anything but a clear yes loads
        // vanilla Customs, because only then do this client's scenes and the server's data match.
        internal static bool AllowsRaid()
        {
            var answer = Ask(out var reason, out var version);
            if (answer == Answer.On)
            {
                Plugin.Verbose($"The server serves Fums Customs (server mod {version})");
                return true;
            }
            Plugin.Log.LogWarning(answer == Answer.Off
                ? "The server has Fums Customs switched off" + Because(reason) + ", so this raid loads vanilla Customs."
                : "The server did not say it serves Fums Customs: it has no FumsCustoms server mod, or one older than this client. " +
                  "This raid loads vanilla Customs. Install the same FumsCustoms version on the server.");
            return false;
        }

        // One retry: a dropped request must not cost a raid its 1.3 map.
        private static Answer Ask(out string reason, out string version)
        {
            reason = version = "";
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    var json = RequestHandler.GetJson(Route);
                    if (string.IsNullOrWhiteSpace(json)) continue;
                    var root = JObject.Parse(json);
                    if (root["active"]?.Type != JTokenType.Boolean) continue; // not our route's answer (an SPT error body)
                    reason = (string)root["reason"] ?? "";
                    version = (string)root["version"] ?? "";
                    return (bool)root["active"] ? Answer.On : Answer.Off;
                }
                catch (Exception error)
                {
                    Plugin.Verbose($"Server status request failed (attempt {attempt} of 2): {error.GetType().Name}: {error.Message}");
                }
            }
            return Answer.NoAnswer;
        }

        private static string Because(string reason) => string.IsNullOrWhiteSpace(reason) ? "" : " (" + reason + ")";
    }
}

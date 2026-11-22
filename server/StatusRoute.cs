using System.Text.Json.Serialization;
using JetBrains.Annotations;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Utils;

namespace Fums.Customs.Server;

// GET /fums/customs/status: whether this server serves Fums Customs' data (loot, exits, raid
// settings, container types). The client plugin asks before it loads the 1.3 scenes and loads
// vanilla Customs unless the answer is yes.
// Why: the server and each client decide on their own whether FumsCustoms runs (for example
// ClassicCoexist sees CustomsClassic on the server only). A client that loads the 1.3 scenes
// while the server serves other data binds loot by container Id to the wrong map: most
// containers get nothing and open like doors, with no error anywhere. A server with no
// FumsCustoms (or an older one) has no such route, so the client then loads vanilla too.
public sealed class StatusResponse
{
    [JsonPropertyName("active")] public bool Active { get; set; }
    [JsonPropertyName("version")] public string Version { get; set; } = "";

    // Why Fums Customs is off, for the client's log line; empty when it is on.
    [JsonPropertyName("reason")] public string Reason { get; set; } = "";
}

// The route takes EmptyRequestData: a route that declares a body type fails on the null body
// SPT hands every GET.
[Injectable, UsedImplicitly]
public class StatusRoute(JsonUtil jsonUtil, HttpResponseUtil httpResponseUtil) : StaticRouter(
    jsonUtil,
    [
        new RouteAction<EmptyRequestData>("/fums/customs/status",
            (url, info, sessionId, output, ct) => ValueTask.FromResult(httpResponseUtil.NoBody(Status()))),
    ])
{
    internal static StatusResponse Status() => new()
    {
        Active = !ClassicCoexist.FumsOff,
        Version = typeof(StatusRoute).Assembly.GetName().Version?.ToString(3) ?? "",
        Reason = ClassicCoexist.FumsOff
            ? "CustomsClassic is installed on the server, so the server serves CustomsClassic's Customs"
            : "",
    };
}

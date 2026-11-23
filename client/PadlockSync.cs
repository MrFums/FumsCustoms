using System;
using System.IO;
using System.Reflection;
using EFT.Interactive;

namespace Fums.Customs.Client
{
    // Fika door sync for padlocked doors. The Fika code lives in its own assembly
    // (fika/PadlockSyncFika.cs, FumsCustoms.Fika), embedded here as a resource and loaded
    // only when Fika is installed.
    //
    // Never put a Fika type in this assembly, not even in a field or interface that never
    // runs: other mods (Newtonsoft's UnityConverterInitializer) scan every loaded type, and
    // without Fika that scan throws a TypeLoadException. In 1.0.3 it stopped JSON setup, and
    // every item stack (money, ammo) showed 1.
    internal static class PadlockSync
    {
        private const string Resource = "FumsCustoms.Fika.dll";
        private const string SyncType = "Fums.Customs.Client.PadlockSyncFika";

        private static Func<bool> _isClient;
        private static Action<string, byte> _send;

        internal static void Init()
        {
            try
            {
                if (HarmonyLib.AccessTools.TypeByName("Fika.Core.Modding.FikaEventDispatcher") == null) return;

                byte[] raw;
                using (var s = typeof(PadlockSync).Assembly.GetManifestResourceStream(Resource))
                {
                    if (s == null) { Plugin.Log.LogWarning("Padlock sync off: " + Resource + " not embedded"); return; }
                    using (var m = new MemoryStream()) { s.CopyTo(m); raw = m.ToArray(); }
                }

                var t = Assembly.Load(raw).GetType(SyncType, true);
                var init = (Action<Action<string, byte>>)Delegate.CreateDelegate(
                    typeof(Action<Action<string, byte>>), t.GetMethod("Init"));
                var isClient = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), t.GetMethod("IsClient"));
                var send = (Action<string, byte>)Delegate.CreateDelegate(typeof(Action<string, byte>), t.GetMethod("Send"));

                init(Apply);
                _isClient = isClient;
                _send = send;
                Plugin.Log.LogInfo("Padlock sync on (Fika)");
            }
            catch (Exception e)
            {
                _isClient = null;
                _send = null;
                Plugin.Log.LogInfo("Padlock sync off: " + e.Message);
            }
        }

        internal static bool IsClient()
        {
            var f = _isClient;
            return f != null && f();
        }

        internal static void HostSend(string id, EDoorState state)
        {
            var f = _send;
            if (f == null) return;
            try { f(id, (byte)state); }
            catch (Exception e) { Plugin.Log.LogWarning("Padlock sync send: " + e.Message); }
        }

        private static void Apply(string id, byte state)
        {
            try { Padlocks.ApplyHostState(id, (EDoorState)state); }
            catch (Exception e) { Plugin.Log.LogError("Padlock sync: " + e); }
        }
    }
}

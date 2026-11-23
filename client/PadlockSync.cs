using System;
using System.Runtime.CompilerServices;
using EFT.Interactive;
using Fika.Core.Main.Utils;
using Fika.Core.Modding;
using Fika.Core.Modding.Events;
using Fika.Core.Networking;
using Fika.Core.Networking.LiteNetLib;
using Fika.Core.Networking.LiteNetLib.Utils;

namespace Fums.Customs.Client
{
    // Fika: the raid host runs the bots, so it is the host that unlocks and opens padlocked
    // doors (Padlocks / PadlockWatcher). Fika does not send a door change made in code, and a
    // client ignores a bot's "open" while its own copy of the door is still locked, so the bot
    // walks through a door that looks shut and padlocked there. The host sends every state
    // change of a padlocked door; clients apply it. Only touched when Fika is loaded.
    internal static class PadlockSyncFika
    {
        private static IFikaNetworkManager _manager;

        internal static void Init()
        {
            FikaEventDispatcher.SubscribeEvent<FikaNetworkManagerCreatedEvent>(e =>
            {
                _manager = e.Manager;
                e.Manager.RegisterPacket<PadlockDoorPacket>(OnPacket);
            });
        }

        internal static bool IsServer => FikaBackendUtils.IsServer;
        internal static bool IsClient => FikaBackendUtils.IsClient;

        internal static void Send(string id, EDoorState state)
        {
            if (_manager == null || !FikaBackendUtils.IsServer) return;
            var packet = new PadlockDoorPacket { DoorId = id, State = (byte)state };
            _manager.SendData(ref packet, DeliveryMethod.ReliableOrdered);
        }

        private static void OnPacket(PadlockDoorPacket p)
        {
            if (!FikaBackendUtils.IsClient) return;
            try { Padlocks.ApplyHostState(p.DoorId, (EDoorState)p.State); }
            catch (Exception e) { Plugin.Log.LogError("Padlock sync: " + e); }
        }
    }

    internal sealed class PadlockDoorPacket : INetSerializable
    {
        public string DoorId;
        public byte State;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(DoorId ?? "");
            writer.Put(State);
        }

        public void Deserialize(NetDataReader reader)
        {
            DoorId = reader.GetString();
            State = reader.GetByte();
        }
    }

    // Keeps Fika's types out of everything else, so FumsCustoms still runs without Fika.
    internal static class PadlockSync
    {
        private static bool _present;

        internal static void Init()
        {
            try { _present = HarmonyLib.AccessTools.TypeByName("Fika.Core.Modding.FikaEventDispatcher") != null && InitFika(); }
            catch (Exception e) { _present = false; Plugin.Log.LogInfo("Padlock sync off: " + e.Message); }
        }

        internal static bool IsClient() => _present && IsClientFika();

        internal static void HostSend(string id, EDoorState state)
        {
            if (!_present) return;
            try { SendFika(id, state); }
            catch (Exception e) { Plugin.Log.LogWarning("Padlock sync send: " + e.Message); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool InitFika() { PadlockSyncFika.Init(); return true; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool IsClientFika() => PadlockSyncFika.IsClient;

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void SendFika(string id, EDoorState state) => PadlockSyncFika.Send(id, state);
    }
}

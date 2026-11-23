using System;
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
    // change of a padlocked door; clients apply it.
    //
    // This file is its own assembly (FumsCustoms.Fika), embedded in FumsCustoms.dll and loaded
    // only when Fika is installed. FumsCustoms.dll itself must never reference Fika.Core: other
    // mods scan every loaded type, and one Fika-typed field or interface there throws a
    // TypeLoadException without Fika (1.0.3 broke every item stack that way).
    // Names and namespace match 1.0.3, so the packet hash is unchanged.
    public static class PadlockSyncFika
    {
        private static IFikaNetworkManager _manager;
        private static Action<string, byte> _apply;

        public static void Init(Action<string, byte> apply)
        {
            _apply = apply;
            FikaEventDispatcher.SubscribeEvent<FikaNetworkManagerCreatedEvent>(e =>
            {
                _manager = e.Manager;
                e.Manager.RegisterPacket<PadlockDoorPacket>(OnPacket);
            });
        }

        public static bool IsClient() => FikaBackendUtils.IsClient;

        public static void Send(string id, byte state)
        {
            if (_manager == null || !FikaBackendUtils.IsServer) return;
            var packet = new PadlockDoorPacket { DoorId = id, State = state };
            _manager.SendData(ref packet, DeliveryMethod.ReliableOrdered);
        }

        private static void OnPacket(PadlockDoorPacket p)
        {
            if (!FikaBackendUtils.IsClient) return;
            _apply?.Invoke(p.DoorId, p.State);
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
}

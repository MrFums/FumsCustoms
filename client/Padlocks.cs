using System.Collections.Generic;
using System.Linq;
using CommonAssets.Scripts.Game.GameTriggers.Handlers;
using Comfort.Common;
using EFT;
using EFT.GameTriggers;
using EFT.Interactive;
using EFT.InventoryLogic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;

namespace Fums.Customs.Client
{
    // 1.3 padlocks: a locked door or container whose padlock (a TriggerBallistic on the
    // lock's BallisticCollider) opens it when shot (HandlerInteractable, Open) and hides
    // the lock (HandlerGameObjectState). They have no key.
    //
    // AI mods (ORBIT) unlock doors with WorldInteractiveObject.Unlock(). That is let
    // through as is. When a padlocked object leaves Locked without its padlock being shot
    // (on every machine: Fika clients see the host's door state), the padlock is removed
    // and its trigger spent, as if shot, and the nearest bot's gun plays a shot sound.
    internal static class Padlocks
    {
        internal sealed class Entry
        {
            internal WorldInteractiveObject Target;
            internal TriggerBallistic Trigger;
            internal Collider Lock;
            internal readonly List<HandlerGameObjectState> States = new List<HandlerGameObjectState>();
            // doors only: the shut leaf's colliders, for "a bot walked up to it"
            internal Bounds? Leaf;
            // the bot that unlocked it, and when to open the door for it if it has not
            internal Player Crosser;
            internal float OpenAt;
            // one-sided: the door's own link (LockedDoorCarvers), blocked a while after a far-side refusal
            internal NavMeshDoorLink Link;
            internal float BlockUntil;
            // where the padlock itself hangs: the objects its shot hides (the padlock mesh),
            // else the lock's ballistic collider
            internal Vector3? PadPos;

            internal Vector3 LockPos => Lock != null ? Lock.bounds.center : Trigger.transform.position;
            internal string Name => Target.Id;
        }

        // how far from the lock a bot's gun is heard as the shot
        private const float SoundRange = 15f;

        private static readonly AccessTools.FieldRef<TriggerBallistic, bool> IsDone =
            AccessTools.FieldRefAccess<TriggerBallistic, bool>("_isDone");

        private static List<Entry> _entries;
        private static GameWorld _builtFor;

        internal static List<Entry> Entries
        {
            get
            {
                var world = Singleton<GameWorld>.Instantiated ? Singleton<GameWorld>.Instance : null;
                if (_entries == null || _builtFor != world || _entries.Any(e => e.Target == null || e.Trigger == null))
                {
                    _builtFor = world;
                    _entries = world != null ? Build() : new List<Entry>();
                }
                return _entries;
            }
        }

        // Built on first use in a raid, so trigger ids renamed at runtime are final.
        private static List<Entry> Build()
        {
            var list = new List<Entry>();
            var byId = new Dictionary<string, WorldInteractiveObject>();
            foreach (var wio in Object.FindObjectsOfType<WorldInteractiveObject>(true))
                if (wio != null && !string.IsNullOrEmpty(wio.Id) && BundleSceneLoader.Owns(wio.gameObject.scene.path))
                    byId[wio.Id] = wio;
            var handlers = Object.FindObjectsOfType<BaseTriggerHandler>(true)
                .Where(h => h != null && !string.IsNullOrEmpty(h._triggerId)).ToLookup(h => h._triggerId);
            foreach (var trigger in Object.FindObjectsOfType<TriggerBallistic>(true))
            {
                if (trigger == null || string.IsNullOrEmpty(trigger._triggerId)) continue;
                if (!BundleSceneLoader.Owns(trigger.gameObject.scene.path)) continue;
                var opener = handlers[trigger._triggerId].OfType<HandlerInteractable>().FirstOrDefault();
                if (opener == null || string.IsNullOrEmpty(opener._interactableId)) continue;
                if (!byId.TryGetValue(opener._interactableId, out var target)) continue;
                var entry = new Entry
                {
                    Target = target,
                    Trigger = trigger,
                    Lock = trigger._targetCollider != null ? trigger._targetCollider.GetComponent<Collider>() : null
                };
                foreach (var h in handlers[trigger._triggerId])
                    if (h is HandlerGameObjectState s) entry.States.Add(s);
                if (target is Door)
                    foreach (var c in target.GetComponentsInChildren<Collider>())
                    {
                        if (c == null || c.isTrigger) continue;
                        if (entry.Leaf is Bounds b) { b.Encapsulate(c.bounds); entry.Leaf = b; }
                        else entry.Leaf = c.bounds;
                    }
                list.Add(entry);
                Watch(entry);
            }
            Plugin.Verbose($"Padlocks: {list.Count} padlocked object(s): {string.Join(", ", list.Select(e => e.Name))}");
            return list;
        }

        internal static bool WasShot(Entry e) => IsDone(e.Trigger);

        // The doorway in or out of the navmesh (closed carver of the door's link).
        internal static void Block(Entry e, bool on)
        {
            var c = e.Link != null ? e.Link.Carver_Closed : null;
            if (c == null) return;
            c.enabled = on;
            c.carving = on;
        }

        // Padlocked-door events: VerboseLogs only.
        internal static void Event(Entry e, string message) => Plugin.Verbose($"Padlock {e.Name}: {message}");

        // The door's face normal, pointing to the padlock's side.
        internal static Vector3 Normal(Entry e)
        {
            var t = e.Target.transform;
            var n = Vector3.Cross(WorldInteractiveObject.GetRotationAxis(e.Target.DoorForward, t),
                                  WorldInteractiveObject.GetRotationAxis(e.Target.DoorAxis, t));
            n.y = 0f;
            n.Normalize();
            var center = e.Leaf?.center ?? t.position;
            if (SideOverride.TryGetValue(e.Name, out var dir))
                return Vector3.Dot(dir, n) >= 0f ? n : -n;
            if (Vector3.Dot(PadPos(e) - center, n) < 0f) n = -n;
            return n;
        }

        // > 0 padlock side, < 0 far side.
        internal static float SideOf(Entry e, Vector3 pos) =>
            Vector3.Dot(pos - (e.Leaf?.center ?? e.Target.transform.position), Normal(e));

        // Doors whose padlock hangs in the door plane (no face to read the side from): the side
        // it is shot from, by hand. Old Gas <-> Customs: from the Old Gas side (east, +x).
        private static readonly Dictionary<string, Vector3> SideOverride = new Dictionary<string, Vector3>
        {
            ["door_custom_AZS_old_00003"] = new Vector3(1f, 0f, 0f),
        };

        internal static Vector3 PadPos(Entry e)
        {
            if (e.PadPos is Vector3 known) return known;
            Bounds? b = null;
            foreach (var st in e.States)
                if (st != null && st._controlledGameObjects != null)
                    foreach (var go in st._controlledGameObjects)
                        if (go != null)
                            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                            {
                                if (r == null) continue;
                                if (b is Bounds bb) { bb.Encapsulate(r.bounds); b = bb; } else b = r.bounds;
                            }
            var pos = b?.center ?? e.LockPos;
            // a hidden padlock's renderer bounds can be empty: fall back to the collider
            if (b is Bounds rb && rb.size.sqrMagnitude < 1e-6f) pos = e.LockPos;
            e.PadPos = pos;
            return pos;
        }


        internal static void ShowPadlock(Entry e, bool on)
        {
            foreach (var s in e.States)
                if (s != null && s._controlledGameObjects != null)
                    foreach (var go in s._controlledGameObjects)
                        if (go != null) go.SetActive(on ? !s._targetState : s._targetState);
        }

        // Opened without a shot (an AI mod's Unlock, a debug key): padlock off, trigger
        // spent, and a shot heard from the nearest bot.
        private static void Watch(Entry e)
        {
            e.Target.OnDoorStateChanged += (obj, prev, next) =>
            {
                if (e.Leaf != null) Event(e, $"door {prev} -> {next}");
                // Fika host: tell the clients (no-op elsewhere)
                if (e.Leaf != null && (next == EDoorState.Locked || next == EDoorState.Shut || next == EDoorState.Open))
                    PadlockSync.HostSend(e.Name, next);
                if (prev != EDoorState.Locked || next == EDoorState.Locked || e.Trigger == null || WasShot(e)) return;
                IsDone(e.Trigger) = true;
                e.BlockUntil = 0f;
                Block(e, false);
                ShowPadlock(e, false);
                var who = PlayShotSound(e);
                Event(e, "padlock removed" + (who != null ? $", shot sound from {who}'s gun" : ", no armed bot within 15 m for the shot sound"));
            };
        }

        // Fika client: the host changed a padlocked door; do the same here.
        internal static void ApplyHostState(string id, EDoorState state)
        {
            var e = Entries.Find(x => x.Name == id && x.Leaf != null);
            if (e == null) return;
            var w = e.Target;
            if (w.DoorState == state) return;
            Event(e, $"host: door {state} (here {w.DoorState})");
            switch (state)
            {
                case EDoorState.Locked:
                    w.Lock();
                    if (e.Trigger != null) IsDone(e.Trigger) = false;
                    ShowPadlock(e, true);
                    break;
                case EDoorState.Shut:
                    if (w.DoorState == EDoorState.Locked) w.SetDoorState(EDoorState.Shut, true);
                    else if (w is Door ds) ds.Close();
                    break;
                case EDoorState.Open:
                    if (w.DoorState == EDoorState.Locked) w.SetDoorState(EDoorState.Shut, true);
                    if (w is Door d) d.Open();
                    break;
            }
        }

        // Best effort: the gun of the nearest armed bot fires a sound at the lock. No bullet.
        private static string PlayShotSound(Entry e)
        {
            try
            {
                if (!Singleton<GameWorld>.Instantiated) return null;
                var lockPos = e.LockPos;
                // on a Fika client the bots are observed players (IsAI false)
                var fikaClient = PadlockSync.IsClient();
                var bot = Singleton<GameWorld>.Instance.AllAlivePlayersList
                    .Where(p => p != null && (p.IsAI || (fikaClient && !p.IsYourPlayer)) && p.HandsController is Player.FirearmController
                                && (p.Position - lockPos).sqrMagnitude <= SoundRange * SoundRange)
                    .OrderBy(p => (p.Position - lockPos).sqrMagnitude).FirstOrDefault();
                if (bot == null) return null;
                var fc = (Player.FirearmController)bot.HandsController;
                var weapon = fc.Weapon;
                var ammo = weapon.Chambers?.Select(c => c.ContainedItem as Ammo).FirstOrDefault(a => a != null)
                           ?? weapon.GetCurrentMagazine()?.FirstRealAmmo() as Ammo;
                if (ammo == null || fc.WeaponSoundPlayer == null) return null;
                var from = fc.FireportPosition;
                fc.PlayFireBulletSound(fc.WeaponSoundPlayer, ammo, from, (lockPos - from).normalized, false);
                return bot.Profile.Nickname;
            }
            catch (System.Exception error)
            {
                Plugin.Log.LogWarning("Padlock shot sound failed: " + error.Message);
                return null;
            }
        }
    }

    // Padlocked doors are left in the navmesh (LockedDoorCarvers does not carve them), so
    // any bot (vanilla, SAIN, ORBIT) may path through one like a normal door. When a bot
    // reaches a padlocked door that is still locked, it is unlocked (padlock pops, shot
    // sound, see Padlocks.Watch) and the bot's own door logic opens it.
    //
    // It acts only where the bots run: single player, or the Fika host/headless. It is
    // added on every machine, but on a Fika client the bots are observed players with
    // IsAI false, so it finds no bot there and does nothing. Clients get the door
    // instead from the host's PadlockDoorPacket (PadlockSync -> Padlocks.ApplyHostState),
    // because Fika does not send an Unlock() or Open() made in code.
    internal sealed class PadlockWatcher : MonoBehaviour
    {
        private const float Interval = 0.25f;
        private const float ArriveSq = 1.5f * 1.5f;
        private const float BlockSeconds = 90f;
        private float _next;

        private void Update()
        {
            if (Time.time < _next) return;
            _next = Time.time + Interval;
            if (!BundleSceneLoader.Active || !Singleton<GameWorld>.Instantiated) return;
            var world = Singleton<GameWorld>.Instance;
            // not MainPlayer: a Fika headless host has none, and it is the machine that runs the bots
            if (world.AllAlivePlayersList == null || world.AllAlivePlayersList.Count == 0) return;
            try
            {
                foreach (var e in Padlocks.Entries)
                {
                    if (!(e.Leaf is Bounds leaf)) continue;
                    // safety net: the bot that unlocked it is still pressed against the shut door
                    // (its own door logic did not open it): open it, as ORBIT's ghost opener does
                    if (e.OpenAt > 0f && Time.time >= e.OpenAt)
                    {
                        e.OpenAt = 0f;
                        var b = e.Crosser;
                        if (b != null && b.HealthController.IsAlive && e.Target is Door d && d.DoorState == EDoorState.Shut
                            && leaf.SqrDistance(b.Position + Vector3.up) < 4f)
                        {
                            d.Open();
                            Padlocks.Event(e, $"{b.Profile.Nickname} still at the shut door after 3 s: opened it for the bot");
                        }
                    }
                    if (e.BlockUntil > 0f && Time.time >= e.BlockUntil)
                    {
                        e.BlockUntil = 0f;
                        if (e.Target.DoorState == EDoorState.Locked) Padlocks.Block(e, false);
                    }
                    if (e.Target.DoorState != EDoorState.Locked || Padlocks.WasShot(e)) continue;
                    foreach (var p in world.AllAlivePlayersList)
                    {
                        if (p == null || !p.IsAI) continue;
                        if (leaf.SqrDistance(p.Position + Vector3.up) > ArriveSq) continue;
                        var side = Padlocks.SideOf(e, p.Position);
                        if (side < 0f)
                        {
                            // far side: no padlock to shoot from here. Take the doorway out of the
                            // navmesh for a while so this bot re-plans around, then put it back.
                            if (e.BlockUntil > Time.time) continue;
                            e.BlockUntil = Time.time + BlockSeconds;
                            Padlocks.Block(e, true);
                            Padlocks.Event(e, $"{p.Profile.Nickname} reached it from the far side: refused, doorway blocked {BlockSeconds:0} s");
                            continue;
                        }
                        Padlocks.Event(e, $"{p.Profile.Nickname} reached it, unlocking");
                        e.Crosser = p;
                        e.OpenAt = Time.time + 3f;
                        e.Target.Unlock();
                        break;
                    }
                }
            }
            catch (System.Exception error)
            {
                Plugin.Log.LogError("Padlock watcher: " + error);
                _next = Time.time + 10f;
            }
        }

    }

    // For AI mods (by reflection): whether an object is still padlocked.
    public static class PadlockApi
    {
        public static bool IsPadlocked(WorldInteractiveObject target)
        {
            if (!BundleSceneLoader.Active || target == null) return false;
            var e = Padlocks.Entries.FirstOrDefault(x => x.Target == target);
            return e != null && !Padlocks.WasShot(e);
        }
    }
}

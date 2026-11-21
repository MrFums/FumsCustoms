using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Fums.Customs.Client
{
    // 1.3's spatial audio rooms play their room tone (amb_factory2 in the industrial
    // buildings, amb_residential elsewhere) at RoomToneVolume 1.0 in 351 of 398
    // rooms. Every one of 0.16.9's 397 Customs rooms uses 0.4, and 0.16.9's mixer is
    // balanced for that. Rooms are capped at the 0.16.9 value when the backport
    // scene loads. (This was not the cause of the indoor hiss; that was the steam
    // loop's rolloff curve, see CurveWrapFix.)
    internal static class RoomToneVolume
    {
        private const float Vanilla = 0.4f;
        private const float VanillaHighCut = 0.5f;

        internal static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (!BundleSceneLoader.Owns(scene.path)) return;
            try
            {
                var roomType = AccessTools("SpatialAudioRoom");
                if (roomType == null) return;
                var dataField = roomType.GetField("AmbientData", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var volumeField = dataField?.FieldType.GetField("RoomToneVolume", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (volumeField == null)
                {
                    Plugin.Log.LogWarning("Room tone volume: SpatialAudioRoom.AmbientData.RoomToneVolume not found");
                    return;
                }
                var highCutField = dataField.FieldType.GetField("OutdoorAmbientHighCutFreq", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                int rooms = 0, lowered = 0, highCut = 0;
                foreach (var root in scene.GetRootGameObjects())
                {
                    foreach (var room in root.GetComponentsInChildren(roomType, true))
                    {
                        rooms++;
                        var data = dataField.GetValue(room);
                        if (data == null) continue;
                        bool changed = false;
                        if ((float)volumeField.GetValue(data) > Vanilla)
                        {
                            volumeField.SetValue(data, Vanilla);
                            lowered++; changed = true;
                        }
                        // 1.3 gives the indoor high cut in Hz (20 to 22000); 0.16.9 reads it
                        // as 0..1 and every vanilla room uses 0.5. Out of range, the filter
                        // on outdoor sound turns into a constant hiss indoors.
                        if (highCutField != null && (float)highCutField.GetValue(data) > 1f)
                        {
                            highCutField.SetValue(data, VanillaHighCut);
                            highCut++; changed = true;
                        }
                        if (changed && dataField.FieldType.IsValueType) dataField.SetValue(room, data);
                    }
                }
                if (rooms > 0)
                    Plugin.Verbose($"Room audio {scene.name}: {rooms} rooms; room tone lowered to {Vanilla} in {lowered}, " +
                                       $"high cut set to {VanillaHighCut} in {highCut}{(highCutField == null ? " (high cut field not found)" : "")}");
            }
            catch (Exception error)
            {
                Plugin.Log.LogError("Room tone volume failed in " + scene.name + ": " + error);
            }
        }

        private static Type AccessTools(string name) => HarmonyLib.AccessTools.TypeByName(name);
    }
}

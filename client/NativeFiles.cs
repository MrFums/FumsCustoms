#pragma warning disable CS0649 // fields are filled by Json.NET
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Newtonsoft.Json;
using UnityEngine;

namespace Fums.Customs.Client
{
    // The scenes bundle does not ship textures, meshes, cubemaps or sounds that
    // SPT's own EscapeFromTarkov_Data files already hold: it references them in
    // those files (analysis/native_reuse). bundles/native_files.json lists each
    // file it relies on with its size and sha256. A different client build would
    // hand the bundle the wrong objects, so every file is checked before a raid
    // and the map falls back to vanilla Customs on any mismatch.
    // Hashes are cached by size + last write time, so only the first raid pays.
    internal static class NativeFiles
    {
        private sealed class Manifest
        {
            public string Bundle;
            public long BundleBytes;
            public List<Entry> NativeFiles = new List<Entry>();
        }

        private sealed class Entry
        {
            public string Path;
            public long Size;
            public string Sha256;
        }

        private sealed class CacheEntry
        {
            public long Size;
            public long Ticks;
            public string Sha256;
        }

        private static string ManifestPath => Path.Combine(Plugin.Root, "bundles", "native_files.json");
        private static string CachePath => Path.Combine(Plugin.Root, "native_files.cache.json");
        private static bool? _verdict;

        // Runs on a worker thread. Returns null when the bundle may load, else the reason it may not.
        internal static string Check(string dataPath, string bundlePath)
        {
            if (_verdict == true) return null;
            if (!File.Exists(ManifestPath)) return null; // a bundle that ships everything needs no check
            var manifest = JsonConvert.DeserializeObject<Manifest>(File.ReadAllText(ManifestPath));
            if (manifest.BundleBytes > 0 && new FileInfo(bundlePath).Length != manifest.BundleBytes)
                return $"scenes bundle is {new FileInfo(bundlePath).Length} bytes, native_files.json expects {manifest.BundleBytes}";
            var cache = LoadCache();
            var dirty = false;
            foreach (var entry in manifest.NativeFiles)
            {
                var path = Path.Combine(dataPath, entry.Path);
                if (!File.Exists(path)) return "missing SPT client file " + entry.Path;
                var info = new FileInfo(path);
                if (info.Length != entry.Size) return $"SPT client file {entry.Path} is {info.Length} bytes, expected {entry.Size}";
                var ticks = info.LastWriteTimeUtc.Ticks;
                if (!cache.TryGetValue(entry.Path, out var known) || known.Size != info.Length || known.Ticks != ticks)
                {
                    known = new CacheEntry { Size = info.Length, Ticks = ticks, Sha256 = Hash(path) };
                    cache[entry.Path] = known;
                    dirty = true;
                }
                if (!string.Equals(known.Sha256, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                    return $"SPT client file {entry.Path} does not match the build this bundle was made for";
            }
            if (dirty) SaveCache(cache);
            _verdict = true;
            Plugin.Verbose($"Native SPT files checked: {manifest.NativeFiles.Count} match the bundle");
            return null;
        }

        private static string Hash(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        private static Dictionary<string, CacheEntry> LoadCache()
        {
            try
            {
                if (File.Exists(CachePath))
                    return JsonConvert.DeserializeObject<Dictionary<string, CacheEntry>>(File.ReadAllText(CachePath))
                           ?? new Dictionary<string, CacheEntry>();
            }
            catch (Exception error)
            {
                Plugin.Log.LogWarning("Native file hash cache unreadable, rebuilding: " + error.Message);
            }
            return new Dictionary<string, CacheEntry>();
        }

        private static void SaveCache(Dictionary<string, CacheEntry> cache)
        {
            try { File.WriteAllText(CachePath, JsonConvert.SerializeObject(cache, Formatting.Indented)); }
            catch (Exception error) { Plugin.Log.LogWarning("Native file hash cache not saved: " + error.Message); }
        }
    }
}

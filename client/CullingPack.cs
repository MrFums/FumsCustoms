#pragma warning disable CS0649 // fields are filled by Json.NET
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using ComponentAce.Compression.Libs.zlib;
using Newtonsoft.Json;
using UnityEngine;

namespace Fums.Customs.Client
{
    // The live 1.3 occlusion culling bake ('<hash>_packed_cull.bytes', 627 MB) is
    // one zlib stream per cell, which does not compress any further. It ships
    // instead as its cells inflated ("FCUL" stream, analysis/cullpack/cullpack.py)
    // inside LZMA asset bundles (Culling_Pack/cull_NN.bundle, about 40% of the size).
    // On game start the stream is read back here, every cell is deflated again and
    // the cell table is rebuilt, giving a file in the exact format the game reads.
    // The visibility data is the same bit for bit, so culling, quality and frame
    // rate are unchanged. Runs once; a stamp keeps it until the pack changes.
    //
    // Packed layout (Koenigz.PerfectCulling.EFT.PackedCullingGridData):
    //   int32 groupCount, groupCount x (int32 len, guid bytes)
    //   cells: 40 bytes bounds, int32 dataSize, dataSize bytes zlib
    //   int32[numCells] cell offsets, int32 numCells
    internal static class CullingPack
    {
        private sealed class Manifest
        {
            public string Source;
            public long StreamBytes;
            public string StreamSha256;
            public int Cells;
            public List<Chunk> Chunks = new List<Chunk>();
        }

        private sealed class Chunk
        {
            public string File;
            public long Bytes;
        }

        private const int BoundsSize = 40;
        private const int MaxCell = 262140; // the game's inflate buffer
        private const int Batch = 2048;
        private static readonly TaskCompletionSource<bool> Done = new TaskCompletionSource<bool>();

        private static string PackDir => Path.Combine(Plugin.Root, "Culling_Pack");
        private static string DataDir => Path.Combine(Plugin.Root, "Culling_Data");

        // True once the culling bake is in place; false if it could not be built.
        internal static Task<bool> WhenReady => Done.Task;
        internal static bool Ready => Done.Task.IsCompleted && Done.Task.Result;

        internal static void Begin(MonoBehaviour host)
        {
            try
            {
                var manifestPath = Path.Combine(PackDir, "cullpack.json");
                if (!File.Exists(manifestPath))
                {
                    // older layout: the bake shipped unpacked
                    Done.TrySetResult(Directory.Exists(DataDir) && Directory.GetFiles(DataDir, "*_packed_cull.bytes").Length > 0);
                    return;
                }
                var manifest = JsonConvert.DeserializeObject<Manifest>(File.ReadAllText(manifestPath));
                var target = Path.Combine(DataDir, manifest.Source);
                var stamp = target + ".stamp";
                if (File.Exists(target) && File.Exists(stamp) && File.ReadAllText(stamp).Trim() == manifest.StreamSha256)
                {
                    Done.TrySetResult(true);
                    return;
                }
                host.StartCoroutine(Rebuild(manifest, target, stamp));
            }
            catch (Exception error)
            {
                Plugin.Log.LogError("Culling pack: could not start: " + error);
                Done.TrySetResult(false);
            }
        }

        private static IEnumerator Rebuild(Manifest manifest, string target, string stamp)
        {
            Plugin.Log.LogInfo($"Culling pack: building {manifest.Source} from {manifest.Chunks.Count} chunk(s) (first run only)");
            var started = DateTime.UtcNow;
            var queue = new BlockingCollection<byte[]>(2);
            var worker = Task.Run(() => Write(manifest, target, queue));
            foreach (var chunk in manifest.Chunks)
            {
                var path = Path.Combine(PackDir, Path.ChangeExtension(chunk.File, ".bundle"));
                var load = AssetBundle.LoadFromFileAsync(path);
                yield return load;
                var bundle = load.assetBundle;
                if (bundle == null)
                {
                    Plugin.Log.LogError("Culling pack: Unity could not open " + path);
                    break;
                }
                var assets = bundle.LoadAllAssetsAsync<TextAsset>();
                yield return assets;
                var text = assets.asset as TextAsset;
                var bytes = text != null ? text.bytes : null;
                bundle.Unload(true);
                if (bytes == null || bytes.Length != chunk.Bytes)
                {
                    Plugin.Log.LogError($"Culling pack: {chunk.File} holds {bytes?.Length ?? -1} bytes, expected {chunk.Bytes}");
                    break;
                }
                while (!queue.TryAdd(bytes) && !worker.IsCompleted) yield return null;
                if (worker.IsCompleted) break;
            }
            queue.CompleteAdding();
            while (!worker.IsCompleted) yield return null;
            var ok = !worker.IsFaulted && worker.Result;
            if (worker.IsFaulted) Plugin.Log.LogError("Culling pack: build failed: " + worker.Exception);
            if (ok)
            {
                File.WriteAllText(stamp, manifest.StreamSha256);
                Plugin.Log.LogInfo($"Culling pack: {manifest.Source} ready in {(DateTime.UtcNow - started).TotalSeconds:F0}s");
            }
            else
            {
                Plugin.Log.LogError("Culling pack: the 1.3 culling bake is not available; backport raids render without occlusion culling");
            }
            Done.TrySetResult(ok);
        }

        // Worker thread: reads the FCUL stream, deflates cells in parallel, writes the packed file.
        private static bool Write(Manifest manifest, string target, BlockingCollection<byte[]> queue)
        {
            Directory.CreateDirectory(DataDir);
            var temp = target + ".tmp";
            var reader = new ChunkReader(queue);
            using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            using (var writer = new BinaryWriter(output))
            {
                if (reader.ReadInt32() != 0x4C554346 || reader.ReadInt32() != 1) // "FCUL", version 1
                    throw new InvalidDataException("not an FCUL v1 culling stream");
                var header = reader.ReadBytes(reader.ReadInt32());
                writer.Write(header);
                var cells = reader.ReadInt32();
                if (cells != manifest.Cells) throw new InvalidDataException($"stream has {cells} cells, manifest says {manifest.Cells}");
                var offsets = new int[cells];
                var bounds = new byte[Batch][];
                var raw = new byte[Batch][];
                var packed = new byte[Batch][];
                var packedLength = new int[Batch];
                using (var zstreams = new ThreadLocal<Deflater>(() => new Deflater(), true))
                {
                    for (var first = 0; first < cells; first += Batch)
                    {
                        var count = Math.Min(Batch, cells - first);
                        for (var i = 0; i < count; i++)
                        {
                            bounds[i] = reader.ReadBytes(BoundsSize);
                            var length = reader.ReadInt32();
                            if (length > MaxCell) throw new InvalidDataException($"cell {first + i} is {length} bytes");
                            raw[i] = length < 0 ? null : reader.ReadBytes(length);
                        }
                        Parallel.For(0, count, i =>
                        {
                            if (raw[i] == null) { packed[i] = null; packedLength[i] = 0; return; }
                            packed[i] = zstreams.Value.Deflate(raw[i], out packedLength[i]);
                        });
                        for (var i = 0; i < count; i++)
                        {
                            offsets[first + i] = checked((int)output.Position);
                            writer.Write(bounds[i]);
                            writer.Write(packedLength[i]);
                            if (packedLength[i] > 0) writer.Write(packed[i], 0, packedLength[i]);
                        }
                    }
                    foreach (var z in zstreams.Values) z.Dispose();
                }
                foreach (var offset in offsets) writer.Write(offset);
                writer.Write(cells);
                var hash = reader.Finish();
                if (!string.Equals(hash, manifest.StreamSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("culling stream checksum does not match cullpack.json");
            }
            if (File.Exists(target)) File.Delete(target);
            File.Move(temp, target);
            return true;
        }

        private sealed class Deflater : IDisposable
        {
            private readonly ZStream _z = new ZStream();
            private readonly byte[] _out = new byte[MaxCell + MaxCell / 100 + 1024];

            internal Deflater() { _z.deflateInit(zlibConst.Z_DEFAULT_COMPRESSION); }

            internal byte[] Deflate(byte[] input, out int length)
            {
                _z.deflateReset();
                _z.next_in = input;
                _z.next_in_index = 0;
                _z.avail_in = input.Length;
                _z.next_out = _out;
                _z.next_out_index = 0;
                _z.avail_out = _out.Length;
                var result = _z.deflate(zlibConst.Z_FINISH);
                if (result != zlibConst.Z_STREAM_END) throw new InvalidDataException("deflate returned " + result + ": " + _z.msg);
                length = _z.next_out_index;
                var copy = new byte[length];
                Buffer.BlockCopy(_out, 0, copy, 0, length);
                return copy;
            }

            public void Dispose() { _z.deflateEnd(); }
        }

        // Reads across the queued chunks, hashing every byte it hands out.
        private sealed class ChunkReader
        {
            private readonly BlockingCollection<byte[]> _queue;
            private readonly SHA256 _sha = SHA256.Create();
            private byte[] _chunk = new byte[0];
            private int _pos;

            internal ChunkReader(BlockingCollection<byte[]> queue) { _queue = queue; }

            internal byte[] ReadBytes(int count)
            {
                var result = new byte[count];
                var done = 0;
                while (done < count)
                {
                    if (_pos == _chunk.Length)
                    {
                        if (!_queue.TryTake(out _chunk, Timeout.Infinite)) throw new EndOfStreamException("culling stream ended early");
                        _pos = 0;
                    }
                    var take = Math.Min(count - done, _chunk.Length - _pos);
                    Buffer.BlockCopy(_chunk, _pos, result, done, take);
                    _pos += take;
                    done += take;
                }
                _sha.TransformBlock(result, 0, count, null, 0);
                return result;
            }

            internal int ReadInt32() => BitConverter.ToInt32(ReadBytes(4), 0);

            internal string Finish()
            {
                if (_pos != _chunk.Length || _queue.TryTake(out _)) throw new InvalidDataException("culling stream has trailing data");
                _sha.TransformFinalBlock(new byte[0], 0, 0);
                return BitConverter.ToString(_sha.Hash).Replace("-", "").ToLowerInvariant();
            }
        }
    }
}

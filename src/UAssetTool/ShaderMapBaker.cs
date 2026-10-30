#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.UnrealTypes;
using UAssetTool.IoStore;

namespace UAssetTool;

/// <summary>
/// Bakes the shader code a cooked material instance's inline shader maps take from the game's shader
/// library into the material itself (UE 5.3).
/// <para>
/// A cooked shader map ends in <c>bool bShareCode, FName ShaderPlatform</c>, then either the
/// resource hash the engine looks up in the shader library (<c>bShareCode = true</c>) or the code
/// itself (<c>FShaderMapResourceCode</c>: hash, shader hashes, shader entries). A copy of a game
/// material with a static permutation stops rendering when a game update rebuilds the library
/// without its hash; with the code inline it no longer depends on the library at all, and keeps its
/// static switches and base-property overrides.
/// </para>
/// <para>
/// The output is written with property tags: an unversioned copy is misread by any update that
/// adds a property to a struct it uses. The shader maps themselves carry their own name table and
/// no tagged data, so they are unaffected by that.
/// </para>
/// </summary>
public static class ShaderMapBaker
{
    public sealed record Result(byte[] AssetData, byte[] ExportsData, IReadOnlyList<string> Notes);

    /// <param name="source">
    /// The material instance, read with <see cref="CustomSerializationFlags.NoDummies"/> and, when it
    /// is unversioned, the .usmap of the game build it was extracted from.
    /// </param>
    /// <param name="libraries">The shader libraries of the game build the material was extracted from.</param>
    public static Result Bake(UAsset source, ShaderLibrarySet libraries)
    {
        var notes = new List<string>();
        var export = MaterialInstanceRebaser.FindInstance(source);
        var tail = export.Extras ?? [];
        var remap = MaterialInstanceRebaser.ReadCachedData(tail, source, out var shaderBytes);
        var firstMapByte = tail.Length - shaderBytes;

        // [int32 bShareCode = 1][FName platform: int32 index, int32 number][FSHAHash] — the end of each shared-code map.
        var maps = new List<(int At, byte[] Hash, List<ShaderLibrarySet.Shader> Shaders)>();
        for (var i = firstMapByte; i + 32 <= tail.Length; i++)
        {
            if (BitConverter.ToInt32(tail, i) != 1) continue;
            var hash = tail[(i + 12)..(i + 32)];
            if (libraries.GetShaderMap(hash) is not { } shaders) continue;
            maps.Add((i, hash, shaders));
            i += 31;
        }

        if (maps.Count == 0)
        {
            throw new InvalidDataException(
                "No shader map of this material is in the given shader libraries: it has no inline shader maps, " +
                "they are already baked, or they come from another game build.");
        }

        using var output = new MemoryStream();
        using var w = new BinaryWriter(output);
        w.Write(MaterialInstanceRebaser.TaggedTail(source, remap));
        var copied = firstMapByte;
        foreach (var (at, hash, shaders) in maps)
        {
            w.Write(tail, copied, at - copied);
            w.Write(0);                     // bShareCode = false
            w.Write(tail, at + 4, 8);       // same platform FName
            w.Write(hash);                  // FShaderMapResourceCode::ResourceHash
            w.Write(shaders.Count);
            foreach (var shader in shaders) w.Write(shader.Hash);
            w.Write(shaders.Count);
            var bytes = 0L;
            foreach (var shader in shaders)
            {
                // Compressed with the shader compression format the library itself uses; raw when that does not help.
                var packed = OodleCompression.Compress(shader.Code);
                var code = packed is not null && packed.Length < shader.Code.Length ? packed : shader.Code;
                w.Write(code.Length);
                w.Write(code);
                w.Write(shader.Code.Length); // UncompressedSize
                w.Write(shader.Frequency);
                bytes += code.Length;
            }
            notes.Add($"Baked shader map {Convert.ToHexString(hash)[..16]}: {shaders.Count} shaders, {bytes / 1024} KB.");
            copied = at + 32;
        }
        w.Write(tail, copied, tail.Length - copied);
        export.Extras = output.ToArray();

        source.PackageFlags &= ~EPackageFlags.PKG_UnversionedProperties;
        var (uasset, uexp) = MaterialInstanceRebaser.WriteTagged(source);
        return new Result(uasset, uexp, notes);
    }
}

/// <summary>
/// The IoStore shader libraries (<c>FIoStoreShaderCodeArchive</c>, version 1) of a set of containers,
/// resolving a shader-map hash to its shaders' code.
/// </summary>
public sealed class ShaderLibrarySet : IDisposable
{
    public sealed record Shader(byte[] Hash, byte Frequency, byte[] Code);

    private sealed class Library
    {
        public byte[][] ShaderHashes = [];
        public byte[][] GroupChunkIds = [];
        public (int Offset, int Num)[] MapEntries = [];
        public ulong[] ShaderEntries = [];
        public (int Offset, int Num, int Uncompressed, int Compressed)[] Groups = [];
        public int[] Indices = [];
    }

    private readonly List<IoStoreReader> _readers = [];
    private readonly Dictionary<string, (Library Library, int Map)> _maps = new();
    private readonly Dictionary<string, IoStoreReader> _chunks = new();
    private readonly Dictionary<(Library, int), byte[]> _groupCache = new();

    /// <param name="utocPaths">Containers in mount order, lowest priority first; a later library wins for a hash both have.</param>
    public static ShaderLibrarySet Open(IEnumerable<string> utocPaths, byte[]? aesKey)
    {
        var set = new ShaderLibrarySet();
        foreach (var path in utocPaths)
        {
            var reader = new IoStoreReader(path, aesKey);
            set._readers.Add(reader);
            foreach (var chunk in reader.GetChunks())
            {
                switch (chunk.GetChunkType())
                {
                    case EIoChunkType.ShaderCode:
                        set._chunks[Convert.ToHexString(chunk.ToBytes())] = reader;
                        break;
                    case EIoChunkType.ShaderCodeLibrary:
                        var library = Parse(reader.ReadChunk(chunk), out var mapHashes);
                        for (var m = 0; m < mapHashes.Length; m++) set._maps[Convert.ToHexString(mapHashes[m])] = (library, m);
                        break;
                }
            }
        }
        return set;
    }

    public int ShaderMapCount => _maps.Count;

    /// <summary>The shaders of one shader map, in the order its frozen content indexes them; null if no library has it.</summary>
    public List<Shader>? GetShaderMap(byte[] hash)
    {
        if (!_maps.TryGetValue(Convert.ToHexString(hash), out var found)) return null;
        var (lib, map) = found;
        var (offset, count) = lib.MapEntries[map];
        var shaders = new List<Shader>(count);
        for (var i = 0; i < count; i++)
        {
            var index = lib.Indices[offset + i];
            var entry = lib.ShaderEntries[index];
            var group = (int)((entry >> 4) & 0x3FFFFFFF);
            var start = (int)((entry >> 34) & 0x3FFFFFFF);
            var data = Group(lib, group);

            // A shader runs to the next shader of its group, or to the end of the group.
            var (groupOffset, groupCount, _, _) = lib.Groups[group];
            var end = data.Length;
            for (var j = 0; j < groupCount; j++)
            {
                var other = (int)((lib.ShaderEntries[lib.Indices[groupOffset + j]] >> 34) & 0x3FFFFFFF);
                if (other > start && other < end) end = other;
            }
            shaders.Add(new Shader(lib.ShaderHashes[index], (byte)(entry & 0xF), data[start..end]));
        }
        return shaders;
    }

    private byte[] Group(Library lib, int group)
    {
        if (_groupCache.TryGetValue((lib, group), out var cached)) return cached;
        var id = lib.GroupChunkIds[group];
        if (!_chunks.TryGetValue(Convert.ToHexString(id), out var reader))
        {
            throw new InvalidDataException($"Shader group chunk {Convert.ToHexString(id)} is in none of the given containers.");
        }
        var raw = reader.ReadChunk(FIoChunkId.FromBytes(id));
        var (_, _, uncompressed, compressed) = lib.Groups[group];
        var data = compressed == uncompressed
            ? raw[..uncompressed]
            : OodleCompression.Decompress(raw[..compressed], uncompressed)
              ?? throw new InvalidDataException($"Could not decompress shader group {Convert.ToHexString(id)} (Oodle unavailable?).");
        _groupCache[(lib, group)] = data;
        return data;
    }

    private static Library Parse(byte[] data, out byte[][] mapHashes)
    {
        using var r = new BinaryReader(new MemoryStream(data));
        var version = r.ReadUInt32();
        if (version != 1) throw new InvalidDataException($"Unsupported IoStore shader library version {version}.");
        byte[][] Hashes(int size) => Enumerable.Range(0, r.ReadInt32()).Select(_ => r.ReadBytes(size)).ToArray();
        mapHashes = Hashes(20);
        var lib = new Library { ShaderHashes = Hashes(20), GroupChunkIds = Hashes(12) };
        lib.MapEntries = Enumerable.Range(0, r.ReadInt32()).Select(_ => (r.ReadInt32(), r.ReadInt32())).ToArray();
        lib.ShaderEntries = Enumerable.Range(0, r.ReadInt32()).Select(_ => r.ReadUInt64()).ToArray();
        lib.Groups = Enumerable.Range(0, r.ReadInt32()).Select(_ => (r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32())).ToArray();
        lib.Indices = Enumerable.Range(0, r.ReadInt32()).Select(_ => r.ReadInt32()).ToArray();
        return lib;
    }

    public void Dispose()
    {
        foreach (var reader in _readers) reader.Dispose();
    }
}

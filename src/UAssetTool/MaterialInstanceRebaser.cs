#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UAssetAPI;
using UAssetAPI.UnrealTypes;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;

namespace UAssetTool;

/// <summary>
/// Turns a copy of a game material instance into a <b>child</b> of that game material instance.
/// <para>
/// A copied MI that owns a static permutation (any static-switch or base-property override) carries
/// the game's compiled inline shader maps, which find their shaders in the game's shader library by
/// hash. A game patch that rebuilds the library drops those hashes and the copy renders with the
/// default material (WorldGrid). A child with no permutation of its own renders through its parent's
/// — the game's current one, re-cooked with every patch — while its parameter values still apply.
/// </para>
/// <para>
/// The output is written with property tags so a later change to the game's property layout cannot
/// misread it.
/// </para>
/// </summary>
public static class MaterialInstanceRebaser
{
    public sealed record Result(byte[] AssetData, byte[] ExportsData, IReadOnlyList<string> Notes);

    /// <summary>Properties that give an MI its own permutation, or only matter when it has one.</summary>
    private static readonly HashSet<string> PermutationProperties =
        ["bHasStaticPermutationResource", "StaticParametersRuntime", "StaticParameters", "BasePropertyOverrides"];

    /// <param name="source">
    /// The copied MI, read with <see cref="CustomSerializationFlags.NoDummies"/> (and, when it is
    /// unversioned, the .usmap of the game build it was extracted from).
    /// </param>
    /// <param name="parentPackage">Game package of the new parent, e.g. <c>/Game/Marvel/Characters/1031/1031305/Materials/MI_1031305_Hair_01</c>.</param>
    /// <param name="parent">Optional: the parent itself, to report static settings the child gives up.</param>
    public static Result Rebase(UAsset source, string parentPackage, UAsset? parent = null)
    {
        if (!parentPackage.StartsWith('/') || parentPackage.Contains(':') || parentPackage.Contains('\\') || parentPackage.EndsWith('/'))
        {
            throw new ArgumentException($"'{parentPackage}' is not a package path such as /Game/Folder/MI_Name.", nameof(parentPackage));
        }

        if (string.Equals(parentPackage, source.FolderName?.Value, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("A material instance cannot be its own parent: give the copy a new path, or rebase it onto its current parent.", nameof(parentPackage));
        }

        var notes = new List<string>();
        var export = FindInstance(source);

        var parentClass = parent?.Exports.OfType<NormalExport>().FirstOrDefault()?.GetExportClassType()?.Value?.Value
                          ?? "MaterialInstanceConstant";
        RepointParent(source, export, parentPackage, parentClass, notes);

        var dropped = export.Data.Where(p => PermutationProperties.Contains(p.Name.Value.Value)).ToList();
        if (parent is not null) ReportLostStatics(dropped, parent, notes);
        foreach (var property in dropped) export.Data.Remove(property);
        notes.Add(dropped.Count == 0
            ? "The copy had no static permutation."
            : $"Removed {string.Join(", ", dropped.Select(p => p.Name.Value.Value))}.");

        var remap = ReadCachedData(export.Extras ?? [], source, out var shaderBytes);
        if (shaderBytes > 0) notes.Add($"Dropped {shaderBytes} bytes of inline shader maps.");

        source.PackageFlags &= ~EPackageFlags.PKG_UnversionedProperties;
        export.Extras = TaggedTail(source, remap);

        var (asset, exports) = WriteTagged(source);
        return new Result(asset, exports, notes);
    }

    /// <summary>The package's material instance. UAssetAPI turns an export it cannot parse into a raw one without saying so.</summary>
    internal static NormalExport FindInstance(UAsset source)
    {
        if (source.Exports.Any(e => e is RawExport))
        {
            throw new InvalidDataException("The material could not be parsed: use the mappings of the game build it was extracted from.");
        }

        return source.Exports.OfType<NormalExport>()
            .SingleOrDefault(e => e.GetExportClassType()?.Value?.Value == "MaterialInstanceConstant")
            ?? throw new InvalidDataException("The package has no single MaterialInstanceConstant export.");
    }

    private static void RepointParent(UAsset asset, NormalExport export, string parentPackage, string parentClass, List<string> notes)
    {
        var property = export.Data.OfType<ObjectPropertyData>().FirstOrDefault(p => p.Name.Value.Value == "Parent")
                       ?? throw new InvalidDataException("The material instance has no Parent.");
        var objectName = parentPackage[(parentPackage.LastIndexOf('/') + 1)..];

        // Reuse the old parent's two imports when nothing else hangs off them, so the package does
        // not keep a dependency on a material it no longer uses.
        if (property.Value.IsImport())
        {
            var old = property.Value.ToImport(asset);
            // Rebasing onto the current parent (an in-place override whose own overrides are redundant):
            // only the permutation goes, the import stays exactly as it is.
            if (old.OuterIndex.IsImport() && string.Equals(old.OuterIndex.ToImport(asset).ObjectName.ToString(), parentPackage, StringComparison.OrdinalIgnoreCase))
            {
                notes.Add($"Parent kept: {parentPackage}.");
                return;
            }
            var outerShared = asset.Imports.Count(i => i.OuterIndex.Index == old.OuterIndex.Index) > 1;
            if (!outerShared && old.OuterIndex.IsImport())
            {
                var package = old.OuterIndex.ToImport(asset);
                notes.Add($"Parent {package.ObjectName.Value.Value} -> {parentPackage}.");
                package.ObjectName = SplitName(asset, parentPackage);
                old.ObjectName = SplitName(asset, objectName);
                old.ClassName = new FName(asset, parentClass);
                return;
            }
        }

        var packageIndex = asset.AddImport(new Import("/Script/CoreUObject", "Package", FPackageIndex.FromRawIndex(0), parentPackage, false, asset));
        var objectIndex = asset.AddImport(new Import("/Script/Engine", parentClass, packageIndex, objectName, false, asset));
        asset.Imports[objectIndex.Index * -1 - 1].ObjectName = SplitName(asset, objectName);
        asset.Imports[packageIndex.Index * -1 - 1].ObjectName = SplitName(asset, parentPackage);
        property.Value = objectIndex;
        notes.Add($"Parent -> {parentPackage} (new import).");
    }

    /// <summary>UE stores a trailing <c>_N</c> (no leading zero) as number N+1: <c>SK_1031_1031305</c> = <c>SK_1031</c> #1031306.</summary>
    private static FName SplitName(UAsset asset, string text)
    {
        var underscore = text.LastIndexOf('_');
        var digits = underscore > 0 ? text[(underscore + 1)..] : "";
        if (digits.Length is > 0 and < 10 && digits.All(char.IsAsciiDigit) && (digits.Length == 1 || digits[0] != '0'))
        {
            return new FName(asset, text[..underscore], int.Parse(digits) + 1);
        }
        return new FName(asset, text);
    }

    private static void ReportLostStatics(List<PropertyData> dropped, UAsset parent, List<string> notes)
    {
        var parentExport = parent.Exports.OfType<NormalExport>().First();
        var mine = dropped.Where(p => p.Name.Value.Value != "bHasStaticPermutationResource").SelectMany(p => Flatten(p, "")).ToHashSet();
        var theirs = parentExport.Data.Where(p => PermutationProperties.Contains(p.Name.Value.Value) && p.Name.Value.Value != "bHasStaticPermutationResource")
            .SelectMany(p => Flatten(p, "")).ToHashSet();
        var lost = mine.Except(theirs).ToList();
        notes.Add(lost.Count == 0
            ? "Static switches and base-property overrides match the parent: nothing is lost."
            : "Differs from the parent and is lost: " + string.Join("; ", lost));
    }

    /// <summary>Leaf values as <c>path=value</c>; array elements are labelled by their parameter name, GUIDs skipped.</summary>
    private static IEnumerable<string> Flatten(PropertyData p, string path)
    {
        path = path.Length == 0 ? p.Name.Value.Value : path + "." + p.Name.Value.Value;
        switch (p)
        {
            case StructPropertyData s:
                foreach (var child in s.Value)
                    if (child.Name.Value.Value != "ExpressionGUID")
                        foreach (var leaf in Flatten(child, path)) yield return leaf;
                break;
            case ArrayPropertyData a:
                foreach (var element in a.Value)
                {
                    var label = ParameterName(element) ?? "?";
                    foreach (var leaf in Flatten(element, path + "[" + label + "]")) yield return leaf;
                }
                break;
            default:
                yield return path + "=" + p;
                break;
        }
    }

    private static string? ParameterName(PropertyData element) =>
        (element as StructPropertyData)?.Value.OfType<StructPropertyData>().FirstOrDefault(c => c.Name.Value.Value == "ParameterInfo")
            ?.Value.FirstOrDefault(c => c.Name.Value.Value == "Name")?.ToString();

    /// <summary>
    /// The post-property tail of a UE 5.3 material instance is
    /// <c>int32 bSavedCachedExpressionData (0 for an MI), int32 bSavedCachedData,
    /// [FMaterialInstanceCachedData], [inline shader maps when bHasStaticPermutationResource]</c>.
    /// Returns <c>ParentLayerIndexRemap</c>, the cached struct's only property.
    /// </summary>
    internal static List<int> ReadCachedData(byte[] tail, UAsset asset, out int shaderBytes)
    {
        var remap = new List<int>();
        using var r = new BinaryReader(new MemoryStream(tail));
        if (r.ReadInt32() != 0) throw new InvalidDataException("Unexpected cached expression data: not a plain material instance.");
        if (r.ReadInt32() != 0)
        {
            if (asset.HasUnversionedProperties) ReadUnversionedRemap(r, remap);
            else ReadTaggedRemap(r, asset, remap);
        }
        shaderBytes = (int)(tail.Length - r.BaseStream.Position);
        return remap;
    }

    private static void ReadUnversionedRemap(BinaryReader r, List<int> remap)
    {
        // FUnversionedHeader: uint16 fragments (7-bit skip, 0x80 has-zeroes, 0x100 last, value count << 9).
        int skip = -1, values = 0, zeroValues = 0;
        ushort fragment;
        do
        {
            fragment = r.ReadUInt16();
            if (skip < 0) skip = fragment & 0x7F;
            values += fragment >> 9;
            if ((fragment & 0x80) != 0) zeroValues += fragment >> 9;
        } while ((fragment & 0x100) == 0);

        if (values == 0) return;
        if (skip != 0 || values != 1) throw new InvalidDataException("Unexpected FMaterialInstanceCachedData layout.");
        var isZero = zeroValues > 0 && (r.ReadByte() & 1) != 0;
        if (isZero) return;

        var count = r.ReadInt32();
        for (var i = 0; i < count; i++) remap.Add(r.ReadInt32());
    }

    private static void ReadTaggedRemap(BinaryReader r, UAsset asset, List<int> remap)
    {
        while (true)
        {
            var name = asset.GetNameReference(r.ReadInt32()).Value; r.ReadInt32();
            if (name == "None") return;
            var type = asset.GetNameReference(r.ReadInt32()).Value; r.ReadInt32();
            var size = r.ReadInt32();
            r.ReadInt32(); // ArrayIndex
            if (type == "ArrayProperty") r.ReadInt64(); // InnerType
            if (r.ReadByte() != 0) r.ReadBytes(16); // property GUID
            var value = r.ReadBytes(size);
            if (name != "ParentLayerIndexRemap") continue;
            for (var i = 1; i <= BitConverter.ToInt32(value, 0); i++) remap.Add(BitConverter.ToInt32(value, i * 4));
        }
    }

    /// <summary>The tail of a tagged (UE-cooked) MI, up to where any inline shader maps would start.</summary>
    internal static byte[] TaggedTail(UAsset asset, List<int> remap)
    {
        int Name(string s) => asset.AddNameReference(new FString(s));
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(0); // bSavedCachedExpressionData
        w.Write(1); // bSavedCachedData
        w.Write(Name("ParentLayerIndexRemap")); w.Write(0);
        w.Write(Name("ArrayProperty")); w.Write(0);
        w.Write(4 + 4 * remap.Count);
        w.Write(0); // ArrayIndex
        w.Write(Name("IntProperty")); w.Write(0);
        w.Write((byte)0); // no property GUID
        w.Write(remap.Count);
        foreach (var index in remap) w.Write(index);
        w.Write(Name("None")); w.Write(0);
        return ms.ToArray();
    }

    /// <summary>
    /// Tagged writing needs names (property types, struct names) an unversioned package never stored;
    /// UAssetAPI refuses to add names mid-write, so add each one it asks for and retry.
    /// </summary>
    internal static (byte[] Asset, byte[] Exports) WriteTagged(UAsset asset)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                asset.Write(out var uasset, out var uexp);
                return (uasset.ToArray(), uexp?.ToArray() ?? []);
            }
            catch (NameMapOutOfRangeException e) when (attempt < 1000)
            {
                asset.AddNameReference(e.RequiredName);
            }
            catch (InvalidOperationException e) when (attempt < 1000 && QuotedName(e.Message) is { } name)
            {
                asset.AddNameReference(new FString(name));
            }
        }
    }

    private static string? QuotedName(string message)
    {
        const string marker = "Attempt to add name \"";
        var start = message.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return null;
        start += marker.Length;
        var end = message.IndexOf('"', start);
        return end > start ? message[start..end] : null;
    }
}

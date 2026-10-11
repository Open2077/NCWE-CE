// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Immutable;
using System.Numerics;

namespace NCWE.Core;

// Custom decals: pictures imported into the user's bank (PNG, JPG, TGA, WebP... with or without
// transparency), placed like the game's decals and exported as real game resources inside the
// project's archive: a material instance of the game's decal template and its texture. Each lives
// under the depot folder ncwe\decals\<id>\ so its paths never collide with the game's.
public static class CustomDecalPaths
{
    public const string Root = @"ncwe\decals";
    public static string Folder(string id) => $@"{Root}\{id}";
    // What a decal element names as its material.
    public static string Material(string id) => $@"{Root}\{id}\{id}.mi";
    public static string Texture(string id) => $@"{Root}\{id}\{id}_d.xbm";
    // The texture the editor draws a version of the picture from (never exported): a changed
    // picture is another texture, so nothing drawn keeps the old one.
    public static string PreviewTexture(string id, int version) => $@"{Root}\{id}\v{version}\d.xbm";

    public static bool IsCustom(string? path) => IdOf(path) is not null;

    // The bank id in a custom decal's material path, or null for any other path.
    public static string? IdOf(string? path)
    {
        if (path is null || !path.StartsWith(Root + @"\", StringComparison.OrdinalIgnoreCase) || !path.EndsWith(".mi", StringComparison.OrdinalIgnoreCase)) return null;
        var parts = path.Substring(Root.Length + 1).ToLowerInvariant().Split('\\');
        return parts.Length == 2 && CustomMeshPaths.IsValidId(parts[0]) && parts[1] == parts[0] + ".mi" ? parts[0] : null;
    }
}

// A bank item: everything but the picture beside it.
public sealed record CustomDecalInfo(string Id, string Name)
{
    // Larger pictures are scaled down on import; the game's own decals are 2048 at most.
    public const int MaxImageSide = 4096, MaxTextureSide = 2048;
    public const float MinSize = .02f, MaxSize = 200, DefaultDepth = .3f;
    public const float MinEmissiveEV = -4, MaxEmissiveEV = 12;

    public int Version { get; init; } = 1;
    // The picture in the bank folder (a PNG, rows top to bottom, straight alpha).
    public string File { get; init; } = "image.png";
    public string SourceFile { get; init; } = "";
    public DateTimeOffset Created { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset Modified { get; init; } = DateTimeOffset.UtcNow;
    public int Width { get; init; }     // pixels
    public int Height { get; init; }
    // Some of it is see-through (else it paints its whole rectangle).
    public bool HasAlpha { get; init; }
    // Its width in the world when placed, metres; the height follows the picture's proportions.
    public float Size { get; init; } = 1;
    // Lights itself (a neon tag, a screen): the game's emissive decal template, with this exposure.
    public bool Emissive { get; init; }
    public float EmissiveEV { get; init; } = 2;
    public string Credits { get; init; } = "";
    public ImmutableArray<string> Warnings { get; init; } = [];

    [System.Text.Json.Serialization.JsonIgnore] public string MaterialPath => CustomDecalPaths.Material(Id);
    [System.Text.Json.Serialization.JsonIgnore] public string TexturePath => CustomDecalPaths.Texture(Id);
    [System.Text.Json.Serialization.JsonIgnore] public ulong Hash => CustomMeshInfo.Fnv1a64(MaterialPath);
    [System.Text.Json.Serialization.JsonIgnore] public ulong TextureHash => CustomMeshInfo.Fnv1a64(TexturePath);
    [System.Text.Json.Serialization.JsonIgnore] public ulong PreviewTextureHash => CustomMeshInfo.Fnv1a64(CustomDecalPaths.PreviewTexture(Id, Version));
    // Height over width.
    [System.Text.Json.Serialization.JsonIgnore] public float Proportions => Width > 0 && Height > 0 ? Height / (float)Width : 1;
    // The box of a newly placed one (decal space: X its width, Y its height, Z the depth it paints through).
    [System.Text.Json.Serialization.JsonIgnore] public Vector3 PlacedScale => new(Size, Size * Proportions, DefaultDepth);
    [System.Text.Json.Serialization.JsonIgnore] public string SizeText => $"{Width} × {Height}";

    public CustomDecalInfo Validated()
    {
        if (!CustomMeshPaths.IsValidId(Id)) throw new InvalidDataException("A custom decal needs a valid id.");
        if (string.IsNullOrWhiteSpace(Name)) throw new InvalidDataException("A decal needs a name.");
        if (Width <= 0 || Height <= 0 || Width > MaxImageSide || Height > MaxImageSide) throw new InvalidDataException($"A decal's picture is 1 to {MaxImageSide} pixels a side.");
        if (!float.IsFinite(Size) || Size < MinSize || Size > MaxSize) throw new InvalidDataException($"A decal is {MinSize} to {MaxSize} m wide.");
        if (!float.IsFinite(EmissiveEV) || EmissiveEV < MinEmissiveEV || EmissiveEV > MaxEmissiveEV) throw new InvalidDataException($"A glowing decal's exposure is {MinEmissiveEV} to {MaxEmissiveEV} EV.");
        return this with { Name = Name.Trim(), Warnings = Warnings.IsDefault ? [] : Warnings };
    }
}

public static class DecalDistances
{
    // The distance beyond which the game stops drawing a new decal (its node's auto hide distance),
    // from its size: small marks go early, a mural is seen from across the street.
    public static float AutoHide(Vector3 scale)
    {
        float side = MathF.Max(MathF.Abs(scale.X), MathF.Abs(scale.Y));
        return float.IsFinite(side) ? Math.Clamp(side * 25, 25, 300) : 25;
    }
}

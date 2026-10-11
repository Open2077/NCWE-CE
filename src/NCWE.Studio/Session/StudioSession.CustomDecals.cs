// SPDX-License-Identifier: GPL-3.0-only
using System.Numerics;
using NCWE.Core;
using NCWE.Formats.CustomDecals;

namespace NCWE.Studio.Session;

// Custom decals: the user's bank (pictures imported from PNG, JPG, TGA, WebP...), drawn and placed
// like the game's decals through the game index, exported as game resources (a material instance
// and its texture) inside the project archive.
internal sealed partial class StudioSession
{
    public CustomDecalLibrary CustomDecals { get; } = new(Path.Combine(StudioSettings.StateDirectory, "CustomDecals"));

    // What a click places for a decal of the bank: its material, at its own size.
    public static ElementStamp DecalStamp(CustomDecalInfo info) =>
        new(info.Name, ElementProperties.Preset(ElementKind.Decal) with { Resource = info.MaterialPath }) { Scale = info.PlacedScale };

    // A decal changed in the bank (picture, glow) or left it: what is placed is drawn again from it.
    public void CustomDecalChanged(string id)
    {
        _renderer.ForgetDecalMaterial(CustomMeshInfo.Fnv1a64(CustomDecalPaths.Material(id)));
        ElementVersion++;
    }

    // Decal elements of the project that show a decal of the bank.
    public int CustomDecalUses(string id) =>
        Elements.Values.Count(e => e.Properties.Kind == ElementKind.Decal && string.Equals(CustomDecalPaths.IdOf(e.Properties.Resource), id, StringComparison.OrdinalIgnoreCase));

    // The bank's decal an element shows, when it shows one.
    public CustomDecalInfo? CustomDecalOf(ElementProperties? p) => p is { Kind: ElementKind.Decal } ? CustomDecals.ByMaterialPath(p.Resource) : null;

    // A placed decal whose picture left the bank: nothing to draw, and the export stops on it.
    public bool IsMissingDecal(ElementProperties p) => p.Kind == ElementKind.Decal && CustomDecalPaths.IdOf(p.Resource) is { } id && CustomDecals.Get(id) is null;

    // One decal of the bank on the surface under a pixel of the view (a picture dropped there).
    public string? PlaceDecalAt(CustomDecalInfo info, Vector2 pixel, Vector2 viewport)
    {
        var stamp = DecalStamp(info);
        return SurfaceUnder(pixel, viewport) is { } on ? AddElementAt(stamp, on.Point, on.Normal) : AddElementAt(stamp, PlacementPoint(pixel, viewport));
    }
}

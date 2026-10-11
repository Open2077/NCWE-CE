// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace NCWE.Core;

// Archive-entry SHA-1 is provenance/cache identity, not the SHA-256 extracted
// sector baseline used by saved edits. No parser objects escape the adapter.
public sealed record ResourceProvenance(ulong Hash, string Path, string? Archive, string? EntrySha1, long SourceBytes)
{
    [JsonIgnore] public bool Resolved => Archive is not null;
}
public sealed record StreamingBlockIndex(uint GridCell, string Group);
public sealed record StreamingVariant(ulong NodeRef, uint Id, uint ParentId, string Name, uint RangeIndex, bool EnabledByDefault);
public sealed record StreamingDescriptor(ulong BlockHash, int DescriptorIndex, ResourceProvenance Sector,
    Bounds3 GameBounds, byte Level, string Category, StreamingBlockIndex BlockIndex,
    ulong QuestPrefabNodeRef, uint NodeRangeCount, ImmutableArray<StreamingVariant> Variants)
{
    [JsonIgnore] public bool HasValidBounds => SpatialBounds.IsValid(GameBounds);
    [JsonIgnore] public bool IsQuestDependent => QuestPrefabNodeRef != 0;
}
public sealed record StreamingBlockSnapshot(ResourceIdentity Resource, ResourceProvenance Provenance,
    StreamingBlockIndex Index, ImmutableArray<StreamingDescriptor> Descriptors, ImmutableArray<string> Diagnostics);
public sealed record StreamingWorldSnapshot(ResourceIdentity Resource, ResourceProvenance Provenance,
    uint Version, Bounds3 GameBounds, ImmutableArray<ResourceProvenance> BlockReferences);
public sealed record StreamingCatalog(int SchemaVersion, string ArchiveProfile, StreamingWorldSnapshot World,
    ImmutableArray<StreamingBlockSnapshot> Blocks, ImmutableArray<string> Diagnostics)
{
    public const int CurrentSchema = 1;
    [JsonIgnore] public IEnumerable<StreamingDescriptor> Descriptors => Blocks.SelectMany(b => b.Descriptors);
}

public static class SpatialBounds
{
    public static bool IsValid(Bounds3 b) => float.IsFinite(b.Min.X) && float.IsFinite(b.Min.Y) && float.IsFinite(b.Min.Z)
        && float.IsFinite(b.Max.X) && float.IsFinite(b.Max.Y) && float.IsFinite(b.Max.Z)
        && b.Min.X <= b.Max.X && b.Min.Y <= b.Max.Y && b.Min.Z <= b.Max.Z;
}

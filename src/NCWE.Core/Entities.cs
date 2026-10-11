// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Immutable;

namespace NCWE.Core;

// One mesh of an entity appearance, relative to the entity placement (component transforms
// composed through their parent bindings). Moving: the part rides a moving platform (an
// elevator cabin), so it travels with it from floor to floor.
public readonly record struct EntityPart(MeshReference Mesh, PlacementTransform Local)
{
    public bool Moving { get; init; }
}

// An entity node's template and appearance; Door is set for door entities.
public sealed record EntityInfo(string Template, string Appearance)
{
    public string EntityClass { get; init; } = "";
    public DoorSettings? Door { get; init; }
    public bool IsDoor => Door is not null;
}

// Door behaviour, from the template's Door entity and DoorController persistent state,
// overridden by the node's instance data. Enum values use the game's names.
public sealed record DoorSettings
{
    public static readonly ImmutableArray<string> Types = ["INTERACTIVE", "AUTOMATIC", "PHYSICAL", "REMOTELY_CONTROLLED", "NONE"];
    public static readonly ImmutableArray<string> States = ["CLOSED", "OPENED", "LOCKED", "SEALED"];
    public static readonly ImmutableArray<string> Openings = ["SLIDING_HORIZONTALLY", "SLIDING_VERTICALLY", "HINGED", "GATE", "HINGED_SIDE_ONE", "HINGED_SIDE_TWO"];

    // Decorative doors (FakeDoor) never open; only their look can change.
    public bool Decorative { get; init; }
    public string DoorType { get; init; } = "INTERACTIVE";
    public string InitialState { get; init; } = "CLOSED";
    public string Opening { get; init; } = "SLIDING_HORIZONTALLY";
    public bool ClosesAutomatically { get; init; } = true;
    public float CloseDelay { get; init; } = 3;      // seconds before an automatic close
    public float OpeningSpeed { get; init; } = 1;
    public bool PlayerCanToggleLock { get; init; }

    public DoorSettings Validated()
    {
        if (!Types.Contains(DoorType)) throw new InvalidDataException($"Unknown door type {DoorType}.");
        if (!States.Contains(InitialState)) throw new InvalidDataException($"Unknown door state {InitialState}.");
        if (!Openings.Contains(Opening)) throw new InvalidDataException($"Unknown door opening {Opening}.");
        if (!float.IsFinite(CloseDelay) || CloseDelay < 0 || CloseDelay > 600) throw new InvalidDataException("Door close delay must be between 0 and 600 s.");
        if (!float.IsFinite(OpeningSpeed) || OpeningSpeed <= 0 || OpeningSpeed > 20) throw new InvalidDataException("Door opening speed must be between 0 and 20.");
        return this;
    }

    public static string Label(string value) => value switch
    {
        "SLIDING_HORIZONTALLY" => "Sliding (sideways)", "SLIDING_VERTICALLY" => "Sliding (up)", "HINGED" => "Hinged",
        "GATE" => "Gate", "HINGED_SIDE_ONE" => "Hinged, opens to side one", "HINGED_SIDE_TWO" => "Hinged, opens to side two",
        "INTERACTIVE" => "Interactive (press to open)", "AUTOMATIC" => "Automatic (opens when approached)", "PHYSICAL" => "Physical (pushed open)",
        "REMOTELY_CONTROLLED" => "Remote (terminal or quest)", "NONE" => "None (never opens)",
        "CLOSED" => "Closed", "OPENED" => "Open", "LOCKED" => "Locked", "SEALED" => "Sealed",
        _ => value
    };
}

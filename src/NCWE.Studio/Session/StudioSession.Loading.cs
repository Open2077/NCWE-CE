// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Immutable;
using System.Numerics;
using NCWE.Core;

namespace NCWE.Studio.Session;

public enum LoadingHold { Follow, Keep, Area }

// World ▸ Loading: the world loads around the camera as it moves. A hold stops that, to work on
// one place without anything arriving or leaving: what is loaded stays (Keep), or everything
// within a distance of the camera is loaded first and then stays (Area). The hold lasts until the
// camera is followed again; it is not kept from one run to the next (nothing is loaded at start).
internal sealed partial class StudioSession
{
    public LoadingHold Hold { get; private set; }
    public Vector3 HoldCentre { get; private set; }
    public float HoldRadius { get; private set; }
    public int HeldSectors => _held.Length;
    // Sectors within the distance that the streamer cannot hold with the others (its sector limit).
    public int HoldLeftOut { get; private set; }
    // The area is still arriving.
    public bool HoldLoading { get; private set; }
    // The camera left what is held: nothing is loaded where it looks from.
    public bool HoldOutside { get; private set; }
    public const float MinHoldRadius = 25, MaxHoldRadius = 2000;
    private ImmutableArray<StreamingDescriptor> _held = [], _lastDesired = [];
    private bool _holdChanged;
    private double _nextHoldCheck;

    // The world loads around the camera again.
    public void FollowCamera()
    {
        if (Hold == LoadingHold.Follow) return;
        Hold = LoadingHold.Follow; _held = []; HoldLeftOut = 0; HoldLoading = HoldOutside = false;
        _nextPlan = 0; _plannedAt = new(float.NaN);
        Status = Loc.T("Loading follows the camera again.");
    }

    // What is loaded (or on its way) stays; nothing else loads.
    public bool KeepLoaded()
    {
        if (_streamer is null) { Status = Loc.T("The world is not loaded yet."); return false; }
        // From a hold already in place, the same sectors stay.
        if (Hold == LoadingHold.Follow)
        {
            if (_lastDesired.IsDefaultOrEmpty) { Status = Loc.T("The world is not loaded yet."); return false; }
            _held = _lastDesired; HoldCentre = Coordinates.ViewToGame(Camera.Eye);
        }
        Hold = LoadingHold.Keep; HoldRadius = 0; HoldLeftOut = 0; HoldLoading = false; _holdChanged = true; _nextHoldCheck = 0;
        Status = Loc.F("Loading frozen: the {0:N0} sectors loaded here stay and nothing else loads (World ▸ Loading to resume).", _held.Length);
        return true;
    }

    // Everything within radius metres of the camera loads, then stays.
    public bool HoldArea(float radius)
    {
        if (_streamer is null || Spatial is null) { Status = Loc.T("The world is not loaded yet."); return false; }
        radius = float.IsFinite(radius) ? Math.Clamp(radius, MinHoldRadius, MaxHoldRadius) : 300;
        var centre = Coordinates.ViewToGame(Camera.Eye);
        var all = NeighborhoodPolicy.ChooseArea(Spatial, centre, radius, 100_000);
        int limit = _streamer.Budgets.MaximumSectors;
        _held = all.Length > limit ? all[..limit] : all;
        HoldLeftOut = all.Length - _held.Length;
        Hold = LoadingHold.Area; HoldCentre = centre; HoldRadius = radius; HoldLoading = true; _holdChanged = true; _nextHoldCheck = 0;
        Settings.HoldRadius = radius; Settings.Save();
        Status = Loc.F("Loading everything within {0:0} m of the camera ({1:N0} sectors); nothing else will load after that…", radius, _held.Length);
        return true;
    }

    // Twice a second while a hold is on: has the area arrived, and is the camera still in it?
    private void UpdateHold(double now)
    {
        if (Hold == LoadingHold.Follow || _streamer is null || now < _nextHoldCheck) return;
        _nextHoldCheck = now + .5;
        var position = Coordinates.ViewToGame(Camera.Eye);
        // The detail around the camera is in the smallest sectors (level 0): none within reach, and
        // what is seen here is at best the coarse shapes of larger ones.
        bool inside = _held.Length == 0, detail = false;
        foreach (var d in _held)
        {
            if (d.Level != 0) continue;
            detail = true;
            if (SectorSpatialIndex.DistanceSquared(position, d.GameBounds) <= 40 * 40) { inside = true; break; }
        }
        HoldOutside = detail && !inside;
        if (!HoldLoading || _holdChanged) return;
        var m = _streamer.Metrics;
        if (m.PendingSectors > 0 || m.PendingMeshes > 0 || m.DeferredMeshes > 0 || m.ActiveWorkers > 0 || _renderer.PendingWorldUploads > 0) return;
        HoldLoading = false;
        int tight = _streamer.Errors.Values.Count(e => e.Contains("budget", StringComparison.OrdinalIgnoreCase));
        Status = Loc.F("{0:0} m around this place are loaded ({1:N0} sectors): loading is frozen (World ▸ Loading to resume).", HoldRadius, m.ResidentSectors)
            + (HoldLeftOut > 0 || tight > 0 ? " " + Loc.T("Not everything fits in memory at this distance: the furthest parts were left out.") : "");
    }
}

// SPDX-License-Identifier: GPL-3.0-only
using NCWE.Core;
using NCWE.Formats;
using NCWE.Formats.CustomMeshes;
using NCWE.Render;

namespace NCWE.Studio.Session;

// The material tool (NCWE.Core.ProjectMaterials): the user's bank, the project's materials, which
// mesh slots wear them, and how they look (preview spheres; the 3D view draws a mesh wearing
// project materials "dressed": its own drawable key, its surface with those slots replaced).
internal sealed partial class StudioSession
{
    private MaterialStore? _materialBank;
    private ProjectMaterialSource? _projectMaterials;
    // The user's material bank and the textures of every bank and project material.
    public MaterialStore MaterialBank
    {
        get
        {
            if (_materialBank is not null) return _materialBank;
            _materialBank = new MaterialStore(Path.Combine(StudioSettings.StateDirectory, "Materials"));
            _materialBank.Changed += () => MaterialVersion++;
            return _materialBank;
        }
    }
    // Goes up when the bank or the project's materials change (the panels follow it).
    public long MaterialVersion { get; private set; }

    // The renderer's source with the project's materials in front (set when the game opens).
    private IMaterialSource DressedMaterials(IMaterialSource inner)
    {
        Index!.MaterialStore = MaterialBank;
        _dressedAssets.Clear(); _dressedKeys.Clear(); _dressedAdded = null;
        // An edited mesh's drawable wears its source's slots (StudioSession.MeshEdit).
        return _projectMaterials = new ProjectMaterialSource(inner, Index, key => _editedMaterials?.PartSlots(key, Index.PartSlots));
    }
    // What the material spheres are drawn from (the archives, the store's textures).
    public IMaterialSource? PreviewSource => _projectMaterials;

    // --- Definitions as drawn -------------------------------------------------------------------

    // A definition's surface (spheres, the 3D view); its textures from the store, or from a custom
    // mesh's folder. Its key follows its content, so an edit is drawn anew.
    public SurfaceMaterial? DefinitionSurface(CustomMaterial material, string? textureFolder = null)
    {
        if (Index is not { } index || _projectMaterials is not { } source) return null;
        var own = MaterialInstances.OwnTextures(material).GroupBy(t => t.Channel).ToDictionary(g => g.Key, g => g.First());
        return index.MaterialSurface(material, "def:" + MaterialDefinitions.Fingerprint(material) + (textureFolder is null ? "" : ":" + textureFolder.GetHashCode().ToString("x8")), (channel, file) =>
        {
            string path = textureFolder is not null ? Path.Combine(textureFolder, file) : MaterialBank.TexturePath(file) ?? Path.Combine(MaterialBank.TexturesFolder, file);
            var texture = own.GetValueOrDefault(channel);
            return source.Texture(path, TextureCode(material, channel), texture?.Prepare ?? (x => x), MaterialInstances.IsColorTexture(channel));
        });
    }

    // How a channel's texture is prepared, in its preview path (the same file prepared otherwise is another texture).
    private static string TextureCode(CustomMaterial m, string channel) => channel switch
    {
        "n" => "n" + (m.FlipNormalGreen ? 1 : 0),
        "r" => $"r{m.RoughnessChannel}{(m.InvertRoughness ? 1 : 0)}",
        "m" => $"m{m.MetalnessChannel}",
        _ => channel
    };

    // --- Bank -------------------------------------------------------------------------------------

    public IReadOnlyList<MaterialAsset> BankMaterials() => MaterialBank.List();

    public MaterialAsset? SaveBankMaterial(MaterialAsset asset)
    {
        try { var saved = MaterialBank.Save(asset); Error = ""; return saved; }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) { Error = ex.Message; return null; }
    }

    public void DeleteBankMaterial(string id)
    {
        try { MaterialBank.Delete(id); Error = ""; }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) { Error = ex.Message; }
    }

    // A texture file (from disk) into the store: its store name.
    public string? AddMaterialTexture(string file)
    {
        try { return MaterialBank.AddTexture(file); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) { Error = ex.Message; return null; }
    }

    // A mesh slot's material (of an object, or of a mesh resource) into the bank.
    public MaterialAsset? ImportSlotToBank(ulong mesh, string slot, bool editable, string? name = null, string category = "")
    {
        if (Index is not { } index) { Error = Loc.T("Open the game first."); return null; }
        try
        {
            var imported = index.ImportSlotMaterial(mesh, slot, editable);
            var asset = SaveBankMaterial(new MaterialAsset(MaterialAsset.NewId(name ?? imported.Material.Name), name ?? imported.Material.Name, imported.Material)
            { Source = imported.Source, Category = category });
            if (asset is not null)
                Status = imported.Skipped > 0 ? Loc.F("Material “{0}” imported into the bank ({1} value(s) of an unsupported type left to its base).", asset.Name, imported.Skipped)
                    : Loc.F("Material “{0}” imported into the bank.", asset.Name);
            return asset;
        }
        catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException or IOException or NotSupportedException) { Error = ex.Message; return null; }
    }

    // A game material file (.mi, .mt, .remt) into the bank.
    public MaterialAsset? ImportMaterialFileToBank(string path, bool editable, string category = "")
    {
        if (Index is not { } index) { Error = Loc.T("Open the game first."); return null; }
        try
        {
            var imported = index.ImportMaterialFile(path, editable);
            var asset = SaveBankMaterial(new MaterialAsset(MaterialAsset.NewId(imported.Material.Name), imported.Material.Name, imported.Material) { Source = imported.Source, Category = category });
            if (asset is not null) Status = Loc.F("Material “{0}” imported into the bank.", asset.Name);
            return asset;
        }
        catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException or IOException or NotSupportedException) { Error = ex.Message; return null; }
    }

    // A project material saved into the bank (a new bank material, or the one it came from).
    public MaterialAsset? SaveProjectMaterialToBank(string materialId, bool asNew = false)
    {
        if (!Document.Materials.TryGetValue(materialId, out var material)) return null;
        var existing = !asNew && material.BankId is { } bank ? MaterialBank.Get(bank) : null;
        var asset = SaveBankMaterial(existing is not null ? existing with { Name = material.Name, Material = material.Material }
            : new MaterialAsset(MaterialAsset.NewId(material.Name), material.Name, material.Material) { Source = Loc.F("project “{0}”", Document.Project.Name) });
        if (asset is not null && material.BankId != asset.Id) Apply(() => Document.UpdateMaterial(material with { BankId = asset.Id }));
        if (asset is not null) Status = Loc.F("Material “{0}” saved in the bank.", asset.Name);
        return asset;
    }

    // --- Slots and the project's materials ------------------------------------------------------------

    // The material slots of an object's mesh, with the project material each wears (null: its own).
    public IReadOnlyList<(GameArchiveIndex.SlotInfo Slot, ProjectMaterial? Worn)> SlotsOf(string id)
    {
        if (Index is not { } index || Document.TryGetObject(id) is not { Mesh: { } mesh } item) return [];
        IReadOnlyList<GameArchiveIndex.SlotInfo> slots;
        try { slots = index.MeshSlots(mesh.Hash, mesh.Appearance); }
        catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException or IOException or NotSupportedException) { return []; }
        var materials = Document.Materials;
        return [.. slots.Select(s => (s, item.Materials.FirstOrDefault(m => m.Slot == s.Name) is { } worn ? materials.GetValueOrDefault(worn.Material) : null))];
    }

    public bool CanWearMaterials(string id, out string reason) => Document.CanWearMaterials(id, out reason);

    // A bank material on a slot of the objects (null: every slot of each): it joins the project
    // (once: a project copy of the same bank material is reused while it is unchanged).
    public IReadOnlyList<string> ApplyBankMaterial(IEnumerable<string> ids, string? slot, string bankId)
    {
        var asset = MaterialBank.Get(bankId);
        if (asset is null) { Error = Loc.T("This material is no longer in the bank."); return []; }
        var reuse = Document.Materials.Values.FirstOrDefault(m => m.BankId == bankId && m.Material == asset.Material);
        var material = reuse ?? new ProjectMaterial(ProjectMaterial.NewId(), asset.Name, asset.Material) { BankId = bankId };
        return ApplyMaterial(ids, slot, material.Id, reuse is null ? [material] : []);
    }

    // A project material (null: the mesh's own again) on a slot of the objects that have it (null:
    // every slot of each); a game mesh becomes a project copy wearing it, selected in its place.
    // One undo step per set of slots.
    public IReadOnlyList<string> ApplyMaterial(IEnumerable<string> ids, string? slot, string? materialId, IReadOnlyList<ProjectMaterial>? add = null)
    {
        var list = ids.Distinct(StringComparer.Ordinal).ToArray();
        var groups = new Dictionary<string, (List<string> Ids, string[] Slots)>(StringComparer.Ordinal);
        int skipped = 0;
        foreach (var id in list)
        {
            if (!Document.CanWearMaterials(id, out var reason)) { skipped++; if (list.Length == 1) Error = reason; continue; }
            var own = SlotsOf(id).Select(x => x.Slot.Name).ToArray();
            string[] slots = slot is null ? own : own.Contains(slot, StringComparer.Ordinal) ? [slot] : [];
            if (slots.Length == 0) { skipped++; continue; }
            string key = string.Join("|", slots);
            if (!groups.TryGetValue(key, out var group)) groups[key] = group = ([], slots);
            group.Ids.Add(id);
        }
        var result = new List<string>(); bool first = true;
        foreach (var (members, slots) in groups.Values)
        {
            IReadOnlyList<string> done = [];
            if (!Apply(() => done = Document.AssignMaterial(members, slots, materialId, first ? add : null))) break;
            first = false; result.AddRange(done);
        }
        if (result.Count == 0) { if (Error.Length == 0) Error = Loc.T("None of the selected objects has this slot."); return []; }
        var kept = list.Where(id => result.Contains(id)).ToArray();
        if (!result.SequenceEqual(kept) || result.Count != list.Length) SelectMany(result);
        bool copied = result.Except(list).Any();
        string? name = materialId is null ? null : Document.Materials[materialId].Name;
        // Whole sentences per case (no composed fragments: every language words them its own way).
        Status = (name, slot, copied) switch
        {
            (null, null, _) => Loc.T("Every slot wears its mesh's material again."),
            (null, _, _) => Loc.F("Slot {0} wears its mesh's material again.", slot),
            (_, null, true) => Loc.F("Material “{0}” on every slot of {1} object(s) (game meshes are now project copies of their nodes; Ctrl+Z restores them).", name, result.Count),
            (_, null, false) => Loc.F("Material “{0}” on every slot of {1} object(s).", name, result.Count),
            (_, _, true) => Loc.F("Material “{0}” on slot {1} of {2} object(s) (game meshes are now project copies of their nodes; Ctrl+Z restores them).", name, slot, result.Count),
            _ => Loc.F("Material “{0}” on slot {1} of {2} object(s).", name, slot, result.Count)
        } + (skipped > 0 ? Loc.F(" {0} object(s) skipped (no such slot, or not a mesh that can be copied).", skipped) : "");
        MaterialVersion++;
        return result;
    }
    // A slot's own material made a project material (to edit it for this object): imported from
    // the mesh, put on the slot, one undo step. Returns the material.
    public ProjectMaterial? MakeSlotMaterial(string id, string slot)
    {
        if (Index is not { } index || Document.TryGetObject(id) is not { Mesh: { } mesh }) return null;
        try
        {
            var imported = index.ImportSlotMaterial(mesh.Hash, slot, editable: true);
            var material = new ProjectMaterial(ProjectMaterial.NewId(), imported.Material.Name, imported.Material);
            return ApplyMaterial([id], slot, material.Id, [material]).Count > 0 ? material : null;
        }
        catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException or IOException or NotSupportedException) { Error = ex.Message; return null; }
    }

    public void UpdateProjectMaterial(ProjectMaterial material)
    {
        if (Apply(() => Document.UpdateMaterial(material))) { MaterialVersion++; Status = Loc.F("Material “{0}” updated on every mesh wearing it.", material.Name); }
    }

    public void AddProjectMaterial(ProjectMaterial material)
    {
        if (Apply(() => Document.SetMaterials([material], "Add material"))) MaterialVersion++;
    }

    public void RemoveProjectMaterial(string id)
    {
        if (Apply(() => Document.RemoveMaterial(id))) { MaterialVersion++; Status = Loc.T("Material deleted; the slots wearing it wear their mesh's material again."); }
    }

    // --- The 3D view: dressed meshes ------------------------------------------------------------------

    // --- Trying a material on -----------------------------------------------------------------------

    // A material shown on some objects' slots without changing the project (Slots null: every
    // slot): the picker's hovered material, a material dragged over a mesh, a slot's highlight.
    // Surface null: nothing changes but the selection tint, left out of these objects while the
    // user looks at their materials. Signature: what the surface is (its drawable follows it).
    public sealed record MaterialTrial(IReadOnlyList<string> Ids, IReadOnlyCollection<string>? Slots, SurfaceMaterial? Surface, string Signature);
    private MaterialTrial? _trial;
    private object? _trialOwner;
    public MaterialTrial? Trial => _trial;

    // `owner`: who shows it (EndTrial by another owner leaves it).
    public void TryMaterial(MaterialTrial? trial, object? owner = null)
    {
        _trialOwner = trial is null ? null : owner;
        if (SameTrial(trial, _trial)) return;
        _trial = trial; _sceneDirty = true;
    }

    public void EndTrial(object owner) { if (ReferenceEquals(owner, _trialOwner)) TryMaterial(null); }
    public bool TrialIs(object owner) => _trial is not null && ReferenceEquals(owner, _trialOwner);

    private static bool SameTrial(MaterialTrial? a, MaterialTrial? b) => ReferenceEquals(a, b) || a is not null && b is not null
        && a.Signature == b.Signature && a.Ids.SequenceEqual(b.Ids, StringComparer.Ordinal)
        && (a.Slots is null ? b.Slots is null : b.Slots is not null && a.Slots.Order(StringComparer.Ordinal).SequenceEqual(b.Slots.Order(StringComparer.Ordinal), StringComparer.Ordinal));

    // A definition as a trial's surface and signature.
    public MaterialTrial? TrialOf(IReadOnlyList<string> ids, IReadOnlyCollection<string>? slots, CustomMaterial material) =>
        DefinitionSurface(material) is { } surface ? new(ids, slots, surface, "def:" + MaterialDefinitions.Fingerprint(material)) : null;

    // The highlight a slot takes while the pointer is on its row (Inspector ▸ Materials).
    private static readonly CustomMaterial Highlight = new("highlight") { BaseColor = new(1f, .52f, .06f, 1), Roughness = .45f };
    public MaterialTrial? HighlightTrial(IReadOnlyList<string> ids, string slot) => TrialOf(ids, [slot], Highlight);

    // Whether the selection tint is left out of an object (a trial shows its materials as they are).
    internal bool Untinted(string id) => _trial is { } trial && trial.Ids.Contains(id, StringComparer.Ordinal);

    // The object and material slot under a pixel of the 3D view (Slot null: a part no slot is known for).
    public (string Id, string? Slot)? SlotAt(System.Numerics.Vector2 pixel, System.Numerics.Vector2 viewport)
    {
        if (_renderer.RenderedWorldCamera is not { } frame || Index is not { } index) return null;
        var ray = ScenePicking.ViewRay(pixel, viewport, frame.View, frame.Projection);
        var locked = LockedIds;
        if (NearestDrawn(ray, frame, item => locked.Count == 0 || !locked.Contains(item.Id)) is not { } hit) return null;
        if (Document.TryGetObject(hit.Item.Id) is not { Mesh: { } mesh }) return null;
        if (!_assets.TryGetValue(hit.Item.MeshHash, out var asset) || ScenePicking.IntersectPart(ray, asset, hit.Item.RelativeModel, hit.Item.LodMask, PickFronts) is not { } part)
            return (hit.Item.Id, null);
        var map = index.PartSlots(mesh.Hash);
        var slots = map?.GetValueOrDefault(mesh.Appearance) ?? map?.GetValueOrDefault("default") ?? map?.Values.FirstOrDefault();
        return (hit.Item.Id, slots is not null && part.Part >= 0 && part.Part < slots.Length ? slots[part.Part] : null);
    }

    // A game material (.mi, .mt) worn as it is (a project material referring to it, reused).
    public IReadOnlyList<string> ApplyGameMaterial(IEnumerable<string> ids, string? slot, string path)
    {
        if (Index is not { } index) { Error = Loc.T("Open the game first."); return []; }
        GameArchiveIndex.ImportedMaterial imported;
        try { imported = index.ImportMaterialFile(path, editable: false); }
        catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException or IOException or NotSupportedException) { Error = ex.Message; return []; }
        var reuse = Document.Materials.Values.FirstOrDefault(m => m.Material.Kind == CustomMaterialKind.Game && string.Equals(m.Material.GameMaterial, imported.Material.GameMaterial, StringComparison.OrdinalIgnoreCase));
        var material = reuse ?? new ProjectMaterial(ProjectMaterial.NewId(), imported.Material.Name, imported.Material);
        return ApplyMaterial(ids, slot, material.Id, reuse is null ? [material] : []);
    }

    // Another slot's own material (of this mesh or another) on a slot: a project material made from it.
    public IReadOnlyList<string> ApplySlotMaterial(IEnumerable<string> ids, string? slot, ulong mesh, string sourceSlot)
    {
        if (Index is not { } index) { Error = Loc.T("Open the game first."); return []; }
        GameArchiveIndex.ImportedMaterial imported;
        try { imported = index.ImportSlotMaterial(mesh, sourceSlot, editable: false); }
        catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException or IOException or NotSupportedException) { Error = ex.Message; return []; }
        var reuse = Document.Materials.Values.FirstOrDefault(m => m.Material == imported.Material);
        var material = reuse ?? new ProjectMaterial(ProjectMaterial.NewId(), imported.Material.Name, imported.Material);
        return ApplyMaterial(ids, slot, material.Id, reuse is null ? [material] : []);
    }

    // --- The 3D view: dressed meshes ------------------------------------------------------------------

    private readonly Dictionary<string, ulong> _dressedKeys = new(StringComparer.Ordinal);
    private Dictionary<ulong, MeshAsset> _dressedAssets = [];
    private object? _dressedAdded, _dressedMaterials, _dressedTrial;
    private long _dressedMeshRevision = -1;
    private bool _dressPending;

    // Every frame: project meshes wearing project materials (and the objects of a trial) get their
    // dressed drawable (once their mesh is loaded); a change of assignment, of a material or of the
    // trial draws them anew.
    private void UpdateMaterialDressing()
    {
        var added = Document.Project.Changes.Added; var materials = Document.Materials; var trial = _trial;
        if (ReferenceEquals(added, _dressedAdded) && ReferenceEquals(materials, _dressedMaterials) && ReferenceEquals(trial, _dressedTrial)
            && !(_dressPending && _dressedMeshRevision != _meshRevision)) return;
        bool trialChanged = !ReferenceEquals(trial, _dressedTrial);
        _dressedAdded = added; _dressedMaterials = materials; _dressedTrial = trial; _dressedMeshRevision = _meshRevision; _dressPending = false;
        // Per object: its mesh and, per slot, what it wears (a signature and how to draw it).
        var dressed = new Dictionary<string, (MeshReference Mesh, SortedDictionary<string, (string Signature, Func<SurfaceMaterial?> Surface)> Slots)>(StringComparer.Ordinal);
        foreach (var item in added.Values)
        {
            if (item.Materials.IsDefaultOrEmpty) continue;
            var slots = new SortedDictionary<string, (string, Func<SurfaceMaterial?>)>(StringComparer.Ordinal);
            foreach (var s in item.Materials)
                if (materials.TryGetValue(s.Material, out var m)) slots[s.Slot] = (MaterialDefinitions.Fingerprint(m.Material), () => DefinitionSurface(m.Material));
            if (slots.Count > 0) dressed[item.Id] = (item.Mesh, slots);
        }
        if (trial is { Surface: { } tried })
            foreach (var id in trial.Ids)
            {
                if (!Document.CanWearMaterials(id, out _) || Document.TryGetObject(id) is not { Mesh: { } mesh }) continue;
                (MeshReference Mesh, SortedDictionary<string, (string Signature, Func<SurfaceMaterial?> Surface)> Slots) entry =
                    dressed.TryGetValue(id, out var known) ? known : (mesh, new(StringComparer.Ordinal));
                var names = trial.Slots ?? (IReadOnlyCollection<string>?)Index?.PartSlots(mesh.Hash)?.Values.SelectMany(v => v).Distinct(StringComparer.Ordinal).ToArray() ?? [];
                foreach (var slot in names) entry.Slots[slot] = ("trial:" + trial.Signature, () => tried);
                dressed[id] = entry;
            }
        var keys = new Dictionary<string, ulong>(StringComparer.Ordinal);
        var assets = new Dictionary<ulong, MeshAsset>();
        bool changed = trialChanged;
        foreach (var (id, (mesh, worn)) in dressed)
        {
            // An edited mesh is dressed over its edited drawable (its own geometry and UVs).
            ulong baseKey = _editedKeys.TryGetValue(id, out var editedKey) && _assets.ContainsKey(editedKey) ? editedKey : mesh.Hash;
            string signature = $"{mesh.Hash:x16}|" + (baseKey != mesh.Hash ? _assets[baseKey].SourceHash + "|" : "") + string.Join("|", worn.Select(s => s.Key + "=" + s.Value.Signature));
            ulong key = DressedKey(signature);
            if (!assets.ContainsKey(key))
            {
                if (_dressedAssets.TryGetValue(key, out var existing)) assets[key] = existing;
                else if (_assets.TryGetValue(baseKey, out var basis))
                {
                    var slots = worn.ToDictionary(s => s.Key, s => s.Value.Surface(), StringComparer.Ordinal);
                    _projectMaterials?.Dress(key, new(baseKey, slots, Index?.IsCustom(mesh.Hash) == true ? basis : null));
                    assets[key] = basis with { SourceHash = basis.SourceHash + "+dressed:" + signature };
                    changed = true;
                }
                else { _dressPending = true; continue; }
            }
            keys[id] = key;
        }
        foreach (var key in _dressedAssets.Keys.Where(k => !assets.ContainsKey(k)).ToArray()) { _projectMaterials?.Undress(key); _assets.Remove(key); changed = true; }
        foreach (var (key, asset) in assets) if (!_assets.TryGetValue(key, out var current) || !ReferenceEquals(current, asset)) { _assets[key] = asset; changed = true; }
        if (!keys.OrderBy(k => k.Key, StringComparer.Ordinal).SequenceEqual(_dressedKeys.OrderBy(k => k.Key, StringComparer.Ordinal))) changed = true;
        _dressedAssets = assets;
        _dressedKeys.Clear(); foreach (var (id, key) in keys) _dressedKeys[id] = key;
        if (changed) { SetWorldAssets(); _sceneDirty = true; }
    }

    // After the drawable table is rebuilt from the streamer, put the dressed meshes back.
    private bool AddDressedAssets()
    {
        bool changed = false;
        foreach (var (key, asset) in _dressedAssets) if (!_assets.ContainsKey(key)) { _assets[key] = asset; changed = true; }
        // A mesh that has just arrived may complete a dressing.
        if (_dressPending) _dressedAdded = null;
        return changed;
    }

    private static ulong DressedKey(string signature)
    {
        ulong hash = 14695981039346656037UL;
        foreach (char ch in signature) { hash ^= ch; hash *= 1099511628211UL; }
        return hash | (1UL << 63) | (1UL << 60);
    }

    // Automation: the dressed drawables (object → key) and the surfaces' sources.
    public IReadOnlyDictionary<string, ulong> DressedObjects => _dressedKeys;
}

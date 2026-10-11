# Third-party notices and license evidence

NCWE's authored source is licensed under **GPL-3.0-only** by project choice. Third-party components retain their own notices. NCWE is an independent community project, unaffiliated with CD PROJEKT RED. Cyberpunk 2077 and its assets remain the property of their respective owners. No game assets or proprietary decompressor are included in this source repository.

## Pinned primary components

| Component | Pinned artifact / source | Evidence |
| --- | --- | --- |
| .NET SDK | 10.0.302, no roll-forward | Installed separately; `global.json` |
| WolvenKit.Modkit, Common, Core, RED4 | NuGet 9.0.1; source `01198a5fffd12b433a6e2713124b17340f3d7e4c` | Actual nuspec files declare MIT; repository LICENSE is GPL v3; see distinction below |
| Vortice.Direct3D11, DXGI, D3DCompiler | NuGet 3.8.3 | MIT in actual package metadata |
| ImGui.NET | NuGet 1.91.6.1; source `8e26803be78b344fd68834817905405b3cdffb94` | Nuspec has no license declaration; [pinned source LICENSE](https://github.com/ImGuiNET/ImGui.NET/blob/8e26803be78b344fd68834817905405b3cdffb94/LICENSE), [local notice](docs/licenses/ImGui.NET.txt) is MIT |
| Dear ImGui docking | `947aa9c9722bd6ff740dd757da609ff41f4d3ba3` | [Bundled source notice](docs/licenses/Dear-ImGui.txt), MIT |
| cimgui | `970c614802935f51f451aa21ae06e838bdcf9349` | [Bundled source notice](docs/licenses/cimgui.txt), MIT |
| ImGuizmo | `b10e91756d32395f5c1fefd417899b657ed7cb88` | [Bundled source notice](docs/licenses/ImGuizmo.txt), MIT |
| AssimpNet (custom mesh import) | NuGet 5.0.0-beta1; bundles the native Open Asset Import Library 5.0 (`runtimes/win-x64/native/assimp.dll`) | [Package license file](docs/licenses/AssimpNet-5.0.0-beta1.txt), MIT; Assimp itself is BSD-3-Clause (https://github.com/assimp/assimp/blob/master/LICENSE) |
| SixLabors.ImageSharp (custom mesh textures) | NuGet 3.1.12 | [Package license file](docs/licenses/SixLabors.ImageSharp-3.1.12.txt): Six Labors Split License 1.0, Apache 2.0 terms for open source software such as this GPL project |
| BCnEncoder.Net (custom mesh textures) | NuGet 2.2.1 | MIT OR Unlicense in package metadata |
| NAudio.Core, NAudio.Wasapi (custom sounds: reading, re-encoding through Windows Media Foundation, Studio playback) | NuGet 2.2.1 | MIT in package metadata |
| NAudio.Vorbis, NVorbis (custom sounds: Ogg Vorbis decoding) | NuGet 1.5.0 / 0.10.4 | MIT in package metadata; NVorbis [package license file](docs/licenses/NVorbis-0.10.4.txt), MIT |

Native source download SHA-256 checksums are in `native/CMakeLists.txt`. NuGet package content hashes are locked in each `packages.lock.json`. [Machine-readable inventory](docs/dependencies.json) records every resolved package/version, SHA-512, authors/copyright where declared, repository commit, and license metadata.

## WolvenKit metadata distinction

All four pinned 9.0.1 NuGet artifacts declare `<license type="expression">MIT</license>`. The corresponding [repository LICENSE](https://github.com/WolvenKit/WolvenKit/blob/01198a5fffd12b433a6e2713124b17340f3d7e4c/LICENSE) contains GPL v3, copied verbatim into [repository-license evidence](docs/licenses/WolvenKit-repository.txt). These are separate observations, not a claim that every WolvenKit artifact necessarily imposes GPL. NCWE references the NuGet binaries and does not copy a second parser into its source. Its own GPL-3.0-only choice is explicit.

## Transitive payloads

- Most resolved managed libraries declare MIT. Copyright attribution and repository evidence remain in the inventory; package-supplied license files are preserved under `docs/licenses/`.
- Serilog and SQLitePCLRaw packages declare Apache-2.0. The [license text](docs/licenses/Apache-2.0.txt) is included. The SQLite native package is the prerelease version required by the pinned WolvenKit dependency graph; it is locked, not silently upgraded.
- ClearScript packages include a combined license file covering their MIT wrapper and bundled V8/ICU/other components. Those actual package files are retained separately, including their third-party notices. NCWE does not exercise the scripting runtime in these milestones.
- SharpDX packages use a legacy license URL. Their recorded source commit is `8e5df9f17b1d328c595a9df5851dbb2537b55621`; the [source license](https://github.com/sharpdx/SharpDX/blob/8e5df9f17b1d328c595a9df5851dbb2537b55621/License.txt) is MIT and [included](docs/licenses/SharpDX.txt). NCWE renders through Vortice; SharpDX is transitive through WolvenKit.
- K4os.Compression.LZ4 1.3.8 has legacy metadata. Its [1.3.8 source notice](https://github.com/MiloszKrajewski/K4os.Compression.LZ4/blob/1.3.8/LICENSE) is [included](docs/licenses/K4os.Compression.LZ4.txt).
- NAudio.Lame's wrapper notice is MIT. Its package description also declares bundled libmp3lame binaries; LAME's separate [LGPL v2 license](docs/licenses/LGPL-2.0.txt) is not replaced by that wrapper notice. NCWE does not use audio. Any future binary packaging must account for the actual embedded native payload and corresponding-source requirements rather than relying only on the wrapper's nuspec.
- Legacy .NET metapackages retain their original license URLs in the inventory. They are dependency-resolution artifacts; the app runs on the separately installed .NET 10 framework.

WolvenKit.Modkit's content files include Opus executables and a sound-event list unrelated to mesh viewing. The direct reference excludes `contentFiles` so clean builds do not copy them. Existing local output directories from older builds may still contain stale files; a release must be assembled from a fresh build and audited. This milestone supplies source/build tooling, not a published binary distribution.

## System and installation components

Windows, Direct3D, D3DCompiler, Visual C++ runtime components, and the installed Segoe UI font are supplied by the user's system. NCWE loads the font from Windows and does not embed it. The Oodle library is proprietary and loaded by full path from the user's selected Cyberpunk installation, without copying or redistribution. Missing Oodle produces a visible diagnostic.

# Claude of Duty — Unity port

A Unity 6.3 / URP 17.3 port of [claude-of-duty-optimized](../claude-of-duty-optimized):
a browser FPS whose entire art set — meshes, textures, animation, audio — is
generated procedurally from code. There are no art files to copy, so this port
does not copy art. It **bakes** it: a headless pipeline runs the original's own
generators in Chromium and writes real assets this project imports.

## Status

| piece | state |
|---|---|
| Texture bake — 19 world surfaces + 15 weapon variants | **done**, 34 sets, 5 maps each |
| Weapon mesh bake — rifle, SMG, pistol, every attachment variant | **done** |
| Audio bake — weapons, foley, UI, ambience, barks, reverb IRs | **done**, 114 files |
| Triplanar URP shader (vertex masks, roughness remap, env specular, alpha clip) | **done** |
| Viewmodel light rig (key/fill/rim/bounce + hemisphere, view-space) | **done**, in the shader |
| Material + prefab build from the bakes | **done**, verified by in-editor render |
| Player movement tuning | data ported, controller not written |
| World geometry (market street, buildings, interiors, props) | not started |
| Skinned soldiers + animation clips | not started |
| Weapon definitions, ballistics, AI | not started |

## Opening it

Open the folder in Unity Hub with **6000.3.10f1**. The project builds its own
materials and prefabs on demand:

```
Claude of Duty ▸ Run All          # rebuild materials, then weapon prefabs
Claude of Duty ▸ Render Previews  # render the prefabs to tools/bake/out/unity
```

Both are also batch entry points:

```
Unity.exe -batchmode -projectPath . -executeMethod PortPipeline.RunAll -logFile -
Unity.exe -batchmode -projectPath . -executeMethod PortPreview.RenderPreviews -logFile -
```

## Regenerating the art

`Assets/Art/` is gitignored. To rebuild it you need the source repo checked out
as a sibling directory, plus Node and Playwright's Chromium:

```bash
cd ../claude-of-duty-optimized
npm install
npx playwright install chromium

node tools/bake/bake-textures.mjs   # 34 sets  -> ../claude-of-duty-unity/Assets/Art/Textures
node tools/bake/bake-meshes.mjs     # 3 weapons -> ../claude-of-duty-unity/Assets/Art/Models
node tools/bake/bake-audio.mjs      # 114 wavs  -> ../claude-of-duty-unity/Assets/Art/Audio
```

Both scripts take `--out=<dir>` to write somewhere else, `--only=a,b` to bake a
subset, and `--size=` for the texture bake (1024 default; the surfaces were
authored and tuned at 1K, so larger mostly buys file size).

## How the port works

**The geometry has no UVs.** The original samples every surface triplanar in
object space, so a weapon's anodising grain is 9.5 cm of object scale on a 2 cm
rail without anyone unwrapping anything. `TriplanarLit.shader` does the same
projection, which is why the baked tiles can be dropped straight onto the models
and why there is no unwrapper or atlas in this repo.

**CODM, not glTF.** `Assets/Art/Models/*/*.codm.bytes` plus a `.codm.json`
manifest is what the importer reads. Unity has no native glTF importer, the
package CDN is unreachable from the machine this was built on, and glTF would
have dropped the per-vertex wear masks anyway. The format is a flat blob and a
table of byte ranges — `CodmModelBuilder.cs` reads it in one pass. A `.glb` is
written alongside as an archive copy that opens in Blender.

**Vertex masks.** The viewmodel bakes a wear/grime/AO mask into vertex colours;
the baker carries it through and the shader paints bare metal onto convex
corners and grime into the height field's valleys with it. Without it every
corner stays painted and the gun reads as a resin cast.

**Every material number comes from the source.** `_Tiling` is metres-per-tile
inverted the way `MaterialSystem.tune` inverts it, `_Tint` is linearised the way
`THREE.Color` linearises it, and roughness is remapped as
`clamp(rough * scale + bias, min, 1)` with the per-material values from
`weapons/materials.js`. The sidecars are the source of truth; nothing is retyped.

## Known debt

**The weapon renders dark in the preview, and it is environment radiance, not
albedo.** The viewmodel rig is ported — key 2.0 warm upper-front-left, cool fill
0.6, rim 1.0, warm bounce 0.5 from below, and a 0.35 hemisphere, all fixed in
view space exactly as `render/index.js` sets them — and it visibly catches the
rail, magazine and receiver edges. What the preview does not have is the
browser's environment: upstream reflects a PMREM of a real atmosphere, and the
README there records that its viewmodel rig delivers roughly 20× the irradiance
per unit albedo that the world does, with every weapon albedo authored a third
of physical to compensate. A receiver here is ≈0.003 linear after tint, so the
broad specular term is doing most of the work and a stand-in skybox at exposure
5.5 only approximates it. When the sky and time-of-day are ported this should be
re-measured rather than tuned around.

**Audio is baked dry.** One-shots carry no reverb send, because Unity
spatialises and reverbs them itself. The five impulses (`ir_tight` … `ir_open`)
are there for Unity's reverb to convolve with, but nothing wires them up yet.

## Attribution

The original game is ISC licensed; see `LICENSE` and `THIRD-PARTY-NOTICES.txt`,
copied from the source repo. All geometry, texture and material design in
`Assets/` is generated from that codebase, not authored here.

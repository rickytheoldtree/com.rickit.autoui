# RicKit Auto UI

Unity editor tools for Auto UI: turn PSD exports into project-conforming UI assets.

Auto UI itself is a desktop tool (`AutoUI.exe`) that cuts PSD/PSB files into sprites plus an engine-neutral `layout.json`, and writes an **import plan** that follows a per-project profile (`autoui.profile.json`). This package applies those plans inside Unity and gives AI agents data about existing UI. It is **editor only**: generated prefabs use plain uGUI components and do not depend on this package at runtime.

## Install

Add the OpenUPM registry scope and the dependency to `Packages/manifest.json`:

```json
{
  "scopedRegistries": [
    { "name": "package.openupm.com", "url": "https://package.openupm.com", "scopes": ["com.rickit.autoui"] }
  ],
  "dependencies": {
    "com.rickit.autoui": "0.6.0"
  }
}
```

Use the same version as the `AutoUI.exe` that writes the plans.

## Tools

| Entry | Use |
| --- | --- |
| `AutoUI.Editor.AutoUiPlanImporter.ApplyFromCommandLine -autoUiPlan <plan> [-autoUiAcceptReview]` | Apply an `import-plan.json`: re-check every input hash, write/reuse/rename/delete sprites, build or update the visual prefab, update the page manifest, run the profile's post-import tools and validators, write `<plan>.result.json` |
| Menu **Tools → Auto UI → 执行导入计划…** | Same, from an open editor |
| `AutoUI.Editor.AutoUiPrefabDump.DumpFromCommandLine -autoUiPrefab <a;b> -autoUiOut <json or dir> [-autoUiCanvas WxH]` | Export an existing prefab as data: hierarchy, canvas-space rects, anchors and sizes, layout settings, images, texts, nested prefabs, script references |
| `AutoUI.Editor.AutoUiPreview.RenderFromCommandLine -autoUiPrefab <prefab> -autoUiOut <png> [-autoUiCanvas WxH]` | Render a UI prefab on an opaque backdrop for comparison with the design |

Command-line entries run with `-batchmode` and need the project's editor to be closed.

## How re-import stays stable

Only files listed in the page manifest (`Assets/.../Manifests/<page>.json`, location set by the profile) are ever changed. The manifest also maps each layout node ID to its object path in the visual prefab, so a re-import finds the same objects: file IDs and references from other prefabs survive, and renamed nodes are renamed in place.

See `Documentation~/AI.md` for the rules AI agents follow.

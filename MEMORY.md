# Project Memory

This repository contains a Figma plugin and a Unity importer for turning selected Figma frames into Unity UI scenes.

## Folder Layout

- `Figma Plugin/` contains the Figma plugin source, UI, manifest, bundled plugin code, and package scripts.
- `Figma2Unity_UnityProject/` contains the Unity project, editor importer, runtime helper scripts, sample scene, fonts, and generated sample sprites.

## Current Workflow

1. In Figma, open the plugin from `Figma Plugin/manifest.json`.
2. Select one or more frames.
3. Click `Export Unity Package`.
4. Save the generated JSON. The default filename is based on the selected frame names: `figma-{frame-prefix}-{timestamp}.json`.
5. In Unity, open `Figma2Unity_UnityProject`.
6. Use `Tools > Figma > Select Figma JSON And Build`.
7. Choose create-new or update-existing based on whether you want a fresh generated hierarchy or to preserve user-edited objects and wiring.

## Important Behavior

- Multiple selected Figma frames become separate Unity screen panels in one scene, not separate scenes.
- Unity screen panels are normalized to their frame size rather than preserving Figma canvas offsets.
- Text is generated as TextMeshPro where possible.
- Buttons, inputs, sliders, and dropdowns are detected from lenient Figma naming and component structure.
- Missing objects during update are deactivated instead of removed.
- Existing Unity components and user wiring should be preserved on update.
- Common sprites are reused through a common asset path when duplicated across frames.
- Per-frame assets are grouped into frame-specific folders when they are not shared.
- Background rasters should stay plain; foreground objects are generated separately.
- Logo-like grouped text/vector content may be rasterized as a logo image.
- Person or photo images can be placed into masked UI shapes.
- Font matching prefers imported Unity/TMP fonts and avoids synthetic bold when a bold font asset exists.

## Unity Notes

- Unity version: `6000.0.73f1`.
- Main editor importer: `Figma2Unity_UnityProject/Assets/Editor/FigmaJsonToUnity.cs`.
- Runtime binding: `Figma2Unity_UnityProject/Assets/Scripts/FigmaNodeBinding.cs`.
- Alpha hit testing: `Figma2Unity_UnityProject/Assets/Scripts/FigmaAlphaRaycastFilter.cs`.
- Responsive root scaling: `Figma2Unity_UnityProject/Assets/Scripts/FigmaResponsiveRoot.cs`.
- Test flow script: `Figma2Unity_UnityProject/Assets/Scenes/UserFlowController.cs`.
- `Use Webcam` in `UserFlowController` is guarded in the Unity Editor. Editor webcam startup is blocked unless `Allow Webcam In Editor` is explicitly enabled.

## Plugin Notes

- Plugin entry point: `Figma Plugin/src/code.ts`.
- Plugin UI: `Figma Plugin/ui.html`.
- Bundled output used by Figma: `Figma Plugin/build/code.js`.
- Analysis helper: `Figma Plugin/analyze_unity_package.py`.
- The plugin UI also shows an in-plugin analysis panel after export, mirroring the Python analysis checks.

## Git Notes

- Track source files, Unity `Assets`, `Packages`, and `ProjectSettings`.
- Keep `Figma Plugin/build/code.js` tracked so Figma can import the plugin directly.
- Ignore `node_modules`, plugin JSON exports, Unity `Library`, `Temp`, `Logs`, `UserSettings`, generated IDE projects, and build outputs.


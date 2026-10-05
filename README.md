# Figma UI to Unity Scene Generator

Export selected Figma frames into JSON and rebuild them as Unity UI screens. The project includes both sides of the pipeline:

- A Figma plugin that exports selected frames, metadata, text, UI controls, and PNG assets.
- A Unity editor importer that builds Canvas-based UI screens from the exported package.

## Repository Structure

```text
.
├── Figma Plugin/
│   ├── src/code.ts
│   ├── ui.html
│   ├── manifest.json
│   ├── build/code.js
│   └── analyze_unity_package.py
└── Figma2Unity_UnityProject/
    ├── Assets/
    │   ├── Editor/FigmaJsonToUnity.cs
    │   ├── Scripts/
    │   ├── Scenes/
    │   └── FigmaSprites/
    ├── Packages/
    └── ProjectSettings/
```

## Features

- Export one or more selected Figma frames.
- Generate one Unity scene containing one screen panel per selected Figma frame.
- Normalize each frame to its own Unity screen instead of preserving Figma canvas offsets.
- Convert regular text into TextMeshPro text.
- Detect buttons, input fields, sliders, and dropdowns with lenient names such as `button`, `btn`, `input`, `inpt`, `dropdown`, `options`, and `slider`.
- Preserve existing Unity components and user wiring when updating generated UI.
- Deactivate imported objects that no longer exist in the new export instead of deleting them.
- Reuse duplicated sprites through common assets.
- Organize frame-specific assets separately.
- Support masked/person images, logos, gradients, normal colors, font matching, and responsive Canvas scaling.
- Show export analysis in the plugin UI after JSON generation.

## Requirements

### Figma Plugin

- Figma desktop or browser app with plugin development access.
- Bun or Node tooling for rebuilding the plugin bundle.

### Unity

- Unity `6000.0.73f1`.
- TextMeshPro.
- Unity UI / uGUI.
- Unity Input System.
- Newtonsoft JSON package.

## Setup

### 1. Install the Figma Plugin

1. Open Figma.
2. Go to `Plugins > Development > Import plugin from manifest`.
3. Select:

```text
Figma Plugin/manifest.json
```

The manifest points to `Figma Plugin/build/code.js`, which is tracked so the plugin can be imported without rebuilding first.

### 2. Rebuild the Figma Plugin

From the plugin folder:

```bash
cd "Figma Plugin"
bun install
bun run build
```

If you use npm instead of Bun:

```bash
npm install
npm run build
```

### 3. Open the Unity Project

Open this folder in Unity Hub:

```text
Figma2Unity_UnityProject
```

Use Unity version `6000.0.73f1` or a compatible Unity 6 version.

## Usage

### Export from Figma

1. Select one or more Figma frames.
2. Run the plugin.
3. Click `Export Unity Package`.
4. Save the generated JSON.

The default filename uses the selected frame names:

```text
figma-{frame-prefix}-{timestamp}.json
```

Examples:

```text
Digital India 01
Digital India Start Screen
Digital India End
```

exports as:

```text
figma-digital-india-{timestamp}.json
```

```text
Frame 1
Frame 5
Fail
```

exports as:

```text
figma-frame-{timestamp}.json
```

### Build in Unity

In Unity:

```text
Tools > Figma > Select Figma JSON And Build
```

Choose the exported JSON file.

Use create-new when generating the UI fresh. Use update-existing when you want to keep custom Unity hierarchy changes, attached components, references, and wiring.

## Figma Naming Tips

The importer is designed to be lenient, but clear names produce better Unity objects:

- Buttons: `button`, `btn`, `cta`, `continue`, `submit`, `next`, `home`.
- Inputs: `input`, `inpt`, `field`, `textfield`, `name`, `email`, `message`.
- Dropdowns: `dropdown`, `options`, `select`, `menu`.
- Sliders: `slider`, `range`, `track`, `thumb`.
- Logos: use `logo` or a grouped brand mark/name.

## Analysis Tools

The plugin UI shows an export analysis after JSON generation. The same package can also be checked from the command line:

```bash
cd "Figma Plugin"
python analyze_unity_package.py "exports/your-export.json"
```

The analysis reports screen count, asset count, duplicate PNGs, per-screen node kinds, suspicious large semantic nodes, export-limit warnings, and PNG size mismatches.

## Unity Notes

- Generated objects get stable Figma bindings through `FigmaNodeBinding`.
- Responsive scaling is handled by `FigmaResponsiveRoot`.
- Alpha-based click filtering is handled by `FigmaAlphaRaycastFilter`.
- `UserFlowController` includes a guarded webcam test flow. In the Unity Editor, camera startup is blocked unless `Allow Webcam In Editor` is enabled to avoid editor crashes from unstable webcam drivers.

## Git Hygiene

This repo tracks the Figma plugin source/build and the Unity project source assets. It ignores:

- `node_modules`
- Figma JSON exports
- Unity `Library`, `Temp`, `Logs`, and `UserSettings`
- Generated Unity solution/project files
- Local build outputs


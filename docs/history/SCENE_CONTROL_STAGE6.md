# LiveStudio Stage 6 — Shared Scene Controls

## Added
- New navigation entry: `کنترل تصویر و صدا` at `/scene-control`.
- Shared image processing controls persisted in browser localStorage under `livestudio.sceneEffects`.
- Brightness, contrast, saturation and exposure controls with Natural, Vivid and Cinematic presets.
- Chroma key controls: enable/disable, key color, tolerance and edge softness.
- Shared Canvas 2D processing helper used by both the LiveKit program-output compositor and Creator Studio recording compositor.
- Chroma key processing is downscaled to a maximum processing surface of 1280x720 before being drawn to the final output to reduce CPU load.

## How it applies
- Settings are loaded when either compositor starts.
- Live Studio applies effects to the camera layer in the composed program output before publishing the canvas video track to LiveKit.
- Creator Studio applies effects to the camera layer in the composed recording canvas before MediaRecorder captures it.
- Effects do not alter the raw camera device stream or local raw preview.

## Validation
- JavaScript syntax checked with `node --check` for `livestudio-effects.js`, `livestudio-livekit.js`, `livestudio-creator.js`, and `livestudio-media.js`.
- .NET build and browser/LiveKit runtime tests were not run because the .NET SDK and browser runtime are unavailable in the packaging environment.

## Notes
- Chroma key is a lightweight RGB-distance matte, not a studio-grade AI segmentation model. Lighting and background uniformity materially affect quality.
- Audio mixing, noise gate, compressor, EQ and audio meters are not part of this stage.

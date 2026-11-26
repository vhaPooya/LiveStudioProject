# Stage 9 — Integrated Studio Compositor

## What changed

- Added a reusable `StudioComposerPanel` used by both `/studio/{BroadcastId}` and `/creator`.
- The same 1920x1080, 30fps browser canvas is now the visible compositor preview and the source for the actual output pipeline.
- Live Studio has separate Preview and Program canvases. Edits are made on Preview; `TAKE` copies the complete scene state to Program. LiveKit publishes the Program canvas track, not the raw camera or an unrelated mock preview.
- Creator Studio records the same compositor canvas shown in its monitor and mixes the processed microphone track into the recording.
- Background selection, image/logo/video overlays, text, lower-third and breaking-news elements are rendered in the compositor.
- Elements can be selected from the layer list or by clicking them in the preview, dragged on the preview, hidden, locked, deleted and reordered.
- Each element has brightness, contrast, saturation, exposure and hue correction. Text layers additionally expose text/background color and font size.
- Preset animation choices: Fade In, Slide Left/Right, Zoom, Pulse, Floating, Bounce, Gentle Rotate and Glow. Pulse/Float/Bounce/Rotate/Glow loop continuously; entrance animations play when selected or when a layer is created.
- Camera correction is rendered before compositing: brightness, contrast, saturation, exposure and chroma key with color/tolerance/edge softness controls.
- Camera layout presets: Full, Picture-in-Picture, Left Half and Right Half. Applying a background automatically switches a full-frame camera to PIP; users can override the layout.
- Scene state is stored locally in browser localStorage separately for Live Studio and Creator Studio. Media files remain in the Media Box IndexedDB library.
- Standalone Scene Control, Audio Mixer and Teleprompter links were removed from the main navigation because their relevant controls are embedded in the studio workspaces. Their routes/components remain in the project.

## Important behavior

1. Open either studio and activate the camera/microphone using its existing control.
2. In the composer, choose a Media Box background or add an image/video/text/lower-third/breaking-news layer.
3. Drag the selected layer directly over the large Preview image. Use the Element tab for precise position, size, color correction and animation.
4. In Live Studio, inspect the smaller Program monitor and click `ارسال Preview به Program (TAKE)`. Only Program is published to LiveKit. Repeat TAKE whenever you want to put a revised scene on air.
5. In Creator Studio, the canvas visible in the main monitor is the exact video canvas captured by MediaRecorder.

## Verification and limitations

- `node --check` passed for all JavaScript files in `wwwroot`.
- A .NET build and real camera/LiveKit browser test could not be run in this build environment because the .NET SDK and a usable browser automation runtime were not available.
- This stage implements browser-side scene composition and LiveKit camera-track publishing. It does not implement LiveKit Egress/RTMP delivery to YouTube; the existing YouTube encoder workflow remains separate.
- Chroma key is CPU-based and can be demanding on lower-powered devices. The compositor processes the keyed camera at a reduced intermediate resolution before scaling it to 1080p.
- The browser stores scene configuration locally; it is not yet synchronized across accounts/devices or backed by a server-side purchased asset entitlement system.

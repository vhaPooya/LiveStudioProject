LiveStudio Stage 9 — Integrated Studio Compositor

Open the Live Studio or Creator Studio from the sidebar. The central compositor workspace is embedded directly in both studios.

Live Studio workflow:
- Connect LiveKit and enable the camera.
- Configure background, camera correction, overlays, text, logos and animations in Preview.
- Drag elements directly on the preview image and adjust exact properties in the Element tab.
- Click TAKE to transfer the preview scene to Program. The Program canvas is the video track published to LiveKit.

Creator Studio workflow:
- Activate the camera and microphone.
- Configure the exact recording canvas in the integrated compositor.
- Start recording; the compositor canvas and processed microphone are recorded together.

Scene settings are stored in browser localStorage per studio. Media assets are read from Media Box's local IndexedDB library.

All JavaScript files passed node --check. .NET build and real camera/LiveKit tests were not available in this environment; test locally before replacing the running project.

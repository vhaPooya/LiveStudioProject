# LiveStudio Stage 3 - Media Box

## What's included
- Added `Media Box` to the persistent navigation (`/media-box`) and shortcuts in Live Studio and Creator Studio.
- Added tabbed local asset library: transparent overlays, video backgrounds, image backgrounds, user logos, stingers/intros, and audio effects.
- Added local IndexedDB storage for uploaded media. Files remain in the current browser profile and are not uploaded to the application server.
- Added upload limits: logos are capped at 5; image assets 20 MB; video assets 100 MB; audio assets 30 MB.
- Added demo assets for every tab to support initial testing.
- Added logo placement (horizontal/vertical/size), loop animations, lower-third, breaking-news ticker, and live clock preview.
- Added shared scene design settings in browser localStorage and an overlay layer in both Live Studio and Creator Studio previews.

## Notes and limitations
- The demo store is local-only. Purchase entitlements, user accounts, cloud sync, and the future website store are not implemented yet.
- The current browser overlay is a preview layer. It is not yet composited into the LiveKit outgoing video track or the Creator Studio recording stream. The next implementation stage should connect the shared scene renderer to the actual outgoing/recorded media pipeline.
- Background removal, camera image controls (exposure/contrast/color), transition engine, video source routing, and audio processing are not yet implemented. They should be added against the shared media pipeline rather than as isolated controls.
- Build/runtime testing was not possible in this environment because the .NET SDK is not installed. JavaScript syntax was checked with Node.js.

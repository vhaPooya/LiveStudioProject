# LiveStudio Stage 4 — Creator Program Output

- Creator Studio recording now records a 1920x1080 canvas composition rather than the raw camera stream.
- Composition reads the shared Media Box design and selected assets from IndexedDB.
- Supports image/video background, camera picture-in-picture when a background is selected, logo placement and pulse/float/rotate/glow animation, lower-third title/subtitle, Breaking News ticker and live clock.
- Original microphone audio track is muxed into the canvas recording stream.
- The recorded WebM file is still generated locally in the browser.
- LiveKit's published camera track is unchanged in this stage; live program-output publishing is a separate next integration task.

## Important
Build and browser verification were not run in this environment. Test with Chrome/Edge, a selected Media Box background and logo, and a short recording before relying on this in production. Browser MediaRecorder support and available memory affect maximum recording duration.

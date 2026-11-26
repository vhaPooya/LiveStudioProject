# Stage 7 — Audio Mixer

## Added
- Added `/audio-mixer` page and persistent navigation entry.
- Added browser-based microphone input monitor with RMS level meter and no audible loopback.
- Added Web Audio processing chain: input gain, high-pass filter, 3-band EQ, and optional dynamics compressor.
- Added capture constraints for browser echo cancellation, noise suppression, and automatic gain control.
- Saved audio profile in browser localStorage (`livestudio.audio.settings`).
- Creator Studio recording now uses the processed microphone track when the audio profile is enabled. Camera preview remains on the original camera/microphone stream.

## Scope and limitations
- The processed audio chain is integrated into Creator Studio local recording.
- LiveKit's live microphone publication is not yet routed through this processor. Live broadcast audio remains on the existing LiveKit microphone path until a dedicated live audio track replacement stage is implemented and tested.
- Browser noise suppression and echo cancellation depend on browser/device support; they are capture constraints, not a standalone AI denoiser.
- Settings are local to the browser profile and device; they are not synchronized to an account or server.

## Validation
- `node --check` passed for `livestudio-audio.js` and `livestudio-creator.js`.
- .NET build was not run because the .NET SDK is unavailable in the build environment.

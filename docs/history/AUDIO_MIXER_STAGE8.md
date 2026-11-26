# Stage 8 — Shared Audio Mixer

- Added a reusable `AudioMixerPanel` component and embedded it directly in both Live Studio and Creator Studio.
- The same browser-local audio profile is used by both workspaces.
- The Web Audio processing graph supports live parameter updates while a processed track is active.
- Creator Studio recording continues to process its microphone track through the shared graph.
- Live Studio now creates the microphone capture using the saved browser audio constraints, then publishes a processed microphone track to LiveKit when processing is enabled.
- Added a refresh hook so saving a profile reconfigures the currently published LiveKit microphone processing chain without leaving the studio.
- Kept the standalone Audio Mixer page in NAV for full settings and input monitoring.

## Validation

JavaScript syntax is checked with Node when available. A .NET build and real LiveKit/browser test must still be performed on the user's Windows machine.

LiveStudio Stage 8 — Shared Audio Mixer

The professional audio mixer is now embedded inside both Live Studio and Creator Studio. Its profile is shared and stored in browser localStorage. Saving settings updates active Web Audio processing graphs; Live Studio publishes the processed microphone track through LiveKit, while Creator Studio uses the same processing chain for recording.

The full Audio Mixer remains available from the navigation menu. See AUDIO_MIXER_STAGE8.md for implementation notes.

Important: Run a .NET build and test both studios with a real microphone and LiveKit session on Windows. Browser audio constraints such as echo cancellation and noise suppression are subject to browser/device support.

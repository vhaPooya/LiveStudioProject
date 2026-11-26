# Stage 5 — Live Program Output

## What changed
- Added an opt-in Program Output control to the existing Live Studio page.
- Builds a 1920x1080 / 30fps canvas composition using the active local camera, selected Media Box background (image or looping video), logo placement/animation, lower-third and breaking-news graphics.
- Publishes the canvas video track to the current LiveKit room as the participant's camera source while leaving microphone publication intact.
- Stops the program output by unpublishing the canvas track and restoring the cloned camera media track.
- Automatically stops/restores program output before camera shutdown, camera switching, or LiveKit disconnect.
- Added live output status to the Studio session panel. No new standalone route was introduced because this is a control integrated into the existing Live Studio workflow; Media Box remains accessible from its existing NAV item.

## How to use
1. Open Live Studio and connect to the LiveKit room.
2. Ensure the camera is enabled.
3. Configure background, logo and graphics in Media Box.
4. Return to Live Studio and select **فعال‌سازی خروجی گرافیکی**.
5. The composed canvas video is now the video track published to LiveKit. The microphone remains a separate audio track.
6. Select **توقف خروجی گرافیکی** to restore the raw camera track.

## Technical notes / limitations
- This publishes the composed video to LiveKit participants. It does not by itself send RTMP to YouTube. The existing YouTube encoder workflow remains separate.
- Background, logo and lower-third/breaking-news settings are read when Program Output starts. Stop and start again after changing the Media Box design.
- Screen share remains an independent LiveKit track and is not yet composited into the program canvas.
- Camera is shown full-frame when no background is selected; with a selected background it is shown as a lower-right picture-in-picture window.
- Browser autoplay/device permissions and LiveKit server network configuration still apply.

## Validation
- `node --check` passed for `livestudio-livekit.js` and `livestudio-creator.js`.
- .NET build and browser/LiveKit end-to-end tests were not run because the .NET SDK and a running LiveKit/browser environment were unavailable in the build environment.

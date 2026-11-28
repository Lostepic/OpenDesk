# Changelog

## 0.7.4 (Windows source)

- Use per-monitor Windows Graphics Capture for Android streams and fall back to GDI where capture is unavailable.
- Skip identical frames and reduce capture work on static displays.
- Document the tested signed virtual display driver build with the multi-monitor stutter fix and Windows sign-in setup.

## 0.7.3 (Windows host)

- Hold the last frame on each Android display while a window is dragged on another monitor.

## 0.7.2

- Reorganized the Windows host into Android displays, connection settings and browser sharing sections.
- Added a persistent footer with the Windows build version and a GitHub repository link.
- Includes Android viewer 0.7.1.

## 0.7.1

- Compare a pairing code on both screens and confirm on Android.
- Remember the virtual monitor assigned to each paired viewer.
- Receive newer frames during decoding so slow viewers can skip stale frames.

## 0.7.0

- Encrypts pairing, screen sharing and touch input with TLS.
- Pairs Android devices using a verified host fingerprint and Windows approval. Existing devices pair again once.
- Requires Android 8.0 or later and matching 0.7.0 host and viewer builds.

## 0.6.0

- Added multi-touch gestures, including pinch-to-zoom in supported Windows apps.
- Added live streaming quality controls for Android viewers and reduced encoding work for unchanged frames.
- Preserved remembered devices, automatic reconnection, display positions and per-device virtual displays from earlier versions.
- Includes Android viewer 0.5.0.

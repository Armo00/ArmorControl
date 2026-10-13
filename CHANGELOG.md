# Changelog

## 0.1.2 (Unreleased)

### Planet Map

- Added an independent, touch-friendly Map page with a north-up planetary basemap, ground tracks, orbit readouts, a distance scale, and pan/zoom controls.
- Added automatic extraction of supported in-game global color textures, including the installed Sol/Parallax configuration. Color maps are cached; only low-resolution DDS mip data is read where available. PNG compression runs off the game thread.
- Added per-device relative start-time selectors and 1/3/5/10-hour windows. Defaults use the next five hours, or the specified atmospheric-entry/airless-impact window when its event time is available.
- Added altitude-based solid/dashed/hidden track sections, dateline-safe drawing, real sampled history, and body-rotation-aware orbit predictions. Atmospheric paths use available Trajectories data rather than vacuum substitutes.
- Map paths use on-demand 1 Hz WebSocket updates; equal device windows share calculations. Missing history, missing predictions, unavailable textures, disconnections, and stale map data are reported explicitly.
- Added English and Simplified Chinese localization for the new page without changing the shared server-language behavior.
- Added current-body KSP waypoints, a toggleable marker layer, native-navigation highlighting, and a touch-friendly waypoint list. Centering a waypoint in the web map does not change the game's navigation target; no third-party Waypoint Manager dependency is required.
- Added the currently selected vessel target to the map. Landed/splashed targets use a static position marker; flying targets use a separate purple ground track in the selected future window. Docking-port targets resolve to their vessel. Target tracks follow the same altitude limits and stop at atmospheric entry, impact, the next maneuver, or a SOI transition without borrowing the active vessel's atmospheric prediction. The target layer and camera-centering control do not change the game target. Compilation and offline regression passed; live checks covered the actual CommsSatUltra orbital target at Saturn and a landed lunar base, including native target identity/readouts, independent 5-hour/1-hour windows, purple rendering at 4K, and camera/layer controls. The landed target's coordinates also matched the saved native vessel data and neither window produced a target orbit line. Live splashdown, docking-port, target-clearing and cross-body cases remain pending.
- Made the Show waypoints switch explicit, with a hidden-on-map status and a per-browser preference that survives refresh. It affects map pins and names only, not game navigation or other devices.
- Added read-only map timing diagnostics for first-load texture readback, background PNG encoding, and subscribed map capture.
- Avoided repeated full-scene Trajectories-module searches by caching validated modules per game/scene, with destroyed-object checks. Landed and EVA map frames skip unused prediction and collision calculations.
- Fixed first-frame basemap calibration using a potentially stale ScaledSpace world rotation; calibration now uses body-fixed coordinates.
- Fixed browser basemap decoding under the existing content-security policy, without relaxing that policy. Download and decode failures now have explicit status and rate-limited retries.
- Guarded map sampling outside flight scenes and kept vessel identifiers consistent with existing telemetry channels.
- Server startup logs now report the actual assembly version instead of a hardcoded older version.
- Live checks confirmed corrected Earth basemap alignment, the native Drone Ship waypoint, browser decoding, independent device windows at approximately 1 Hz, body-fixed orbit projection, and atmospheric-path delivery. Safe no-vessel handling was checked during startup, at the space center, and in the editor. The loaded performance-optimized DLL was also checked on Earth and the Moon; detailed native atmospheric-impact matching remains pending. This is not a packaged release.
- On the tested machine, two warm Earth launchpad intervals totaling 31 captures averaged approximately 0.155 ms per map capture with one distinct window, compared with the earlier approximately 29 ms ground baseline. Two Moon ground intervals totaling 31 captures averaged approximately 0.056 ms; a Moon orbit interval with two distinct windows averaged approximately 6.91 ms. These are scene-specific map capture timings, not whole-game frame times. Basemap preparation remains a one-time cost: the latest Earth extraction/readback took approximately 74.1 ms on the game thread and PNG encoding approximately 295.9 ms in the background; Moon timings were approximately 13.0 ms and 275.4 ms respectively. Warm intervals exclude texture preparation and context transitions.
- An observed Saturn interval with two distinct map windows and the orbital target averaged approximately 1.59 ms over 10 warm captures. First Saturn basemap preparation took approximately 572.1 ms on the game thread, with approximately 408.7 ms of background PNG encoding; this one-time cost is separate from steady target updates and can cause a first-load hitch.
- An observed lunar surface interval with a landed target and two distinct windows averaged approximately 0.124 ms over 10 warm captures; the target remained a static square marker with no predicted track in either window.

## 0.1.1

### Localization

- English is now the default language. Simplified Chinese remains supported.
- Language is now shared across all devices connected to the same server. Change it from either the web interface or the in-game ArmorControl toolbar panel; the selection is saved to `settings.cfg` and synchronized automatically.
- Expanded translation coverage for telemetry, flight-recorder charts, part controls, autopilot settings and statuses, tooltips, and accessibility labels.
- Fixed translation matching that could alter parts of unrelated text. Vessel, Kerbal, part, and resource names are preserved.
- Improved handling of rapid language changes, dictionary-loading failures, and settings-save failures.
- Localization files now use English source-text keys, with updated instructions for translation contributors.
- Improved localization file parsing without adding framework dependencies to KSP.

### MechJeb Ascent Guidance

- Ascent settings now synchronize between the web interface and MechJeb without restarting ascent guidance.
- While editing a numeric input, only that field pauses its live updates. Press Enter or leave the field to submit; press Escape to cancel. Toggles and supported drop-down settings apply immediately.
- Each edit updates only the selected setting, avoiding accidental overwrites of changes made in game or on another device.
- Fixed target inclination reverting to its previous value when the native MechJeb window was open. Inclination and LAN changes now update both MechJeb's flight settings and its native input values.
- Added acknowledgement and readback handling, with recovery when a setting update fails or times out.
- Starting ascent now uses MechJeb's current settings. Launch timing and countdown remain options applied when starting ascent, rather than being armed while editing.

### Other Fixes

- Fixed rounding at minute and hour boundaries in MET and countdown displays, preventing values such as `00:09:60.0`.

### Compatibility and Upgrade Notes

- For KSP 1.12.5.
- Requires MechJeb 2.14.3 (assembly version 2.14.3.0) and Vessel Viewer. MechJeb 2.15.x is not supported.
- Trajectories is recommended for the trajectory prediction page.
- Restart KSP after updating the DLL, then refresh the web interface to load the matching frontend.
- Preserve your existing `settings.cfg`. Browser-local language preferences are no longer used; choose the shared language through the web interface or toolbar.

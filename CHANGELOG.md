# Changelog

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

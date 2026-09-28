# Ascent setting synchronization — 2026-09-08

## Follow-up: native MJ window overwrites inclination

Live browser reproduction confirmed that setting 29 degrees acknowledged and briefly
read back as 29, then reverted to 19.61. Decompilation of the installed MJ 2.14.3 DLL
showed that MechJebModuleAscentGuidance maintains independent desiredInclination and
desiredLAN EditableDouble values and writes them to the autopilot during GUI drawing.
The earlier stub omitted this lifecycle, so its passing result was insufficient.

The setter now updates both the autopilot value and the native window's val property
(which also refreshes MJ's text cache). Legacy start and target-plane launch also
synchronize these window values. The regression simulates 120 native window draws
after inclination and LAN edits and checks that neither value reverts.

After the user closed KSP, this follow-up DLL was built into Plugins/ArmorControl.dll
and the full release validation passed. SHA-256:
`52F4E0FF190DA531EA0401174412D408233C77ADAFCC5ABEF402B691682969C2`.
KSP was restarted in normal window mode. The deployment notes below refer to the
previous build, which reproduced the bug. Native-window in-game acceptance testing
of this follow-up is still pending; offline tests are not proof of live resolution.

The new `mechjeb.ascent.setting` command runs through the existing main-thread FIFO.
It accepts one allowlisted field and string value, validates before writing, and never
enables/disables guidance or arms a countdown. It uses the installed MechJeb 2.14.3 types.

The web page protects individual focused numeric inputs, commits on Enter/blur,
cancels on Escape, and sends toggles/selects immediately. Pending fields await both
acknowledgement and rounded telemetry readback. Errors, disconnection and a five-second
timeout restore the latest game value. Other fields continue to receive telemetry.

Profile changes are blocked while ascent is active. Target-plane launch owns its
inclination. Start commands from the updated client use current MJ settings, with
only the locally prepared launch mode/countdown passed as action options.
Legacy start payloads remain supported for older clients.

## Verification

- Independent Release build against installed MJ 2.14.3: zero warnings/errors.
- Frontend field synchronization regression, existing frontend regressions and
  localization tests passed.
- Native setter tests with MJ stubs passed: inclination isolation, no activation
  change, units, range/type validation, missing modules and mode restrictions.
- Existing transport/protocol regression passed.
- Native stubs are not an in-game flight test. No live control commands were sent.

## Deployment status

The changed web sources are in the game-served prototype directory. They require
the new capability and reject setting edits/start on an older DLL.
After the user closed KSP, the new DLL was built into `Plugins/ArmorControl.dll`.
The deployed SHA-256 is `8A3A97EC1498579786F741CFE46E33BBE02367E30E630242B8E1241987C8E552`.
Full release validation, including the new ascent regressions, passed.
KSP was started with a normal visible window at the user's request.
Refresh the browser after startup for live acceptance testing; flight behavior
has not yet been verified in game.

# Embedded Retro Landing

ArmorControl builds the Retro Landing controller and its KSP integration from the source snapshot in `Upstream`. `upstream-revision.txt` records the exact commit from the `KSP_Landing_Assist` repository. `armorcontrol.patch` moves controller creation, configuration and telemetry into ArmorControl. The upstream revision excludes solid motors before constructing the engine catalog for both installations. The sources under `Upstream` are the pinned upstream revision with `armorcontrol.patch` applied. The patch also carries the engine selection cache and search scheduling for ArmorControl. ArmorControl's adapter, web page and protocol are maintained separately from that snapshot.

To update, prepare the desired commit in a local Retro Landing checkout. From the KSP installation directory, run the local import tool with the commit or branch you want to use.

    python BuildTools/ArmorControl.SyncRetroLanding.py D:/KSP/KSP_Git/KSP_Landing_Assist --revision REVISION --check
    python BuildTools/ArmorControl.SyncRetroLanding.py D:/KSP/KSP_Git/KSP_Landing_Assist --revision REVISION

The tool reads committed files, checks the current snapshot for local edits, and tests the host patch on the candidate before replacing the source. If a newer revision changes the patched call sites, reconcile those sites and regenerate `armorcontrol.patch` against the new revision. Without the local tool, export `Source/Controller` and `Source/Ksp` from the desired commit, stage them as `Controller` and `Ksp`, and apply `git apply /path/to/armorcontrol.patch` from the staging directory. Replace the snapshot and revision file together after inspecting the changes.

After an import, build ArmorControl and check settings readback, start/stop and MechJeb handover in a test save. Use a landing replay or a test flight when the upstream guidance changes. Install the integrated ArmorControl DLL and web files together. If the standalone DLL was installed, remove that DLL before starting KSP. The integrated controller imports the previous landing settings on first use and saves them in `GameData/ArmorControl/PluginData/RetroLanding.cfg`.

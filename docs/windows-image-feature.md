# Windows image compatibility feature

The default feature ID is **38943831** and the compatibility target is **Disabled (1)**. Disabling it restored animation in a controlled Windows 11 25H2 investigation with the visible Animation effects toggle on. This observation does not establish a universal Windows-version fault or guarantee animation on another machine. The app uses undocumented Windows feature interfaces, so unavailable or unexpected results remain visible as unknown or failed.

## Current controls

The main page checks Picture mode with an existing lock-screen cache folder, and the selected feature's disabled state on Windows build 26200 or later. Earlier builds do not require the feature prerequisite. An unavailable feature cannot be changed through the button; users whose GIF already animates can continue. The checklist is advisory: **GIF Apply never enables or disables a Windows feature** and does not require every prerequisite to be satisfied.

The Picture check requires Spotlight explicitly off and Slideshow off or absent under a readable Lock Screen registry key. Missing keys, malformed values and conflicting enabled switches remain unconfirmed. The cache check requires an immediate `LockScreen*` directory and excludes reparse points. Polling does not repair permissions, write registry values or request elevation. Opening Windows lock-screen settings does not establish that Picture was selected afterward.

The checklist refreshes every five seconds while the page is loaded, on window activation, and on unlock of the app's session. Navigating away or closing stops page polling and detaches its handlers. Its default expansion follows whether the overall prerequisites need attention; routine refreshes preserve a manual expansion choice while that status stays the same. Activation refreshes do not start diagnostic cache capture.

**Disable Windows feature** is an explicit action on the main page. Settings provides **Enable Windows lock-screen feature** with a warning about animated GIF compatibility. Feature actions cannot run alongside Apply, verification or a diagnostic test. Changing the feature can require administrator approval.

Settings also provides **Save ID** and **Reset to default**. IDs must be positive unsigned 32-bit integers. Save ID shows the requested ID and a warning that an incorrect value may target an unrelated feature; Cancel is the default. Reset directly restores ID 38943831. These controls change the app's future target only. They do not change either the old or new Windows feature's state, and choosing another ID does not establish that it controls animation.

## State changes and reboot

The elevated helper captures the selected ID for the operation, changes runtime state when necessary, and persists the requested state when necessary. For the default ID, the persistent override is:

```text
HKLM\SYSTEM\CurrentControlSet\Control\FeatureManagement\Overrides\8\2920700556
    EnabledState        REG_DWORD  1 (Disabled) or 2 (Enabled)
    EnabledStateOptions REG_DWORD  0
```

Other IDs use their encoded leaf under the same priority-8 root. Requests accept only a validated ID and fixed enable/disable operation; callers cannot choose arbitrary paths, priorities or registry data. Unrelated metadata is preserved. Runtime and stored-override readbacks are evaluated separately. `AlreadyDisabled` accepts an explicitly disabled runtime with no conflicting enabled override; it need not create an override that was previously absent. A requested change succeeds only when its required readbacks are confirmed. Partial changes, elevation denial and uncertain replies are reported separately.

After a confirmed `Disabled` or `AlreadyDisabled` result, the main page offers **Not now** (default) and **Reboot**. The prompt explains that restarting may be needed, asks the user to save their work, and tells them to check the lock screen afterward. Leaving the page or closing dismisses it. Failed or uncertain actions do not offer reboot, and Apply never enforces a restart. An already-applied GIF may start animating after reboot without reapplication.

Reboot explicitly starts the system `shutdown.exe /r /t 0` without `/f`. Command waiting is bounded to ten seconds. A zero exit code records `RequestAccepted`, not a completed reboot; a timeout records `Unobserved`, because Windows may still restart. Launch failures and rejected requests show manual-restart guidance while retaining the successful feature-disable result. No usage analytics event is added for this prompt.

This is a persistent, machine-wide setting. Apply and Remove do not reset it. Enable writes Enabled; it does not restore the historical absence of an override. A true rollback needs the prior state, ownership evidence and later-change checks. Deleting the registry leaf is not a general rollback procedure.

Windows **Accessibility > Visual effects > Animation effects** is independent. The app records `UISettings.AnimationsEnabled` from its own process and does not change the preference. Keep that caller readback, the visible Settings toggle and actual secure-screen playback separate. A false readback alone does not establish that the visible toggle is off or that GIF playback must stop. Visible playback still requires observation on both the lock and sign-in screens.

## Implementation and diagnostic evidence

[WindowsImageFeature](../LockscreenGif.Privileged.Contracts/WindowsImageFeature.cs) reads with `RtlQueryFeatureConfiguration`; the [elevated helper](../LockscreenGif.Privileged.Helper/WindowsImageFeatureRepair.cs) uses `RtlSetFeatureConfigurations` and a persistent registry override. This follows the runtime/persistence distinction in [ViVe's feature manager](https://github.com/thebookisclosed/ViVe/blob/master/ViVe/FeatureManager.cs) and [feature-ID encoding](https://github.com/thebookisclosed/ViVe/blob/master/ViVe/ObfuscationHelpers.cs), without bundling ViVe or modifying Windows binaries. A caller's query does not prove LogonUI's executed branch or frame scheduling.

Apply records a fresh read-only `ApplyResult.WindowsImageFeatureAtApply` snapshot. Environment observations record the generic `Windows image feature` key and Animation effects separately. Legacy keys and older feature-result objects remain readable; missing older evidence is unknown.

`PrerequisiteActions` retains the latest 100 actions from the app lifetime, including feature results, ID edits/resets and restart requests. Export includes the current bounded history, even for actions outside the test interval, and redacts personal details. This history is local and is not sent as usage analytics. Every snapshot and feature action retains its captured ID; later preference edits do not relabel earlier evidence.

`Changed` means an observed runtime or registry change, never playback success. `ChangeOutcomeUnknown` means the response or readback was unavailable; `Changed=false` then does not establish that nothing changed. Recheck the status or repeat the explicit action. Applying a GIF does not repair feature preparation. See [diagnostics](diagnostics.md) for evidence interpretation and [the prerequisite harness](../Tests/Prerequisites.Tests/Prerequisites.Tests.csproj) for synthetic controls coverage.

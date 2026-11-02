# InfraLap MultiWiFi — UAT 0.9

## New in 0.6
- Lists saved WLAN profiles discovered through the Windows Native Wi-Fi API.
- Displays SSID, Windows profile name, adapter and authentication type.
- Passwords remain hidden by default. The selected saved password can be revealed or hidden.
- Uses Windows' WLAN_PROFILE_GET_PLAINTEXT_KEY request. Windows controls whether the process has permission to return a plain-text key; if Windows returns an encrypted key, the app offers to restart as Administrator.
- A selected saved WPA2-Personal profile can be loaded into either connection card.
- Reports actual secondary STA interfaces and the synchronized-connection state exposed by the adapter driver.
- Diagnostic exports include Dual-STA capability information but never Wi-Fi passwords.

## Important Dual-STA limitation
Windows documents the secondary STA connection as automatically synchronized with the primary STA connection. The documented API lets an app request secondary STA connectivity and query the secondary interface list; it does not document an app-controlled way to assign an arbitrary, independent second SSID and password to that secondary interface.

Therefore this UAT build reports Dual-STA capability but does not claim that two different SSIDs can be independently connected through one physical adapter. That requirement needs validation against the target chipset, firmware and driver, and may not be supported by the Windows Dual-STA interface exposed to ordinary apps.

## Existing connection workflow
- Two WPA2-Personal connection cards, each with a separate SSID and password field.
- Nearby Wi-Fi scan, adapter refresh, connect/disconnect actions and per-profile status.
- Each profile is imported into the current Windows user profile. Temporary XML files are deleted after import.
- Separate-adapter operation remains available when Windows exposes two independent WLAN interfaces.
- This app does not bond internet bandwidth.

## Password handling
- InfraLap does not store passwords in its own settings or diagnostics.
- Saved keys are read from Windows WLAN profiles only when requested in the saved-profile panel.
- Plain-text password retrieval is controlled by Windows permissions and is normally restricted to Administrators.
- Use Show/Hide to reveal or conceal the selected saved password. Passwords are hidden by default.
- Passwords are not exported to diagnostic files.

## Supported authentication in the connection cards
The connection cards currently create WPA2-Personal (WPA2-PSK/AES) profiles and accept passphrases of 8–63 characters. Other saved profile types can be displayed, but they cannot currently be converted into the WPA2-only connection workflow.

## UAT checklist
1. Install on Windows 11 x64.
2. Refresh adapters and scan nearby networks.
3. Refresh saved profiles and select a saved WPA2-Personal network.
4. Verify that its password is hidden by default; use Show password and Hide password.
5. Load the selected saved profile into Connection 1 or Connection 2.
6. Check Dual-STA status. A secondary STA count of zero means the driver is not exposing a secondary interface.
7. If a secondary STA is exposed, note that Windows documents the secondary connection as synchronized with the primary; do not treat this as proof of independent SSID selection.
8. Export diagnostics and verify no passwords are present.

Windows 11 x64 UAT build. Not a bandwidth-bonding client.


## Experimental single-adapter second-SSID attempt in 0.7
- Select the same physical Wi-Fi adapter in Connection 1 and Connection 2.
- Connect Connection 1 first, then enter a different WPA2-Personal SSID and password in Connection 2.
- Press "Try same-adapter SSID (experimental)".
- The app queries the secondary STA GUID, requests secondary connectivity, installs a WPA2 profile on that GUID with WlanSetProfile, and submits WlanConnect to that GUID.
- It keeps its WLAN client handle open while the secondary STA session is active, as required by the Windows API. Use "Stop secondary STA" or close InfraLap to release the request.
- After the request is accepted, the app queries the primary and secondary current-connection SSIDs. It reports success only if both interfaces are connected, the SSIDs differ, and the secondary SSID matches the requested SSID.
- Windows may reject WlanSetProfile/WlanConnect on the secondary interface, or the driver may automatically synchronize it to the primary SSID. The app reports the observed result instead of claiming success.
- This is experimental. Microsoft's documented Windows Dual-STA behavior is driver-managed and synchronized; arbitrary independent SSID selection is not guaranteed.



## New in 0.8 — scan-to-select SSID
- Both Connection 1 and Connection 2 SSID inputs are now editable dropdowns.
- Scan nearby networks to populate each dropdown with unique SSIDs discovered by Windows.
- Each dropdown entry displays the SSID plus authentication type, strongest reported signal, channel and radio type when available.
- Select a scanned SSID instead of typing. Hidden or currently out-of-range SSIDs can still be typed manually.
- A scan refresh preserves the user's existing selections or manually entered SSIDs.
- Connection 1 can be pre-populated from the current connected SSID on initial scan; Connection 2 remains independently selectable.
- The saved-profile "Use in Connection 1/2" actions now select the matching SSID from the scanned dropdown when it is visible, or enter the saved SSID as text when it is not currently visible.
- The existing scan output panel remains available for detailed netsh scan diagnostics.


## New in 0.9 — SSID dropdown readability
- Increased contrast for SSID names in both scanned-network dropdowns.
- Uses dark, readable SSID text on a white dropdown surface.
- Network details (authentication, signal, channel and radio type) use a separate readable secondary text colour.
- Added clear hover and selected-item backgrounds so users can identify the active choice.
- Applies consistent high-contrast dropdown item styling to the adapter selectors as well.
- No change to scan parsing, credentials, Windows profiles or Dual-STA connection behaviour.

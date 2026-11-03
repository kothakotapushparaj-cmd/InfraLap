# InfraLap MultiWiFi — UAT 1.0

## New in 1.0 — redesigned control center
- Replaced the previous two-card screen with a dark, modern control-center layout.
- Removed the logo/brand mark from the application header; the screen uses a text-only product title.
- Grouped adapter refresh, network scan, saved profiles and diagnostics into a quick-action toolbar.
- Added clearer connection slot, adapter-targeting and credential-safety summaries.
- Redesigned both connection panels, live adapter readiness, nearby network output and saved-profile library.
- Preserved the existing control names and event handlers so the established connection and diagnostics workflows remain wired.
- Added a persistent activity bar and a scrollable workspace for smaller Windows 11 displays.
- Updated application and installer version to 1.0.0.

## Existing connection workflow
- Two WPA2-Personal connection cards, each with a separate SSID and password field.
- Nearby Wi-Fi scan, adapter refresh, connect/disconnect actions and per-profile status.
- Each profile is imported into the current Windows user profile. Temporary XML files are deleted after import.
- Separate-adapter operation remains available when Windows exposes two independent WLAN interfaces.
- This app does not bond internet bandwidth.

## Saved Windows profiles
- Lists saved WLAN profiles discovered through the Windows Native Wi-Fi API.
- Displays SSID, Windows profile name, adapter and authentication type.
- Passwords remain hidden by default. The selected saved password can be revealed or hidden.
- Uses Windows' WLAN_PROFILE_GET_PLAINTEXT_KEY request. Windows controls whether the process has permission to return a plain-text key; if Windows returns an encrypted key, the app offers to restart as Administrator.
- A selected saved WPA2-Personal profile can be loaded into either connection card.
- Diagnostic exports never include Wi-Fi passwords.

## Important Dual-STA limitation
Windows documents the secondary STA connection as automatically synchronized with the primary STA connection. The documented API lets an app request secondary STA connectivity and query the secondary interface list; it does not document an app-controlled way to assign an arbitrary, independent second SSID and password to that secondary interface.

Therefore, the experimental same-adapter action is not guaranteed to connect a different second SSID. The app reports the observed result and does not claim success unless it verifies two different SSIDs. Separate physical Wi-Fi adapters are the supported route for independent simultaneous connections.

## Authentication and password handling
- The connection cards currently create WPA2-Personal (WPA2-PSK/AES) profiles and accept passphrases of 8–63 characters.
- Other saved profile types can be displayed, but they cannot currently be converted into the WPA2-only connection workflow.
- InfraLap does not store passwords in its own settings or diagnostics.
- Plain-text password retrieval from saved Windows profiles is controlled by Windows permissions and is normally restricted to Administrators.
- Passwords are not exported to diagnostic files.

## UAT checklist
1. Install on Windows 11 x64.
2. Refresh adapters and confirm every detected WLAN interface is listed by Windows.
3. Scan nearby networks and select a visible SSID in either connection dropdown.
4. Test Connection One and Connection Two using different physical Wi-Fi adapters.
5. Refresh saved profiles; select a saved network; verify password is hidden by default and Show/Hide works when Windows allows plain-text access.
6. Load a saved WPA2-Personal profile into either connection slot.
7. Check Dual-STA status. A secondary STA count of zero means the driver is not exposing a secondary interface.
8. Export diagnostics and verify no passwords are present.

Windows 11 x64 UAT build. Not a bandwidth-bonding client. A successful installer build does not establish compatibility with every Wi-Fi adapter/driver; hardware testing is still required.

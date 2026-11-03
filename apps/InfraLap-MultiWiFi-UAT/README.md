# InfraLap MultiWiFi — Multi-adapter UAT 1.1

## New in 1.1
- Replaced the fixed two-connection layout with a dynamic adapter workspace.
- Creates one connection card for every Wi-Fi interface currently reported by Windows.
- Each adapter card has its own editable SSID, WPA2-Personal password, connect/disconnect actions, signal indicator and Windows connection readback.
- Displays interface name, adapter description, MAC address, WLAN interface GUID and Plug-and-Play device ID when Windows exposes the values.
- Supports optional labels such as Built-in Intel or TP-Link USB. Labels are stored locally in the current user's AppData folder and keyed by PnP device ID (falling back to MAC/GUID).
- Rebuilds the adapter list after USB plug/unplug and attempts to preserve labels and SSID selections by hardware identity.
- Reports VERIFIED only when Windows reports the requested SSID connected on the selected interface.
- Shows a warning before switching an adapter that is already connected to another SSID.
- Saved Windows WPA2 profiles can be loaded into any selected detected adapter.
- Diagnostics include WLAN and PnP hardware details but never Wi-Fi passwords.
- Retains the logo-free dark control-center design.

## Hardware identity
Windows interface names and GUIDs identify active WLAN interfaces. InfraLap also queries the Windows Win32_NetworkAdapter inventory to associate interface names with MAC address, manufacturer and PnP device instance ID. Identical model names can be distinguished using interface name, MAC address and PnP instance ID.

If the PnP ID is unavailable, InfraLap falls back to MAC address, GUID and interface name. A USB adapter that is unplugged and re-enumerated may receive a new interface name or GUID; refresh adapters after plugging it back in.

## Connection model
- One card represents one WLAN interface reported by Windows.
- One physical Wi-Fi adapter normally maintains one infrastructure Wi-Fi client connection at a time.
- Independent simultaneous connections to different SSIDs require separate physical Wi-Fi adapters and compatible drivers.
- InfraLap uses netsh wlan to add a WPA2-Personal profile to the selected interface and request connection.
- Windows status is polled after the request. InfraLap reports a verified connection only when the selected interface reports the requested SSID.
- The connection editor supports WPA2-Personal (WPA2-PSK/AES) passphrases from 8 to 63 characters. Open, enterprise and other security profile types are not created by this editor.

## Password handling
- Connection passwords are entered and processed locally.
- The app does not save connection passwords in its own settings.
- Temporary XML profile files are deleted after the Windows profile-import attempt (best-effort cleanup).
- Saved-profile passwords are hidden by default and shown only when requested.
- Plain-text access to saved Windows WLAN keys is controlled by Windows permissions.
- Passwords and saved keys are never included in diagnostic exports.
- Adapter labels are stored in the current user's AppData folder under InfraLap MultiWiFi; no passwords are stored in that file.

## Important limitations
- A successful Windows build does not prove compatibility with every USB Wi-Fi chipset, hardware revision or driver.
- The USB adapter must appear as a separate WLAN interface before it gets its own card.
- Windows Dual-STA is driver-managed and synchronized; InfraLap does not promise arbitrary independent SSIDs on one physical adapter.
- This app manages WLAN interfaces; it does not bond internet bandwidth.
- PnP metadata is queried through Windows CIM inventory. The app falls back to MAC/GUID/name when PnP metadata is unavailable.

## UAT checklist
1. Install on Windows 11 x64.
2. With only the built-in Wi-Fi adapter enabled, verify one adapter card is shown.
3. Plug in the USB Wi-Fi adapter and install its Windows driver if required, then select Refresh adapters.
4. Confirm a second card appears with a separate Windows interface and hardware identity.
5. Label the adapters Built-in Intel and TP-Link USB.
6. Scan nearby networks; choose separate SSIDs on the two cards.
7. Connect each adapter independently and verify both report the correct SSID.
8. Disconnect one adapter and verify the other stays connected.
9. Unplug/replug the USB adapter, refresh, and check label restoration.
10. Export diagnostics and verify no Wi-Fi passwords are present.

Windows 11 x64 UAT build. Real hardware testing is required after the USB adapter arrives.

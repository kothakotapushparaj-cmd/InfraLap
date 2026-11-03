# InfraLap MultiWiFi — Multi-adapter UAT 1.1

## New in 1.1
- Replaced the fixed two-connection layout with a dynamic adapter workspace.
- Creates one connection card for every Wi-Fi interface currently reported by Windows; the UI is no longer limited to two cards.
- Each adapter card has its own editable SSID, WPA2-Personal password, connect/disconnect actions, signal indicator and connection readback.
- One card is created per detected Windows WLAN interface.
- Displays the Windows interface name, adapter description, MAC address, WLAN interface GUID and Plug-and-Play device instance ID when Windows exposes those values.
- Supports optional adapter labels such as Office Wi-Fi or Backup USB. Labels are stored locally in the current user's AppData folder and keyed by Plug-and-Play ID (falling back to MAC/GUID).
- Rebuilds the adapter list after USB plug/unplug and attempts to preserve adapter labels and SSID selections by hardware identity.
- Reports VERIFIED only when Windows reports the requested SSID connected on that interface.
- Shows a warning before switching an adapter that is already connected to another SSID.
- Saved Windows WPA2 profiles can be loaded into any selected detected adapter.
- Diagnostics include WLAN interface and Plug-and-Play details but never include Wi-Fi passwords.
- Keeps the logo-free dark control-center design.

## Hardware identity
Windows interface names and GUIDs identify the active WLAN interfaces. InfraLap also queries the Windows Win32_NetworkAdapter inventory to associate the interface with its MAC address, manufacturer and PnP device instance ID. Identical model names can therefore be distinguished by interface name, MAC address and PnP instance ID.

If the PnP ID is not available, InfraLap falls back to the MAC address, GUID and interface name. A USB adapter that is unplugged and re-enumerated may receive a new Windows WLAN interface name or GUID; refresh adapters after plugging it back in.

## Connection model
- One card represents one WLAN interface reported by Windows.
- One physical Wi-Fi adapter can maintain one normal infrastructure Wi-Fi client connection at a time.
- Independent simultaneous connections to different SSIDs require separate physical Wi-Fi adapters and compatible drivers.
- InfraLap uses netsh wlan to add a WPA2-Personal profile to the selected interface and request connection.
- Windows status is polled after the request. InfraLap reports a verified connection only when the selected interface reports the requested SSID.
- The connection editor currently supports WPA2-Personal (WPA2-PSK/AES) passphrases from 8 to 63 characters. Open, enterprise and other security profile types are not created by this editor.

## Password handling
- Connection passwords are entered and processed locally.
- The app does not save connection passwords in its own settings.
- Temporary XML profile files are deleted after the Windows profile-import attempt (best-effort cleanup).
- Saved-profile passwords are hidden by default and shown only when requested.
- Plain-text access to saved Windows WLAN keys is controlled by Windows permissions.
- Passwords and saved keys are never included in diagnostic exports.
- Adapter labels are stored in the current user's AppData folder under InfraLap MultiWiFi; no passwords are stored in that file.

## Important limitations
- A successful Windows build is not proof of compatibility with every USB Wi-Fi chipset, revision or driver.
- The USB adapter must be detected by Windows as a separate WLAN interface before it gets its own card.
- Windows Dual-STA is driver-managed and synchronized; InfraLap does not promise arbitrary independent SSIDs on one physical adapter.
- This app manages network interfaces; it does not bond or combine internet bandwidth.
- PnP metadata is queried through the Windows CIM hardware inventory. The application falls back to MAC/GUID/name when PnP metadata is not available.

## UAT checklist
1. Install on Windows 11 x64.
2. With only the built-in Wi-Fi adapter enabled, verify exactly one adapter card is shown.
3. Plug in a USB Wi-Fi adapter, install its Windows 11 driver if required, and select Refresh adapters.
4. Confirm a second card appears with a different Windows interface name and separate MAC/PnP identity.
5. Give each adapter a helpful label such as Built-in Intel and TP-Link USB.
6. Scan nearby networks; choose separate SSIDs on the two adapter cards.
7. Connect each adapter independently and verify both cards report the correct SSID.
8. Disconnect one adapter and verify the other remains connected.
9. Unplug/replug the USB adapter, refresh, and check whether its label and identity are restored.
10. Export diagnostics and verify that no Wi-Fi passwords are included.

Windows 11 x64 UAT build. Hardware testing is required after the USB adapter arrives.

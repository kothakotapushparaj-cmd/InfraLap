# InfraLap MultiWiFi — Multi-adapter UAT 1.2

## New in 1.2 — Per-adapter traffic monitor and connection feedback
- Each detected WLAN adapter card shows cumulative received and sent bytes, formatted as KB/MB/GB.
- Each card also shows live receive and send rates in Mbps, calculated from successive Windows per-interface IP-statistics counter samples.
- Traffic counters are matched to the WLAN interface by interface GUID first, MAC address second, and Windows interface name as a fallback.
- Totals are Windows interface counters; they accumulate while the interface is active and can reset when Windows, the driver or the adapter resets. They are not a billable data-usage meter and may include local-network traffic.
- Connection verification now waits up to 20 seconds and distinguishes authentication/association, connected-to-different-SSID, and not-connected outcomes.
- Connection passwords are retained in the relevant card when the connection is not verified, so the user can correct the password and retry. A password is cleared after the requested SSID is verified.

## Existing 1.1 features
- Dynamic card for every Wi-Fi interface reported by Windows.
- Adapter labels, MAC/GUID/PnP identity, separate SSID/password controls, connect/disconnect and saved profile management.
- Passwords excluded from diagnostics.
- Independent simultaneous connections require separate physical Wi-Fi adapters.
- WPA2-Personal is the supported connection-editor authentication mode; other saved profile types can be displayed but not created with this editor.

## Important limitations
- A successful CI build does not confirm compatibility with every USB chipset, hardware revision or driver.
- The TP-Link adapter must appear as a separate WLAN interface before a second adapter card can be displayed.
- Per-adapter live rates are calculated from Windows counters and can vary with the one-second sample interval.
- The app manages Wi-Fi interfaces; it does not bond or combine internet bandwidth.
- Windows Dual-STA does not guarantee arbitrary independent SSIDs on a single physical adapter.

## UAT checklist
1. Install on Windows 11 x64.
2. Verify the Intel internal WLAN adapter is shown as one card.
3. Plug in the TP-Link Archer T2U Plus; install the correct driver if required and refresh adapters.
4. Verify a second card appears for the USB WLAN interface.
5. Set readable labels such as Built-in Intel and TP-Link USB.
6. Confirm each card shows its own received MB, sent MB, live receive Mbps and live send Mbps.
7. Connect each adapter to a different WPA2-Personal SSID and verify the SSID readback independently.
8. Disconnect one adapter and confirm the other stays connected.
9. Check diagnostics contain no Wi-Fi passwords.

Windows 11 x64 UAT build. Hardware testing is still required on the target laptop and USB adapter.

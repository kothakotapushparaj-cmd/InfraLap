# InfraLap MultiWiFi — Two-profile UAT 0.5

## New in 0.5
- Two independent connection cards, each with its own SSID and WPA2-Personal password.
- Separate Wi-Fi adapter selection for each connection.
- Import a temporary WLAN profile into the current Windows user profile, then request connection on the selected adapter.
- Separate connect and disconnect actions and per-profile status.
- Nearby network scan and password-free diagnostic export.
- The app does not store passwords in its own settings. The temporary XML credential file is deleted after Windows imports it; Windows manages the saved WLAN profile.

## Simultaneous connection requirement
For this UAT build, assign a different physical Wi-Fi interface to each connection. Example: built-in Wi-Fi adapter for Connection 1 and a compatible USB Wi-Fi adapter for Connection 2.

If both profiles select the same interface, the app will save the second profile to Windows but will not switch that adapter away from the other profile. One conventional Wi-Fi interface should not be assumed to maintain two simultaneous independent associations. Windows Dual-STA support is not implemented in this build.

## Security and supported authentication
- This build targets WPA2-Personal with AES and passphrase passwords of 8–63 characters.
- It does not support WPA-Enterprise/802.1X, captive portal logins, WEP, WPA3-SAE-specific setup or open networks yet.
- Passwords are not printed into logs or diagnostic exports.
- WLAN profiles are added for the current Windows user (user=current).

## UAT checklist
1. Install on Windows 11 x64.
2. Connect built-in Wi-Fi and one additional USB Wi-Fi adapter.
3. Refresh Wi-Fi adapters and ensure both appear with different interface names.
4. Enter SSID/password for each connection and assign a different adapter to each.
5. Connect Connection 1, then Connection 2.
6. Refresh adapters and verify the two adapters show their respective SSIDs and connected state.
7. Export diagnostics if either profile fails. The diagnostic file does not contain passwords.

This is an adapter/profile UAT. It does not bond internet bandwidth or create 2–5 radios in software.

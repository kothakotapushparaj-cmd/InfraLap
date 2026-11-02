# InfraLap MultiWiFi — Phase A UAT (0.4)

This Windows 11 x64 prototype checks the wireless environment before multi-connection development proceeds.

## Included
- Windows WLAN interface discovery using netsh.
- Nearby SSID scan using netsh.
- Display of wireless driver details.
- Request a connection using an existing Windows Wi-Fi profile.
- Disconnect a selected WLAN interface.
- Export local diagnostics.

## Technical scope
This is a hardware discovery and saved-profile UAT build. It does not create additional Wi-Fi radios, guarantee simultaneous links, or bond bandwidth. Dual-STA and multiple independent radio behavior must be validated on the actual adapter and driver before those controls are added.

## Build output
The GitHub Actions job creates a self-contained Windows x64 executable and an Inno Setup installer named InfraLap-MultiWiFi-Setup-x64.exe.

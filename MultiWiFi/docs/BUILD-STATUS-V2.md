# InfraLap MultiWiFi v2.0.0 development build status

## Implemented in source

- Windows WPF client with InfraLap Solutions logo and square shield application icon.
- Dedicated True Bonding window with two physical Wi-Fi path selectors, relay address, UDP port, TLS SHA-256 pin and pairing token fields.
- Independent TLS-protected QUIC client connection attempts bound to each selected Wi-Fi adapter's local IPv4 address.
- Shared multipath session with initial equal-weight round-robin striping and per-path byte counters.
- Wintun Layer-3 packet device and client/relay packet bridge.
- Windows relay executable using QUIC and a shared Wintun interface.
- Windows PowerShell scripts for relay identity generation, relay NAT/firewall configuration, client route activation and route restoration.
- Smoke-test source for two-path striping, failover and recent duplicate suppression.

## Checks completed in this workspace

- Source ZIP integrity check: passed.
- XAML, project and application manifest XML parse: passed.
- GitHub Actions workflow YAML parse: passed.
- Project-reference paths: statically inspected.

## Checks not completed

- C# compilation: not run; this workspace does not have the .NET 10 SDK/MSBuild.
- Windows installer compilation: not run; Windows runner and Inno Setup job has not yet completed.
- Smoke test execution: not run because .NET is unavailable here.
- Windows client/VPS runtime test: not run.
- Two-modem single-ordinary-TCP-download acceptance: not run.

## Known limitations

- Development build only. Client data plane runs in the elevated WPF process, not a separately managed Windows service.
- Relay runs as a foreground process, supports one bonded client session per process and uses one shared Wintun interface.
- Scheduler is initial equal-weight round-robin; adaptive measurements of path goodput, RTT and loss are not yet implemented.
- IPv4 tunnel only. IPv6 leak prevention/kill switch and crash-safe route recovery are not release-validated.
- No independent security audit has been completed.

**Release status:** source prepared for Windows build and UAT; no verified v2 installer is included in this source package.

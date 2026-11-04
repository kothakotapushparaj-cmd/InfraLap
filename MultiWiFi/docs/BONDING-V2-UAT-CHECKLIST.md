# InfraLap MultiWiFi v2 — True Bonding UAT Checklist

**Status:** Development prototype; not production certified.

## Test environment

- [ ] Windows 11 x64 client with both Wi-Fi adapters supported by current drivers.
- [ ] Two separate broadband services/modems; each Wi-Fi adapter has a different local IPv4 address and independent internet uplink.
- [ ] Windows Server 2022 or later x64 VPS with public IPv4 and inbound UDP 4433 permitted by both Windows Firewall and provider firewall.
- [ ] Relay TLS certificate, certificate SHA-256 pin and high-entropy pairing token created using `New-InfraLapRelayIdentity.ps1`.
- [ ] Wintun runtime is present in the client and relay package.
- [ ] Existing Hyper-V, Docker, HNS and WinNAT configuration on the VPS has been checked before creating the InfraLap NAT.

## Installation and recovery

- [ ] Client installer launches with the InfraLap Solutions wordmark and shield icon.
- [ ] Client is elevated before creating the Wintun adapter or changing routes.
- [ ] Relay starts on the Windows VPS and prints its TLS SHA-256 certificate fingerprint.
- [ ] Relay NAT setup refuses to overwrite an unrelated existing WinNAT object.
- [ ] Client establishes two QUIC connections to the same relay, each bound to a different local Wi-Fi IPv4 address.
- [ ] Client route table includes a host route for the relay through each selected physical interface and the IPv4 default route through the Wintun tunnel.
- [ ] Stopping the tunnel removes only InfraLap-created routes/addresses and preserves original physical Wi-Fi routes.
- [ ] After client/relay restart or unexpected process termination, the recovery procedure restores the original route table before another test.

## Single-download bonding acceptance (mandatory)

- [ ] Confirm the two adapter connections are genuinely independent internet services, not two Wi-Fi adapters connected to the same modem.
- [ ] Start exactly one ordinary TCP download from a server that does not use HTTP Range or multiple parallel download connections.
- [ ] During that same transfer, record timestamped received-byte deltas from each physical Wi-Fi adapter.
- [ ] Confirm payload traffic is observed simultaneously over both QUIC paths, not merely from Windows background traffic or adapter counters.
- [ ] Confirm the client combined rate matches the sum of the two tunnel-path payload rates within expected measurement overhead.
- [ ] Confirm the download completes and compare actual end-to-end goodput with each broadband connection tested individually.
- [ ] Repeat with different path latency, congestion and throughput; note if the equal-weight scheduler underuses or overloads one path.
- [ ] Disconnect one modem during the transfer and verify the remaining path continues, then reconnect and verify the second path rejoins.

## Security and reliability tests still required

- [ ] Invalid token is rejected.
- [ ] Incorrect certificate pin is rejected.
- [ ] UDP path interruption and QUIC reconnect behavior are tested.
- [ ] IPv6 leakage is either prevented by a verified kill switch or clearly blocked from release.
- [ ] Crash/power-loss route recovery is reliable.
- [ ] Long-duration traffic does not grow memory without bound.
- [ ] Relay limits and client isolation are reviewed before allowing more than one client.
- [ ] Independent security review is completed before production use.

## Release gate

Do not label the build as a functioning true-bonding release until the single ordinary TCP-download test above passes on the target Windows client and relay using two independently provisioned internet connections. A successful compile, two connected Wi-Fi adapters, a speed-test sum or two separate downloads are not acceptance evidence.

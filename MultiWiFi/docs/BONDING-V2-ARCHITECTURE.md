# InfraLap MultiWiFi v2 — True Bandwidth Bonding Design

## Product goal

Let normal Windows applications use one virtual Internet connection whose packets can travel over two or more independently connected physical adapters. A single long-lived TCP download must be able to consume both physical Wi-Fi paths during the same transfer.

This is different from:
- adding up per-adapter byte counters;
- distributing separate app connections across adapters;
- downloading a file as several HTTP Range requests.

Those approaches can be useful, but they do not deliver system-wide packet-level bonding for one ordinary TCP flow.

## Non-negotiable networking constraint

A Windows client cannot make an arbitrary Internet server combine two unrelated TCP paths merely by choosing two local Wi-Fi adapters. The traffic needs a common remote aggregation point. The remote point receives packets over both physical paths, reorders/forwards the inner traffic, and provides one public Internet egress address.

The upstream MPTCP project documents native MPTCP support in the Linux kernel (v5.6+) and notes that Windows usage is via WSL2; ordinary endpoints without MPTCP continue as plain TCP. RFC 8684 describes MPTCP as a transport extension negotiated by both endpoints. Therefore, do not market the Windows app as native MPTCP.

## Proposed InfraLap architecture

```
Windows applications
        |
Windows route table
        |
InfraLap virtual Layer-3 adapter (Wintun)
        |
InfraLap Bonding Agent (elevated Windows service)
        |                         |
UDP path A, bound to Wi-Fi A      UDP path B, bound to Wi-Fi B
        |                         |
        +------ Internet ----------+
                    |
           InfraLap Linux Relay
           authenticate / decrypt
           packet sequencing / reordering
           route inner IP packets
           NAT to relay public IP
                    |
               Public Internet
```

Return traffic follows the reverse path: the relay encapsulates packets and schedules them over the live client paths. The client writes the recovered inner IP packets into Wintun, so normal Windows TCP/UDP applications see one logical network route.

### Windows client

- Preserve the current WPF management app and existing adapter/profile features.
- Add a separate elevated Windows service for the packet data plane; keep UI and privileged packet I/O isolated.
- Use the signed Wintun Layer-3 adapter to carry inner IPv4 and IPv6 packets.
- Discover each physical adapter's interface index, local source address, gateway and link state.
- Create one UDP transport socket per selected physical adapter. Bind each socket to its adapter and explicitly select the outbound interface; verify the observed path before marking it healthy.
- Use a path manager with health probes, RTT/loss/goodput estimates and configurable scheduler modes (weighted striping, latency-aware, failover).
- Add a jitter/reorder queue and bounded memory. Do not allow an unbounded queue to turn packet reordering into memory exhaustion.
- Exclude the relay endpoint from the virtual route to prevent tunnel recursion.
- Provide an explicit disconnect/restore path so stopping the service restores the original Windows routes and DNS.
- Start with IPv4-only full-tunnel mode; add IPv6 only after IPv6 routing, leak prevention and DNS behavior are tested.

### Linux relay

- A public Linux VPS with a stable public IPv4 address and reachable UDP port is required.
- Authenticate clients and assign each client a distinct tunnel IPv4 address.
- Maintain independent NAT path mappings for each client transport socket.
- Decrypt and validate encapsulated packets; reject invalid tags, stale/replayed sequence numbers, malformed lengths and unknown sessions.
- Forward inner packets through a TUN interface and NAT them to the VPS public egress interface.
- Encapsulate Internet return packets and schedule them back across the active client paths.
- Apply per-client bandwidth, packet-rate and session limits.

### Secure transport requirements

Do not invent ad-hoc encryption or reuse the Wi-Fi password as a tunnel key. Use a reviewed authenticated key exchange (prefer a standard Noise pattern or WireGuard-compatible construction), AEAD packet protection, per-session keys, nonces that cannot repeat, replay windows, authenticated path registration and key rotation. Do not ship a prototype as production-ready until the protocol receives independent security review.

### Why not simply enable MPTCP?

MPTCP is a standards-based solution when both endpoints and their stacks support it. The Linux kernel supports MPTCP and the MPTCP project documents the client/server negotiation and fallback behavior. InfraLap needs to support ordinary Windows applications talking to ordinary Internet servers, so a relay tunnel is the appropriate abstraction for a Windows-first product.

### Rejected shortcuts

- **Counter summation:** telemetry only; not bonding.
- **Route metrics / interface priority:** chooses a path; does not split one flow.
- **Windows NIC teaming:** not a general Internet multi-WAN bond across unrelated Wi-Fi gateways.
- **HTTP Range splitting:** accelerates only compatible file downloads and only as an app-specific downloader; not system-wide packet bonding.
- **Unfinished third-party proof of concept:** do not ship it unchanged. A reviewed, tested data plane is required.

## Delivery phases

1. **Inventory and path validation:** enumerate adapters and test one bound UDP path to the relay per adapter. Report actual interface index, local IP, route, RTT, loss and observed bytes per path.
2. **Relay handshake:** authenticate the client, register each path independently, assign tunnel address and verify that both paths reach the same session.
3. **Bonding data plane:** Wintun packet read/write, authenticated tunnel packets, path scheduling, replay protection and bounded reordering.
4. **Routing and recovery:** install routes safely, prevent relay recursion, restore previous routes on crash/stop, handle adapter disconnect/reconnect.
5. **Verification:** run a single TCP download through the tunnel while measuring both underlying adapters; compare one-path baseline, two-path bonded result, relay throughput, packet loss and latency. A successful test must show payload bytes on both Wi-Fi adapters during the same one-download session, not merely background probes.
6. **Release hardening:** signed installer and service, signed Wintun distribution, elevated install/uninstall rollback, diagnostics, privacy notice, independent protocol/security review and physical hardware UAT.

## Acceptance criteria

- A single ordinary TCP download (not a set of HTTP Range sub-downloads) traverses the virtual adapter.
- During that exact download, both selected Wi-Fi adapters carry sustained encrypted tunnel payload.
- The relay forwards all traffic through one public egress address.
- The sum of delivered goodput is measured end-to-end; do not promise the arithmetic sum of ISP speed tests. RTT mismatch, congestion, loss and relay capacity can reduce the result.
- Disconnecting either adapter does not crash the client; traffic continues on the surviving path after recovery.
- Stopping/uninstalling the service restores the original routes and DNS.
- No Internet traffic leaks outside the tunnel when full-tunnel bonding is enabled, unless the user explicitly chooses a bypass rule.

## Current status

v1.2.1 does not contain a bonding tunnel, Wintun virtual adapter, relay protocol or server. Its combined traffic display is an arithmetic sum of adapter counters and must not be represented as bonded throughput.

This document is an engineering design, not a claim that the client or relay has been implemented or security-audited.

# W0 — the network: inventory, decisions and slices

Plan step W0 opens phase W with the network milestone M7, whose steps PLAN.md left "detailed at the start of W". This
document records what M7 asks, what exists, what was measured, the decisions the documents leave open with a
recommendation for each, and the slices. Made on 2026-10-19, after phase P.

## 1. What M7 asks

- **Implementation Strategy §62–§68.**
  - The goal is "normal .NET networking": virtio-net through the UHI and the device manager, an Ethernet-like link, IP,
    TCP, UDP, DNS and TLS. The layering is "defined separately" (§64) and was never written; this document writes it.
  - `Socket`, `TcpClient`, `UdpClient`, `NetworkStream` and `HttpClient` work (§65).
  - The key test is `HttpClient.GetStringAsync("https://example.com")` (§66).
  - A later criterion is an ordinary ASP.NET Core application (§67).
  - Definition of done (§68): virtio-net works, TCP and UDP operate, DNS resolves, `HttpClient` does HTTPS, and ASP.NET
    Core runs with minimal or no WitOS-specific source changes.
  - Networking comes only as the milestones need it (§150, invariant 13). New system function is preferably managed
    code (§133–§134).
- **RFC 0011.**
  - The network service is a layer-3 .NET service (§3).
  - Sockets and `epoll` are absent until M7 and fail with `ENOSYS` (§9.3).
  - WitOS adds no libc symbols (§10.2.1).
  - Large data travels as memory objects (§7.6).
- **RFC 0015.**
  - Sockets reach the network service through channels (§5).
  - `pal_networking.c` is a listed patch site, taken only in the order of §4.
  - The Unix form's `System.Security.Cryptography.Native` is over OpenSSL (§2).
  - Until M7, `System.Net.Sockets` and `System.Net.Security.Native` fail explicitly (§8).
- **RFC 0007.**
  - A network driver exposes packet queues (`NetworkQueueDevice v1`, §55, §111).
  - TCP/IP stays above the driver (§11, §120).
  - The driver gets its MMIO, lines and DMA alone (§109).
- **RFC 0006, 0002 and 0004.**
  - The network stack is services and resources, and the standard socket APIs keep standard semantics above it (RFC 0006
    §136–§137).
  - The capabilities are connect, accept, send and receive, and a public listener is stronger than an outbound
    connection (RFC 0006 §159–§163, RFC 0004 §68).
  - A compatibility environment may receive a general network capability by application policy (RFC 0004 §70).
  - The system stays whole without a network (RFC 0006 §154).
- **Platform Components Survey.**
  - `HttpClient` and `SslStream` are the standard .NET ones, with no WitOS replacement (§2).
  - Cryptography is standard .NET first, and BouncyCastle is a fallback that never bypasses the BCL (§26).
  - The survey names no TCP/IP stack, resolver or TLS library.
- **Consumers waiting.**
  - PowerShell remoting (RFC 0020 §19).
  - CSharpRepl's `#r "nuget:"` and its `connect` (C2.5: Unix sockets).
  - The installer's HTTPS sources (C4.3 §6).
  - CSharpFar's `HttpClient` (C4.2).
  - Files into the guest without a disk rebuild.

## 2. What exists

- **The device.**
  - The device manager has translated virtio type 1 as `NetworkQueueDevice` since D1.1 (`PciTranslator`), and the driver
    table has no network driver.
  - Measured on 2026-10-19 with `-netdev hubport -device virtio-net-pci` in `runtime-terminal`, which passed on the
    three ISAs. The device is `1af4:1000` (transitional, modern capabilities present) with four regions and one line on
    every board:
    - q35 places it at `00:02.0` on line 5, which its PIRQ routing gives it beside the block disks and the console.
    - The virt boards place it at `00:01.0`.
  - The table holds 11 of its 16 descriptors on q35 and 6 on the others.
- **The driver model** (ADR 0025): a NativeAOT driver per device, the device moved over its grant channel, and
  `OutWit.OS.Hardware.Virtio`'s transport and split virtqueue, which the block and console drivers share.
- **libc.**
  - Every socket call returns `ENOSYS` (`syscall.c`'s default).
  - `epoll` serves pipe ends alone (N4.2).
  - There is no `/etc/resolv.conf`, `/etc/hosts` or `/etc/services`.
  - ABI-2 1.1 already records musl's `socket`, `getaddrinfo` and `epoll_*` names in `libc.so`, so the M7 libc changes
    behaviour, not symbols.
- **.NET as built for witos.**
  - `System.Net.Sockets` is the Unix flavor, failing at its `socket` call.
  - `System.Net.Http` has no Unix flavor for witos and takes the platform-neutral one, so `HttpClient` throws
    `PlatformNotSupportedException` (N3.2).
  - `System.Security.Cryptography` takes the browser's set through a patch: managed hashes, HMAC and KDFs, X.509 and TLS
    absent (R4.1).
  - No OpenSSL or GSSAPI shim is built (R1.2b).
  - `System.Native`'s networking has no witos branch and builds as on Linux. Its calls:
    - the socket calls (`socket`, `connect`, `bind`, `listen`, `accept4`, `send*`, `recv*`, fourteen
      `getsockopt` and twelve `setsockopt` sites, `shutdown`, `getsockname`, `getpeername`);
    - `sendfile`;
    - `getaddrinfo`, `getnameinfo`, `gethostname`;
    - `getifaddrs`, `if_nametoindex`;
    - `epoll_create1`, `epoll_ctl`, `epoll_wait` (its socket engine);
    - netlink for network change notifications (which fall back to `EAFNOSUPPORT`).
- **QEMU.**
  - Every boot runs with `-nic none`.
  - The Windows package's QEMU has user networking (slirp). The Linux build from the pinned source has none: no
    libslirp was present at its configure. Its backends are socket, stream, dgram, hubport, tap, passt, l2tpv3, bridge
    and vhost-user.
- **The compatibility suite** has no network check, and the matrix lists networking and ASP.NET Core as Planned for M7.

## 3. Decisions

Each decision states the recommendation and its alternative. Decisions 2 and 7 were the author's to take before the
slices that need them (W1.2 and W1.1):
- decision 2 was taken by the author on 2026-10-19: the managed stack;
- decision 7 was taken by W1.1 as recommended.

The others follow from the documents and are recorded for review.

1. **Layering.** The virtio-net driver serves packets, the network service runs the protocols, and the libc's sockets
   speak to the service. These are three processes, as the block driver, the namespace service and the libc's files are
   (RFC 0007 §120, RFC 0002 §45). The driver's packet protocol is the `NetworkQueueDevice` contract: a window of frames
   in a memory object and the queues' notifications on a channel.
2. **The TCP/IP stack: managed** — the author's decision of 2026-10-19. WitOS writes system function in managed code,
   as the documents record.
   - **Managed**, in the NativeAOT network service (`OutWit.OS.Networking`). The documents point there:
     - Strategy §133–§134;
     - the network named among RFC 0011 §3's .NET services;
     - the stack as services and resources (RFC 0006 §136);
     - the precedent of the ext4 provider, written in managed code and judged by e2fsprogs. The stack is judged in the
       same way by the stacks on the other end: QEMU's slirp, the host's sockets through it, and Linux's own stack for
       what slirp does not do.
     - It carries IPv4, ARP, ICMP, UDP, TCP (RFC 9293 with NewReno), a DHCP client and loopback.
   - **Alternative, not taken:** pinned lwIP (BSD-3-Clause) compiled by the pinned clang and linked into the service
     as `libwitos.a` is. It is mature and the quicker way to §68. Its single-threaded raw API fits a service loop. It is
     C in a layer the documents prefer managed.
3. **TLS.** Pinned OpenSSL 3, built after its own rules by the pinned clang against the sysroot, as ICU is (R4.3).
   - This is less a choice than the compatibility contract. The Unix form's `SslStream`, X.509 and most of
     `System.Security.Cryptography` go through `System.Security.Cryptography.Native` over OpenSSL (RFC 0015 §2). A
     managed TLS would replace BCL internals, which the port never does. BouncyCastle stays an application's fallback
     (Survey §26).
   - R4.1's browser-set patch leaves the patch set.
   - The root certificates are a pinned CA bundle (Mozilla's, as curl publishes it, by SHA-256) at the path OpenSSL is
     configured for.
4. **Sockets in the libc.** A socket is a descriptor of the libc, as a volume's file and the terminal are, and it speaks
   a socket protocol to the network service over a session with a window (`witos/socket.h`).
   - `poll` and `epoll` take its readiness from the service's events, as they take a shared pipe's.
   - `pal_networking.c` stays unchanged, so RFC 0015 §4's first preference holds.
5. **DNS.** musl's resolver is unchanged: `getaddrinfo` asks the nameservers of `/etc/resolv.conf` over UDP and TCP and
   reads `/etc/hosts`. The libc serves both files from the network service's configuration (the DHCP lease), as it
   serves `/etc/mtab`, since the boot package is read-only and a board's addresses are not the package's.
6. **Authority.** For M7, every process the process manager starts gets the network service's clients' endpoint, as it
   gets the namespace service's. That endpoint is the general network capability of a compatibility environment (RFC 0004
   §70). The distinctions between connect, listen and listen-public wait for M8's model and are recorded in the
   differences.
7. **The network on the board.** QEMU's user networking (slirp) on both hosts, `-netdev user` with `virtio-net-pci`:
   - It provides DHCP, a DNS forwarder and NAT, and port forwards for the host's test servers.
   - The Linux build gets libslirp from the image (`libslirp-dev`), and its configure says `--enable-slirp`, so a build
     without it fails rather than drops the backend.
   - The gates run against servers the runner starts on the host — an echo server, HTTP, and HTTPS with a certificate of
     a test CA made for the run and trusted in the guest's bundle — so they need no internet. One scenario fetches a
     public HTTPS site, §66 literally; CI runs it and the matrix records it.
   - **Alternative:** a `stream` netdev to the runner, which would then need a TCP/IP stack of its own to terminate the
     guest's connections.
8. **Local sockets.** `AF_UNIX` stream and datagram sockets and `socketpair` are the libc's own, within and between
   processes as shared pipes are (N4.2). `.NET`'s diagnostic IPC and CSharpRepl's `connect` need them, and they need no
   network service.
9. **ASP.NET Core.** The shared framework `Microsoft.AspNetCore.App` is managed alone. The install takes it from the
   stock SDK's packs at the version that matches the runtime's band, under `/dotnet/shared/Microsoft.AspNetCore.App`.
   *W3.2 measured otherwise* ([W3.2-AspNetCore.md](W3.2-AspNetCore.md) §1): the packs are per RID with ReadyToRun code
   CoreCLR refuses on another machine, and none exists for riscv64, so the install builds the framework IL alone from
   dotnet/aspnetcore's pinned release by the repository's own build, one framework for the three ISAs.

## 4. Slices

Every slice closes with its guest acceptance on x64, ARM64 and riscv64.

| Slice | What it adds | Guest acceptance |
| --- | --- | --- |
| **W1.1** The link | user networking on both hosts (libslirp in the Linux image and CI, `--enable-slirp`), `BootRequest.Network`, the virtio-net driver `/system/Drivers/VirtioNet` (feature negotiation, receive and transmit queues, the MAC from the configuration), the packet protocol (`witos/packet.h`) | the device manager's probe sends an ARP request for the gateway and receives slirp's reply through the driver |
| **W1.2** The network service | `/system/Network` with the stack of decision 2: ARP, IPv4, ICMP, UDP, a DHCP client, TCP, loopback; its clients' endpoint in the process manager's start messages | the service's lease from slirp; the device manager's probe connects through the service to the runner's echo server and resolves a name through slirp's forwarder |
| **W2.1** Sockets | the libc's `AF_INET` stream and datagram sockets over `witos/socket.h`, readiness in `poll` and `epoll`, `/etc/resolv.conf` and `/etc/hosts`, `getifaddrs` | `Socket`, `TcpClient`, `UdpClient`, `NetworkStream` and `Dns` under CoreCLR against the runner's servers and over loopback; a Network area in the N1 suite |
| **W2.2** HTTP | `System.Net.Http`'s Unix flavor for witos (a patch of its project, as 8.0's set has) | `HttpClient` over HTTP against the runner's server |
| **W3.1** TLS | pinned OpenSSL, the CA bundle, `System.Security.Cryptography`'s Unix flavor with OpenSSL (R4.1's patch leaves) | `SslStream` and `HttpClient` over HTTPS against the runner's test CA; X.509 in the N1 suite; §66 against the public site in CI |
| **W3.2** ASP.NET Core | the shared framework in the install, port forwards from the host | an ordinary Kestrel application answers the runner's requests (§67) |
| **W4** Local sockets and the rest | `AF_UNIX` and `socketpair`, network interfaces and their change, the matrix and the differences | the diagnostic IPC (`dotnet-counters`-style client) of a process; M7's definition of done (§68) |

The N2.2 suites gain what their projects test once sockets exist; their exclusions for the network leave with W2–W3.

## 5. Measured

- `runtime-terminal` with a virtio-net device (`-netdev hubport,id=n0,hubid=0 -device virtio-net-pci,netdev=n0`, a trial
  argument not committed): PASS on x64, ARM64 and riscv64, with the discovery line
  `Class: NetworkQueueDevice, Provider: Virtio, DeviceId: pci/00:02.0/1af4:1000, Regions: 4, Lines: 1` on q35 and the same
  at `00:01.0` on the virt boards.
- The pinned QEMU's network backends: `socket stream dgram hubport tap user` in the Windows package, `socket stream dgram
  hubport tap passt l2tpv3 bridge vhost-user` in the Linux build.

# E0 — The developer scenario with today's tools

Plan step E0 (phase E, goal Ц6; the author's revision of 2026-10-28): before anything is built for the scenario, carry
a console program written and published on Windows into WitOS on VMware with what the system has today, run it, and
record what works and what is missing. A measurement, as H0 was: it runs on the machine the scenario names, VMware with
x64, and has no gate of its own; the steps it informs (E1–E4) carry the gates.

## 1. What ran

On 2026-10-28, on the Windows host the author develops on:

1. **The program.** `HelloWitOS`, an ordinary console project (`net10.0`, no package), prints the system, the runtime
   identifier, its directory and its arguments, asks a name with `Console.ReadLine`, writes a file under the temporary
   directory and reads it back. `dotnet publish -c Release` with the stock SDK and no RID wrote five files, 180 KB with
   the Windows apphost; `Compress-Archive` made an 81,141-byte zip.
2. **The machine.** The PC's one disk as `shell --pc --prepare` writes it at R9.2's commit, on SATA, in a machine of 2
   processors, 1 GiB and an e1000e on VMware's NAT, started without a window by `vmrun`; its screen read through
   VMware's VNC server on 127.0.0.1, keys typed through it (they reach the PS/2 keyboard, H5 §1.4).
3. **The way in.** At PowerShell's prompt, one line: an `HttpListener` on port 8080 that writes the body of the first
   request to `/data/hello.zip`. The host sent the zip with a `PUT` to the address the network service leased
   (192.168.119.134, printed on the serial port). The direction is the host to the guest, since a server on the host
   needs an inbound rule of the host's firewall, which is the author's to grant.
4. **Unpacked and run.** `Expand-Archive /data/hello.zip /data/hello`, then `dotnet /data/hello/HelloWitOS.dll from
   Windows`, the name typed at its question, then `exit`, which powered the machine off.

## 2. What the screen showed

```text
received 81141
PS /data> Expand-Archive /data/hello.zip /data/hello -Force; Get-ChildItem /data/hello | Format-Table Name, Length
HelloWitOS.deps.json             424
HelloWitOS.dll                  6144
HelloWitOS.exe                162304
HelloWitOS.pdb                 11056
HelloWitOS.runtimeconfig.json    342
PS /data> dotnet /data/hello/HelloWitOS.dll from Windows
Hello from a program published on Windows!
Running on: WitOS 0.1 ABI-1, witos-x64, X64, .NET 10.0.12
From: /data/hello/
Arguments: from Windows
Your name? WitOS
Hello, WitOS! Wrote /data/tmp/hello-witos.txt: WitOS at 2026-10-28T13:58:28.8402744Z
```

| Moment | Seconds after `vmrun start` |
| --- | --- |
| DHCP lease | 9.5 |
| the zip received (sent the moment the listener ran) | 62.0 |
| `Expand-Archive` and the listing on the screen | about 3 after Enter |
| the program's question on the screen | under 3 after Enter |
| the power off after `exit` | 1.0 after Enter |

The 47 s between the prompt and the listener are VNC typing the 240-character line. The keys typed while PowerShell was
still starting (the driver began at a still screen before the prompt) were echoed by the terminal's line discipline
under the console mark and then taken by PSReadLine whole: typing ahead loses nothing, it only leaves its echo where the
mark is drawn afterwards, as a terminal on Linux does.

## 3. What it says

- **Running works.** The bytes the stock SDK publishes on Windows run as they are (Ц2 on VMware): the console's input
  and output, the arguments, the temporary directory, the file system.
- **The way in is the missing part.** Nothing but a hand-written listener and an address read off the serial port
  brings a file in; a person would not do that. E2 (an SMB network volume, the author's choice) and E3 (a USB drive)
  are the ways in a person expects.
- **The Windows apphost is dead weight.** `HelloWitOS.exe` is a Windows executable; the program runs as
  `dotnet HelloWitOS.dll`. The file manager (E4) should run a `.dll` with its runtimeconfig that way, and offer to
  install a folder as an application of the layer, so that it gets a launcher and native code (R9.2).
- **Volumes need names.** The console mark lists `/, /tmp, /boot, /data, /fat`; a drive plugged in or a share mounted
  needs a place and a name a person recognises (E1).

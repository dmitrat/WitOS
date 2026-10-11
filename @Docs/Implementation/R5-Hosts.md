# R5 — The hosts: `dotnet App.dll`

R5 brings up upstream's hosts for `witos`: the `dotnet` muxer, `libhostfxr.so` and `libhostpolicy.so`
(RFC 0015 §7). A program now runs the way it runs on Linux: `dotnet App.dll`, from the shared-framework layout of a
.NET install.

```
[DOTNET] App.dll through hostfxr and hostpolicy on X64: .NET 10.0.7-dev, witos-x64
[DOTNET] App.dll through hostfxr and hostpolicy on Arm64: .NET 10.0.7-dev, witos-arm64
```

## The hosts need no patch

`runtime-witos` builds upstream's `host.native` subset for `witos` on both ISAs with the pinned clang against the
sysroot, as it builds CoreCLR. The output, in `artifacts/bin/witos-<arch>.Release/corehost`, is:

- `dotnet`;
- `apphost`;
- `libhostfxr.so`;
- `libhostpolicy.so`;
- `libnethost.so`.

Nothing in `src/native/corehost` needed a change, for three reasons:

- **Its own path.** The hosts find their own executable through `minipal_getexepath`. That reads
  `/proc/self/exe`, which WitOS lacks, and falls back to `AT_EXECFN`, which libwitos has put on a started program's
  stack since R3.2a.
- **Probing.** The hosts probe with `access` and `stat`, and they read the runtimeconfig and deps.json files through
  the libc's files over the package.
- **Loading.** They load `libhostfxr.so`, `libhostpolicy.so` and `libcoreclr.so` with musl's dynamic linker.

## The layout

The `runtime-dotnet` scenario lays out an install the way the shared framework's packaging lays it out on Linux:

| Path | What |
| --- | --- |
| `/dotnet/dotnet` | the muxer |
| `/dotnet/host/fxr/10.0.8/libhostfxr.so` | the framework resolver |
| `/dotnet/shared/Microsoft.NETCore.App/10.0.8/` | the shared framework: `libhostpolicy.so`, `libcoreclr.so`, `libclrjit.so`, CoreLib, every library of the witos framework (452 assemblies, 53 MiB), the native libraries (`libSystem.Native.so` and the others) and `Microsoft.NETCore.App.deps.json` |
| `/etc/dotnet/install_location` | `/dotnet`, which `apphost` reads in place of a patched `pal.unix.cpp` (RFC 0015 §7) |
| `/app/App.dll`, `/app/App.runtimeconfig.json` | the program and its runtimeconfig: the framework `Microsoft.NETCore.App` 10.0.0, rolled forward to the installed patch, with `System.Globalization.Invariant` true |

**The framework's deps.json** is written by the tool. Upstream's packaging of the CoreCLR runtime pack expects
crossgen'd assemblies and a platform manifest that do not exist for `witos`, so the tool writes the same document the
shared framework's packaging writes:

- one runtime-pack library for the RID;
- every assembly as a runtime asset, with its assembly version from its metadata and its file version from its
  version resource;
- every native library as a native asset.

`hostpolicy` builds the trusted platform assemblies and the native search paths from it.

`tests/User/dotnet_init.c` starts `/dotnet/dotnet /app/App.dll witos` with `posix_spawn` and reports how it ended.
`tests/Runtime.Witos/HostedApp/App.cs` checks what the hosts gave the runtime:

- the framework's deps file under `/dotnet/shared/Microsoft.NETCore.App/`;
- the invariant switch from its runtimeconfig;
- `/app/` as its base directory;
- its command-line argument.

It then prints the framework's description and the RID.

The scenario boots with 512 MiB, since the package carries the whole framework. It requires the program's line and
the root task's report of status 0. `runtime-witos` builds the hosts and boots the scenario on each ISA after
System.Native's check.

## Limits kept explicit

- **`dotnet` runs a program and nothing else.** The SDK's commands are not in the layout.
- **`apphost` is built but not run.** A self-contained or apphost-launched program comes with R6's applications.
- **The framework is the witos build's.** Its version reads `10.0.7-dev`, and its folder carries the pin's `10.0.8`.
- **Every method is JIT-compiled.** No ReadyToRun code exists for `witos`.

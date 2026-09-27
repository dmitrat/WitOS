# Minimal NativeAOT startup workload

This is an ordinary net10.0 executable using the pinned .NET 10.0.8 compiler and standard CoreLib. Main allocates objects and an array, forces a collection and checks static/local roots. Success returns 42. There is no custom managed runtime or reduced CoreLib.

Run `dotnet run --project tools/WitOS.Dev --configuration Release -- runtime-readiness` from the repository root. The command builds/checks the full source profiles, executes the Windows reference and tries a strict link rooted at the actual upstream wmain using source-built WitOS libraries plus real native transport/TLS metadata. runtime-source and runtime-config also include this assessment.

Current result: hosted execution passes; 72 platform symbols remain unresolved. No guest image is linked or booted. wmain is a diagnostic dependency root, not a WitOS startup thunk; image/environment publication, C++ initialization, compiler TLS and managed module initialization require the complete guest startup path.

The report includes input hashes, normal CoreLib/compiler flags, captured SDK native inputs, strict-link diagnostics and measured Windows-reference PE/TLS/unwind characteristics. All generated artifacts stay in artifacts/runtime-readiness. See @Docs/Implementation/NativeAot-Startup-Readiness.md for interpretation and limits.

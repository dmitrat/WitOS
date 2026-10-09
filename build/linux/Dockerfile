# The Linux development host of WitOS (plan steps T2.1a and R1): the .NET SDK of global.json on Ubuntu 24.04, by
# digest; what building the pinned QEMU needs; libxml2, which the official LLVM release's lld loads; cmake, which
# upstream dotnet/runtime's native build drives with the pinned clang and lld; and the libraries the runtime's configure
# of the Linux host requires when it builds the host's JITs and ILC (R1.3). WitOS.Dev's setup pins and verifies every
# other tool: LLVM, QEMU's source, musl, compiler-rt, the LLVM runtimes, libc-test, ICU's headers and dotnet/runtime.
# Run it with --init, so that a process group the tool stops is reaped.
FROM mcr.microsoft.com/dotnet/sdk:10.0.300-noble@sha256:c0790639332692a0d56cdd81ed581cfd24d040d9839764c138994866df89a3b6

RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        bison bzip2 cmake flex g++ gcc git libglib2.0-dev libicu-dev libkrb5-dev liblttng-ust-dev libpixman-1-dev \
        libssl-dev libxml2 make ninja-build pkg-config python3 python3-venv rsync xz-utils zlib1g-dev \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /work

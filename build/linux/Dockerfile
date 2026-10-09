# The Linux development host of WitOS (plan step T2.1a): the .NET SDK of global.json on Ubuntu 24.04, by digest, what
# building the pinned QEMU needs, and libxml2, which the official LLVM release's lld loads. WitOS.Dev's setup pins and
# verifies every other tool: LLVM, QEMU's source, musl, compiler-rt, the LLVM runtimes and libc-test. Run it with
# --init, so that a process group the tool stops is reaped.
FROM mcr.microsoft.com/dotnet/sdk:10.0.300-noble@sha256:c0790639332692a0d56cdd81ed581cfd24d040d9839764c138994866df89a3b6

RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        bison bzip2 flex g++ gcc git libglib2.0-dev libpixman-1-dev libxml2 make ninja-build pkg-config python3 \
        python3-venv rsync xz-utils \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /work

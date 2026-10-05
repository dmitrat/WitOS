# Microsoft C++ Standard Library notice

The build downloads the headers of `stl/inc`, the separately compiled sources the host needs from `stl/src`, `LICENSE.txt` and `NOTICE.txt` from [microsoft/STL vs-2022-17.14](https://github.com/microsoft/STL/tree/1f6e5b16ec02216665624c1e762f3732605cf2b4) into `.tools/stl/<commit>` and verifies their canonical bytes against `stl.lock.json`. No STL file is stored in this repository or changed by the build; guest images contain only object code compiled from the unchanged sources.

Microsoft C++ Standard Library

Copyright (c) Microsoft Corporation

Licensed under the Apache License, Version 2.0 with LLVM Exceptions. The complete license, `LICENSE.txt`, and the notices of the files that carry their own, `NOTICE.txt`, are among the verified downloaded files.

`stl/internal_shared.h` is WitOS's own replacement for the toolset's closed header of the same name, which the STL's sources include; nothing in it is copied from the toolset.

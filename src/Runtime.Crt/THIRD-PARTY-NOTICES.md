# OpenLibm notice

The build downloads the C sources and headers of the UCRT subset's mathematics and `LICENSE.md` from
[OpenLibm v0.8.7](https://github.com/JuliaMath/openlibm/tree/9fbeafcd4f1b6ef6aa3946c1c8faead50f38a94d) into
`.tools/math-audit/<commit>` and verifies their canonical bytes against `openlibm.lock.json`. No OpenLibm file is
stored in this repository. The build copies the files into `artifacts/openlibm/<commit>`, `LICENSE.md` among them,
changing five of them through the patches in `patches/openlibm`, which alter compiler and linkage glue only. Guest
images contain object code compiled from those files.

OpenLibm derives from FreeBSD msun and OpenBSD libm, which derive from FDLIBM 5.3. Its `LICENSE.md` covers the parts
under the MIT License (Copyright (c) 2011-14 The Julia Project), the ISC License (Copyright (c) 2008 Stephen L.
Moshier), the FreeBSD 2-clause BSD License (Copyright 1992-2011 The FreeBSD Project) and the FDLIBM notice:

Copyright (C) 1993 by Sun Microsystems, Inc. All rights reserved.

Developed at SunSoft, a Sun Microsystems, Inc. business.
Permission to use, copy, modify, and distribute this
software is freely granted, provided that this notice
is preserved.

The complete license texts are in the verified `LICENSE.md`. No OpenLibm test source, the only LGPL part, is used.
`openlibm_names.witos.h` and `math.cpp` are WitOS's own.

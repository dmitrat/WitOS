# Native math source notice

The build downloads `src/e_log.c` from [OpenLibm v0.8.7](https://github.com/JuliaMath/openlibm/tree/9fbeafcd4f1b6ef6aa3946c1c8faead50f38a94d), verifies canonical bytes against `math.lock.json`, and preserves this notice in the generated source. The complete upstream LICENSE.md is verified and copied beside the generated file under artifacts/runtime-config/source/.

Copyright (C) 1993 by Sun Microsystems, Inc. All rights reserved.

Developed at SunSoft, a Sun Microsystems, Inc. business.
Permission to use, copy, modify, and distribute this
software is freely granted, provided that this notice
is preserved.

WitOS changes only compiler/header glue and the optional long-double alias; native_math.witos.cpp supplies the native errno wrapper. The original numerical algorithm and constants remain unchanged. No OpenLibm LGPL test sources are used; WitOS test vectors are independently calculated from exact binary64 inputs at 100 decimal digits.

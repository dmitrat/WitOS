#pragma once
/* The trace of tests/User.X64/cxx_exceptions.cpp in its Itanium form (plan step S4), as Linux prints it: the
 * itanium-reference job of CI builds tests/User/cxx_main.cpp natively on Linux with the host's clang and C++ library
 * and requires this trace, and the cxx scenario requires the same in the guest on both ISAs. It differs from
 * vcruntime's trace (NativeCxxExceptionImage.WINDOWS_TRACE) where the ABIs differ: an Itanium catch parameter is
 * built in the landing pad, after the frames in between unwound (S17: -:171 -:170 copy:172), while vcruntime builds
 * it in the search phase, before they unwind. */
#define WIT_CXX_ITANIUM_TRACE \
    "S1:7 S2:5 drop:5 copy:6 S3:6 drop:6 drop:6 S4:1 +:1 +:2 +:3 -:3 -:2 -:1 S5:30 +:61 -:61 S6:60 " \
    "drop:60 S7:71 drop:71 +:81 -:81 drop:80 S8:82 drop:82 S9:22 S9adjust:4 S10:100 drop:100 S11ok:0 " \
    "S11:111 S11ok:2 S11:113 S12:121 drop:121 S12after:120 unwinding:1 S13:130 S13caught:0 copy:140 " \
    "S14:140 drop:140 drop:140 +:151 -:151 S15:150 drop:150 S16:160 drop:160 +:170 +:171 -:171 -:170 " \
    "copy:172 S17:172 drop:172 drop:172 +:182 -:182 drop:181 S18:183 S19:190 S19right:382 S20:603 " \
    "S21inner:0 S21:210 drop:210 live:0"

#ifndef WITOS_NATIVE_CTYPE_H
#define WITOS_NATIVE_CTYPE_H

/* The CT_CTYPE1 classes of U+0000-U+00FF (P6.4.i3b), as Windows' GetStringTypeW reports them from Unicode's
 * categories: control characters with the space and blank classes of the white-space ones, ASCII and Latin-1
 * punctuation, digits and letters, the superscript digits as digits and punctuation, the feminine and masculine
 * ordinals and the micro sign as lowercase letters and punctuation, and the soft hyphen as a control character and
 * punctuation. StlTests checks every value against GetStringTypeW. The guest has no Unicode character database, so
 * characters beyond U+00FF have no class here (0xFFFF). */
static inline unsigned short wit_native_ctype1(unsigned value)
{
    static const struct {
        unsigned short Last, Type;
    } ranges[] = {{0x08, 0x220}, {0x09, 0x268}, {0x0D, 0x228}, {0x1F, 0x220}, {0x20, 0x248}, {0x2F, 0x210},
        {0x39, 0x284}, {0x40, 0x210}, {0x46, 0x381}, {0x5A, 0x301}, {0x60, 0x210}, {0x66, 0x382}, {0x7A, 0x302},
        {0x7E, 0x210}, {0x84, 0x220}, {0x85, 0x228}, {0x9F, 0x220}, {0xA0, 0x248}, {0xA9, 0x210}, {0xAA, 0x312},
        {0xAC, 0x210}, {0xAD, 0x230}, {0xB1, 0x210}, {0xB3, 0x214}, {0xB4, 0x210}, {0xB5, 0x312}, {0xB8, 0x210},
        {0xB9, 0x214}, {0xBA, 0x312}, {0xBF, 0x210}, {0xD6, 0x301}, {0xD7, 0x210}, {0xDE, 0x301}, {0xF6, 0x302},
        {0xF7, 0x210}, {0xFF, 0x302}};

    for (unsigned i = 0; i < sizeof(ranges) / sizeof(ranges[0]); ++i) {
        if (value <= ranges[i].Last) {
            return ranges[i].Type;
        }
    }
    return 0xFFFF;
}

#endif

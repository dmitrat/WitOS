#include "format_fixed.witos.h"
#include <stdint.h>

namespace {
// max binary64 * 10^64 needs fewer than 1240 bits. No floating arithmetic,
// allocation, TLS, errno, or external math routines participate in rounding.
struct Integer {
    uint32_t words[40] = {};
    bool bit(unsigned n) const { return n < 1280 && ((words[n / 32] >> (n % 32)) & 1); }
    bool below(unsigned n) const {
        for (unsigned i = 0; i < 40; ++i) {
            if (n >= 32) { if (words[i]) return true; n -= 32; }
            else return n && (words[i] & ((uint32_t(1) << n) - 1));
        }
        return false;
    }
    void times5() {
        uint64_t carry = 0;
        for (auto& w : words) { const uint64_t v = uint64_t(w) * 5 + carry; w = uint32_t(v); carry = v >> 32; }
    }
    void left(unsigned n) {
        const unsigned word = n / 32, bit = n % 32;
        for (unsigned i = 40; i-- > 0;) {
            uint32_t v = i >= word ? words[i - word] << bit : 0;
            if (bit && i > word) v |= words[i - word - 1] >> (32 - bit);
            words[i] = v;
        }
    }
    void right(unsigned n) {
        const unsigned word = n / 32, bit = n % 32;
        for (unsigned i = 0; i < 40; ++i) {
            uint32_t v = i + word < 40 ? words[i + word] >> bit : 0;
            if (bit && i + word + 1 < 40) v |= words[i + word + 1] << (32 - bit);
            words[i] = v;
        }
    }
    void increment() { for (auto& w : words) if (++w) break; }
    unsigned divide10() {
        uint64_t carry = 0;
        for (unsigned i = 40; i-- > 0;) { const uint64_t v = (carry << 32) | words[i]; words[i] = uint32_t(v / 10); carry = v % 10; }
        return unsigned(carry);
    }
    bool any() const { for (auto w : words) if (w) return true; return false; }
};
}
extern "C" int wit_format_fixed(char* output, double value, unsigned precision, unsigned rounding)
{
    if (!output || precision > WIT_FORMAT_FIXED_PRECISION || rounding > 4) return -1;
    union { double value; uint64_t bits; } input = { value };
    const bool negative = (input.bits >> 63) != 0;
    const unsigned exponent = unsigned((input.bits >> 52) & 2047);
    const uint64_t fraction = input.bits & UINT64_C(0xfffffffffffff);
    unsigned length = 0;
    if (negative) output[length++] = '-';
    if (exponent == 2047) {
        const char* text = !fraction ? "inf" : !(fraction & (1ULL << 51)) ? "nan(snan)" :
            (negative && fraction == (1ULL << 51)) ? "nan(ind)" : "nan";
        while (*text) output[length++] = *text++;
        output[length] = 0;
        return int(length);
    }
    const uint64_t mantissa = fraction | (exponent ? (1ULL << 52) : 0);
    const int binary = exponent ? int(exponent) - 1023 - 52 : -1074;
    Integer integer;
    integer.words[0] = uint32_t(mantissa); integer.words[1] = uint32_t(mantissa >> 32);
    for (unsigned i = 0; i < precision; ++i) integer.times5();
    const int shift = binary + int(precision);
    if (shift >= 0) integer.left(unsigned(shift));
    else {
        const unsigned discarded = unsigned(-shift);
        const bool half = integer.bit(discarded - 1), low = integer.below(discarded - 1);
        const bool lost = half || low;
        const bool up = rounding == 4 ? half : rounding == 0 ? (half && (low || integer.bit(discarded))) :
            rounding == 1 ? negative && lost : rounding == 2 ? !negative && lost : false;
        integer.right(discarded);
        if (up) integer.increment();
    }
    char reverse[WIT_FORMAT_FIXED_CAPACITY];
    unsigned digits = 0;
    do { reverse[digits++] = char('0' + integer.divide10()); } while (integer.any());
    if (digits <= precision) {
        output[length++] = '0';
        if (precision) output[length++] = '.';
        for (unsigned i = digits; i < precision; ++i) output[length++] = '0';
        while (digits) output[length++] = reverse[--digits];
    } else {
        for (unsigned i = digits; i > precision; --i) output[length++] = reverse[i - 1];
        if (precision) output[length++] = '.';
        for (unsigned i = precision; i > 0; --i) output[length++] = reverse[i - 1];
    }
    output[length] = 0;
    return int(length);
}

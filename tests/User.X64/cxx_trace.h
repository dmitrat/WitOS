#pragma once
/* Trace tokens of the C++ runtime scenarios (P6.4.e-g): "label:value" or "label:text" through the harness's
 * cxx_trace. No library is used, so the scenarios observe only the runtime under test. The helpers carry no GS checks
 * (safebuffers): scenario G8 tests those where it means to. */
extern "C" void cxx_trace(const char *text);

namespace CxxTrace {

__declspec(safebuffers) inline void Text(const char *label, const char *value)
{
    char text[64];
    unsigned at = 0;
    while (*label && at < 24) {
        text[at++] = *label++;
    }
    text[at++] = ':';
    while (*value && at < sizeof(text) - 1) {
        text[at++] = *value == ' ' ? '_' : *value;
        ++value;
    }
    text[at] = 0;
    cxx_trace(text);
}

__declspec(safebuffers) inline void Number(const char *label, long long value)
{
    char digits[24];
    unsigned at = 0;
    unsigned long long magnitude = value < 0 ? 0ULL - (unsigned long long)value : (unsigned long long)value;
    if (value < 0) {
        digits[at++] = '-';
    }
    char reversed[20];
    unsigned count = 0;
    do {
        reversed[count++] = (char)('0' + magnitude % 10);
        magnitude /= 10;
    } while (magnitude);
    while (count) {
        digits[at++] = reversed[--count];
    }
    digits[at] = 0;
    Text(label, digits);
}

} // namespace CxxTrace

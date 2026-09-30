#ifndef WITOS_FORMAT_FIXED_H
#define WITOS_FORMAT_FIXED_H
#include <stddef.h>
#define WIT_FORMAT_FIXED_CAPACITY 400U
#define WIT_FORMAT_FIXED_PRECISION 64U
#ifdef __cplusplus
extern "C" {
#endif
/* Private binary64 fixed-decimal conversion. Buffer has at least CAPACITY bytes.
 * rounding: 0 nearest/even, 1 downward, 2 upward, 3 toward zero, 4 legacy nearest/away ties.
 * Returns character count or -1 for unsupported precision/rounding. */
int wit_format_fixed(char* output, double value, unsigned precision, unsigned rounding);
#ifdef __cplusplus
}
#endif
#endif

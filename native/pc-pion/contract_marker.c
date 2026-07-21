// SPDX-License-Identifier: LGPL-2.1-only

#if defined(_WIN32)
#define PC_PION_EXPORT __declspec(dllexport)
#else
#define PC_PION_EXPORT __attribute__((visibility("default")))
#endif

#if defined(__GNUC__) || defined(__clang__)
#define PC_PION_USED __attribute__((used))
#else
#define PC_PION_USED
#endif

// Release verification reads this exported object without executing the shared library.
// Keep the text synchronized with pionABIVersion/pionVersion in the Go ABI and with
// scripts/verify-release-assets.py. The terminating NUL is part of the marker contract.
PC_PION_EXPORT PC_PION_USED const char PC_PION_CONTRACT_MARKER[] =
    "PERFECTCOMMS_PC_PION_ABI=2;PION=4.2.17";

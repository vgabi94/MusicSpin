/* MusicSpin's build configuration for the vendored libsamplerate 0.2.2 (replaces its CMake/autotools config.h).
   Only the best sinc converter is compiled in; the fast and medium coefficient tables are left out. */
#ifndef MSA_LIBSAMPLERATE_CONFIG_H
#define MSA_LIBSAMPLERATE_CONFIG_H
#define PACKAGE "libsamplerate"
#define VERSION "0.2.2"
#define HAVE_STDBOOL_H 1
#define ENABLE_SINC_BEST_CONVERTER 1
#endif

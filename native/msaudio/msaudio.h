/*
 * msaudio: MusicSpin's thin, flat C layer over miniaudio's low-level device API (no engine, no mixer, no gain).
 * Every struct here is plain fixed-size data so the C# side can marshal it blittably; msa_layout() reports the
 * compiled sizes and offsets so tests can check the C# declarations against them.
 */
#ifndef MSAUDIO_H
#define MSAUDIO_H

#include <stdint.h>

#if defined(_WIN32)
#define MSA_API __declspec(dllexport)
#else
#define MSA_API __attribute__((visibility("default")))
#endif

#define MSA_ABI_VERSION 3
#define MSA_ID_BYTES 256
#define MSA_NAME_BYTES 256
#define MSA_MAX_NATIVE_FORMATS 64

/* Values match miniaudio's ma_format. */
enum { MSA_FORMAT_UNKNOWN = 0, MSA_FORMAT_U8 = 1, MSA_FORMAT_S16 = 2, MSA_FORMAT_S24 = 3, MSA_FORMAT_S32 = 4, MSA_FORMAT_F32 = 5 };

/* Notifications passed to the managed side (values match ma_device_notification_type). */
enum { MSA_NOTIFY_STARTED = 0, MSA_NOTIFY_STOPPED = 1, MSA_NOTIFY_REROUTED = 2, MSA_NOTIFY_INTERRUPTION_BEGAN = 3, MSA_NOTIFY_INTERRUPTION_ENDED = 4, MSA_NOTIFY_UNLOCKED = 5 };

typedef struct msa_native_format {
    int32_t format;
    int32_t channels;
    int32_t sample_rate;
    int32_t flags;
} msa_native_format;

typedef struct msa_device_info {
    uint8_t id[MSA_ID_BYTES];       /* opaque ma_device_id; on WASAPI it starts with the UTF-16 endpoint id */
    char name[MSA_NAME_BYTES];      /* UTF-8, NUL-terminated */
    int32_t is_default;
    int32_t native_format_count;
    msa_native_format native_formats[MSA_MAX_NATIVE_FORMATS];
} msa_device_info;

typedef struct msa_open_params {
    int32_t format;                 /* the format our callback writes (MSA_FORMAT_*) */
    int32_t channels;
    int32_t sample_rate;
    int32_t exclusive;              /* 1: ask for exclusive access and no OS conversion */
    int32_t period_frames;          /* 0: backend default */
    int32_t has_device_id;          /* 0: default device */
    uint8_t device_id[MSA_ID_BYTES];
} msa_open_params;

/* What the device really opened at: miniaudio's internal (backend side) format, read back after ma_device_init. */
typedef struct msa_opened {
    int32_t format;
    int32_t channels;
    int32_t sample_rate;
    int32_t exclusive;
    int32_t period_frames;
    char name[MSA_NAME_BYTES];
} msa_opened;

typedef void (*msa_data_callback)(void* user, void* output, uint32_t frames);
typedef void (*msa_notify_callback)(void* user, int32_t type);

typedef struct msa_context msa_context;
typedef struct msa_device msa_device;
typedef struct msa_converter msa_converter;

#ifdef __cplusplus
extern "C" {
#endif

MSA_API int32_t msa_abi_version(void);
/* which: 0 sizes (field: 0 native_format, 1 device_info, 2 open_params, 3 opened); 1..4 offsets of the n-th field. */
MSA_API int32_t msa_layout(int32_t which, int32_t field);
MSA_API const char* msa_result_description(int32_t result);

/* null_backend: 1 = miniaudio's Null backend only (silent, for tests); 0 = the platform's backends. */
MSA_API int32_t msa_context_init(int32_t null_backend, msa_context** context);
MSA_API void msa_context_uninit(msa_context* context);
MSA_API const char* msa_context_backend(msa_context* context);
/* Re-enumerates playback devices; then msa_device_info_at reads them. */
MSA_API int32_t msa_context_refresh(msa_context* context, int32_t* count);
MSA_API int32_t msa_device_info_at(msa_context* context, int32_t index, msa_device_info* info);

MSA_API int32_t msa_device_open(msa_context* context, const msa_open_params* params, msa_data_callback data,
                                msa_notify_callback notify, void* user, msa_device** device, msa_opened* opened);
MSA_API int32_t msa_device_start(msa_device* device);
MSA_API int32_t msa_device_stop(msa_device* device);
MSA_API void msa_device_close(msa_device* device);
/* Reads what the device runs at now (after miniaudio rerouted a default device, the new endpoint's format and name). */
MSA_API int32_t msa_device_opened(msa_device* device, msa_opened* opened);
/* Tests: runs miniaudio's full playback path (our callback plus its client-to-device conversion) into output. */
MSA_API int32_t msa_device_render(msa_device* device, void* output, uint32_t frames);

/* Interleaved float32 sample-rate and channel conversion (miniaudio's data converter), for a shared-mode device that
   stays open at one format while tracks of another play. */
MSA_API int32_t msa_converter_init(int32_t in_channels, int32_t in_rate, int32_t out_channels, int32_t out_rate,
                                   msa_converter** converter);
MSA_API void msa_converter_uninit(msa_converter* converter);
/* Consumes up to *in_frames from input and writes up to *out_frames to output; both are updated to what was used. */
MSA_API int32_t msa_converter_process(msa_converter* converter, const float* input, uint64_t* in_frames, float* output,
                                      uint64_t* out_frames);
/* Forgets filter history, for a seek. */
MSA_API int32_t msa_converter_reset(msa_converter* converter);

#ifdef __cplusplus
}
#endif

#endif

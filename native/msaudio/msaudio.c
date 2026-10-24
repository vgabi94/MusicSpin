#define _CRT_SECURE_NO_WARNINGS
#define MINIAUDIO_IMPLEMENTATION
#define MA_NO_DECODING
#define MA_NO_ENCODING
#define MA_NO_GENERATION
#define MA_NO_RESOURCE_MANAGER
#define MA_NO_NODE_GRAPH
#define MA_NO_ENGINE
#include "miniaudio/miniaudio.h"
#include "msaudio.h"
#include "libsamplerate/samplerate.h"

#include <stddef.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

typedef char msa_id_fits[(sizeof(ma_device_id) <= MSA_ID_BYTES) ? 1 : -1];

struct msa_context {
    ma_context ma;
    ma_device_info* playback;
    ma_uint32 playback_count;
};

struct msa_device {
    ma_device ma;
    msa_data_callback data;
    msa_notify_callback notify;
    void* user;
    /* What the device runs at, as of open and of the latest reroute. The device thread rewrites ma.playback.* while it
       reroutes, so other threads read this copy (under the mutex) instead of those fields. */
    ma_mutex lock;
    msa_opened snapshot;
    int has_snapshot;
};

MSA_API int32_t msa_abi_version(void) { return MSA_ABI_VERSION; }

MSA_API int32_t msa_layout(int32_t which, int32_t field)
{
    switch (which) {
    case 0:
        switch (field) {
        case 0: return (int32_t)sizeof(msa_native_format);
        case 1: return (int32_t)sizeof(msa_device_info);
        case 2: return (int32_t)sizeof(msa_open_params);
        case 3: return (int32_t)sizeof(msa_opened);
        }
        break;
    case 1: /* msa_native_format */
        switch (field) {
        case 0: return (int32_t)offsetof(msa_native_format, format);
        case 1: return (int32_t)offsetof(msa_native_format, channels);
        case 2: return (int32_t)offsetof(msa_native_format, sample_rate);
        case 3: return (int32_t)offsetof(msa_native_format, flags);
        }
        break;
    case 2: /* msa_device_info */
        switch (field) {
        case 0: return (int32_t)offsetof(msa_device_info, id);
        case 1: return (int32_t)offsetof(msa_device_info, name);
        case 2: return (int32_t)offsetof(msa_device_info, is_default);
        case 3: return (int32_t)offsetof(msa_device_info, native_format_count);
        case 4: return (int32_t)offsetof(msa_device_info, native_formats);
        }
        break;
    case 3: /* msa_open_params */
        switch (field) {
        case 0: return (int32_t)offsetof(msa_open_params, format);
        case 1: return (int32_t)offsetof(msa_open_params, channels);
        case 2: return (int32_t)offsetof(msa_open_params, sample_rate);
        case 3: return (int32_t)offsetof(msa_open_params, exclusive);
        case 4: return (int32_t)offsetof(msa_open_params, period_frames);
        case 5: return (int32_t)offsetof(msa_open_params, has_device_id);
        case 6: return (int32_t)offsetof(msa_open_params, device_id);
        }
        break;
    case 4: /* msa_opened */
        switch (field) {
        case 0: return (int32_t)offsetof(msa_opened, format);
        case 1: return (int32_t)offsetof(msa_opened, channels);
        case 2: return (int32_t)offsetof(msa_opened, sample_rate);
        case 3: return (int32_t)offsetof(msa_opened, exclusive);
        case 4: return (int32_t)offsetof(msa_opened, period_frames);
        case 5: return (int32_t)offsetof(msa_opened, name);
        case 6: return (int32_t)offsetof(msa_opened, client_sample_rate);
        }
        break;
    }
    return -1;
}

MSA_API const char* msa_result_description(int32_t result) { return ma_result_description((ma_result)result); }

MSA_API int32_t msa_context_init(int32_t null_backend, msa_context** context)
{
    ma_backend nullOnly[1] = { ma_backend_null };
    ma_context_config config = ma_context_config_init();
    msa_context* c;
    ma_result result;

    if (context == NULL) return MA_INVALID_ARGS;
    *context = NULL;
    c = (msa_context*)calloc(1, sizeof(*c));
    if (c == NULL) return MA_OUT_OF_MEMORY;

    result = null_backend ? ma_context_init(nullOnly, 1, &config, &c->ma) : ma_context_init(NULL, 0, &config, &c->ma);
    if (result != MA_SUCCESS) {
        free(c);
        return result;
    }

    *context = c;
    return MA_SUCCESS;
}

MSA_API void msa_context_uninit(msa_context* context)
{
    if (context == NULL) return;
    ma_context_uninit(&context->ma);
    free(context);
}

MSA_API const char* msa_context_backend(msa_context* context)
{
    return context == NULL ? "" : ma_get_backend_name(context->ma.backend);
}

MSA_API int32_t msa_context_refresh(msa_context* context, int32_t* count)
{
    ma_result result;
    if (context == NULL || count == NULL) return MA_INVALID_ARGS;
    result = ma_context_get_devices(&context->ma, &context->playback, &context->playback_count, NULL, NULL);
    *count = result == MA_SUCCESS ? (int32_t)context->playback_count : 0;
    return result;
}

MSA_API int32_t msa_device_info_at(msa_context* context, int32_t index, msa_device_info* info)
{
    ma_device_info detail;
    ma_uint32 i;
    if (context == NULL || info == NULL || index < 0 || (ma_uint32)index >= context->playback_count) return MA_INVALID_ARGS;

    memset(info, 0, sizeof(*info));
    memcpy(info->id, &context->playback[index].id, sizeof(ma_device_id));
    snprintf(info->name, MSA_NAME_BYTES, "%s", context->playback[index].name);
    info->is_default = context->playback[index].isDefault ? 1 : 0;

    /* The native formats need the detailed query; a device that refuses it still lists, with no formats. */
    if (ma_context_get_device_info(&context->ma, ma_device_type_playback, &context->playback[index].id, &detail) == MA_SUCCESS) {
        for (i = 0; i < detail.nativeDataFormatCount && i < MSA_MAX_NATIVE_FORMATS; i++) {
            info->native_formats[i].format = (int32_t)detail.nativeDataFormats[i].format;
            info->native_formats[i].channels = (int32_t)detail.nativeDataFormats[i].channels;
            info->native_formats[i].sample_rate = (int32_t)detail.nativeDataFormats[i].sampleRate;
            info->native_formats[i].flags = (int32_t)detail.nativeDataFormats[i].flags;
        }
        info->native_format_count = (int32_t)i;
    }

    return MA_SUCCESS;
}

/* Reads the live device fields: only from the thread that changes them (a notification callback) or before the device
   is shared. */
static void msa_read_live(msa_device* d, msa_opened* opened)
{
    memset(opened, 0, sizeof(*opened));
    opened->format = (int32_t)d->ma.playback.internalFormat;
    opened->channels = (int32_t)d->ma.playback.internalChannels;
    opened->sample_rate = (int32_t)d->ma.playback.internalSampleRate;
    opened->exclusive = d->ma.playback.shareMode == ma_share_mode_exclusive ? 1 : 0;
    opened->period_frames = (int32_t)d->ma.playback.internalPeriodSizeInFrames;
    snprintf(opened->name, MSA_NAME_BYTES, "%s", d->ma.playback.name);
    opened->client_sample_rate = (int32_t)d->ma.sampleRate;
}

static void msa_snapshot(msa_device* d, int overwrite)
{
    msa_opened now;
    msa_read_live(d, &now);
    ma_mutex_lock(&d->lock);
    if (overwrite || !d->has_snapshot) {
        d->snapshot = now;
        d->has_snapshot = 1;
    }
    ma_mutex_unlock(&d->lock);
}

static void msa_read_opened(msa_device* d, msa_opened* opened)
{
    ma_mutex_lock(&d->lock);
    *opened = d->snapshot;
    ma_mutex_unlock(&d->lock);
}

static void msa_on_data(ma_device* device, void* output, const void* input, ma_uint32 frames)
{
    msa_device* d = (msa_device*)device->pUserData;
    (void)input;
    d->data(d->user, output, frames);
}

static void msa_on_notification(const ma_device_notification* notification)
{
    msa_device* d = (msa_device*)notification->pDevice->pUserData;
    /* The device thread has finished rewriting its format for the new device: keep a copy others can read. */
    if (notification->type == ma_device_notification_type_rerouted) msa_snapshot(d, 1);
    if (d->notify != NULL) d->notify(d->user, (int32_t)notification->type);
}

MSA_API int32_t msa_device_open(msa_context* context, const msa_open_params* params, msa_data_callback data,
                                msa_notify_callback notify, void* user, msa_device** device, msa_opened* opened)
{
    ma_device_config config;
    ma_device_id id;
    msa_device* d;
    ma_result result;

    if (device != NULL) *device = NULL;
    if (context == NULL || params == NULL || data == NULL || device == NULL || opened == NULL) return MA_INVALID_ARGS;
    if (params->format < MSA_FORMAT_U8 || params->format > MSA_FORMAT_F32 || params->channels < 1
        || params->channels > MA_MAX_CHANNELS || params->sample_rate < 0 || (params->sample_rate > 0 && params->sample_rate < 8000)
        || params->sample_rate > 768000 || (params->sample_rate == 0 && params->exclusive)) {
        return MA_INVALID_ARGS;
    }

    d = (msa_device*)calloc(1, sizeof(*d));
    if (d == NULL) return MA_OUT_OF_MEMORY;
    d->data = data;
    d->notify = notify;
    d->user = user;
    result = ma_mutex_init(&d->lock);
    if (result != MA_SUCCESS) {
        free(d);
        return result;
    }

    config = ma_device_config_init(ma_device_type_playback);
    config.playback.format = (ma_format)params->format;
    config.playback.channels = (ma_uint32)params->channels;
    config.sampleRate = (ma_uint32)params->sample_rate;
    config.playback.shareMode = params->exclusive ? ma_share_mode_exclusive : ma_share_mode_shared;
    config.periodSizeInFrames = params->period_frames > 0 ? (ma_uint32)params->period_frames : 0;
    config.dataCallback = msa_on_data;
    config.notificationCallback = msa_on_notification;
    config.pUserData = d;
    /* Our callback always writes every frame; never let miniaudio clip, pre-silence or adjust what we give it. */
    config.noPreSilencedOutputBuffer = MA_TRUE;
    config.noClip = MA_TRUE;
    config.noFixedSizedCallback = MA_TRUE;
    /* When miniaudio must resample itself (a rerouted device at another rate), its best: linear at the steepest filter. */
    config.resampling.algorithm = ma_resample_algorithm_linear;
    config.resampling.linear.lpfOrder = MA_MAX_FILTER_ORDER;
    if (params->sample_rate == 0) {
        /* The device's own rate. WASAPI would otherwise ask Windows to convert to 48 kHz (AUTOCONVERTPCM) instead of
           taking the mix format's rate. */
        config.wasapi.noAutoConvertSRC = MA_TRUE;
    }
    if (params->exclusive) {
        config.wasapi.noAutoConvertSRC = MA_TRUE;
        config.wasapi.noDefaultQualitySRC = MA_TRUE;
        config.alsa.noAutoFormat = MA_TRUE;
        config.alsa.noAutoChannels = MA_TRUE;
        config.alsa.noAutoResample = MA_TRUE;
        config.coreaudio.allowNominalSampleRateChange = MA_TRUE;
    }
    if (params->has_device_id) {
        memcpy(&id, params->device_id, sizeof(ma_device_id));
        config.playback.pDeviceID = &id;
    }

    result = ma_device_init(&context->ma, &config, &d->ma);
    if (result != MA_SUCCESS) {
        ma_mutex_uninit(&d->lock);
        free(d);
        return result;
    }

    msa_snapshot(d, 0); /* a reroute during init already took a newer one */
    msa_read_opened(d, opened);
    *device = d;
    return MA_SUCCESS;
}

MSA_API int32_t msa_device_start(msa_device* device) { return device == NULL ? MA_INVALID_ARGS : ma_device_start(&device->ma); }

MSA_API int32_t msa_device_stop(msa_device* device) { return device == NULL ? MA_INVALID_ARGS : ma_device_stop(&device->ma); }

MSA_API void msa_device_close(msa_device* device)
{
    if (device == NULL) return;
    ma_device_uninit(&device->ma);
    ma_mutex_uninit(&device->lock);
    free(device);
}

MSA_API int32_t msa_device_opened(msa_device* device, msa_opened* opened)
{
    if (device == NULL || opened == NULL) return MA_INVALID_ARGS;
    msa_read_opened(device, opened);
    return MA_SUCCESS;
}

MSA_API int32_t msa_device_render(msa_device* device, void* output, uint32_t frames)
{
    if (device == NULL || output == NULL) return MA_INVALID_ARGS;
    return ma_device_handle_backend_data_callback(&device->ma, output, NULL, frames);
}

/* Channels first (miniaudio's channel converter, frame for frame), then the rate (libsamplerate's best sinc) at the
   output's channel count. Input is staged in chunks; what the filter did not take is reported back as unconsumed. */
#define MSA_CONVERTER_CHUNK 1024

struct msa_converter {
    int32_t in_channels, out_channels, in_rate, out_rate;
    int channel_convert;
    ma_channel_converter channels;
    SRC_STATE* src;                 /* NULL: same rate */
    float* staged;                  /* channel-converted input, MSA_CONVERTER_CHUNK frames of out_channels */
    uint64_t staged_frames;         /* frames waiting in staged */
    uint64_t staged_at;
};

MSA_API int32_t msa_converter_init(int32_t in_channels, int32_t in_rate, int32_t out_channels, int32_t out_rate,
                                   msa_converter** converter)
{
    msa_converter* c;
    int error = 0;
    if (converter == NULL) return MA_INVALID_ARGS;
    *converter = NULL;
    if (in_channels <= 0 || out_channels <= 0 || in_channels > MA_MAX_CHANNELS || out_channels > MA_MAX_CHANNELS
        || in_rate <= 0 || out_rate <= 0 || (double)out_rate / in_rate > 256.0 || (double)in_rate / out_rate > 256.0) {
        return MA_INVALID_ARGS; /* libsamplerate's ratio range */
    }

    c = (msa_converter*)calloc(1, sizeof(*c));
    if (c == NULL) return MA_OUT_OF_MEMORY;
    c->in_channels = in_channels;
    c->out_channels = out_channels;
    c->in_rate = in_rate;
    c->out_rate = out_rate;
    c->staged = (float*)calloc((size_t)MSA_CONVERTER_CHUNK * (size_t)out_channels, sizeof(float));
    if (c->staged == NULL) {
        free(c);
        return MA_OUT_OF_MEMORY;
    }

    if (in_channels != out_channels) {
        ma_channel_converter_config cc = ma_channel_converter_config_init(ma_format_f32, (ma_uint32)in_channels, NULL,
            (ma_uint32)out_channels, NULL, ma_channel_mix_mode_default);
        if (ma_channel_converter_init(&cc, NULL, &c->channels) != MA_SUCCESS) {
            free(c->staged);
            free(c);
            return MA_INVALID_ARGS;
        }
        c->channel_convert = 1;
    }

    if (in_rate != out_rate) {
        c->src = src_new(SRC_SINC_BEST_QUALITY, out_channels, &error);
        if (c->src == NULL) {
            if (c->channel_convert) ma_channel_converter_uninit(&c->channels, NULL);
            free(c->staged);
            free(c);
            return MA_OUT_OF_MEMORY;
        }
    }

    *converter = c;
    return MA_SUCCESS;
}

MSA_API void msa_converter_uninit(msa_converter* converter)
{
    if (converter == NULL) return;
    if (converter->src != NULL) src_delete(converter->src);
    if (converter->channel_convert) ma_channel_converter_uninit(&converter->channels, NULL);
    free(converter->staged);
    free(converter);
}

/* Moves up to n frames of input into out (out_channels wide). */
static void msa_converter_channels(msa_converter* c, const float* input, float* out, uint64_t n)
{
    if (c->channel_convert) {
        ma_channel_converter_process_pcm_frames(&c->channels, out, input, n);
    } else {
        memcpy(out, input, (size_t)n * (size_t)c->out_channels * sizeof(float));
    }
}

MSA_API int32_t msa_converter_process(msa_converter* converter, const float* input, uint64_t* in_frames, float* output,
                                      uint64_t* out_frames, int32_t end_of_input)
{
    msa_converter* c = converter;
    uint64_t in_total, out_total, consumed = 0, produced = 0;
    if (c == NULL || in_frames == NULL || out_frames == NULL || (*in_frames > 0 && input == NULL)
        || (*out_frames > 0 && output == NULL)) {
        return MA_INVALID_ARGS;
    }

    in_total = *in_frames;
    out_total = *out_frames;
    if (c->src == NULL) {
        /* Same rate: channels only, frame for frame. */
        uint64_t n = in_total < out_total ? in_total : out_total;
        msa_converter_channels(c, input, output, n);
        *in_frames = n;
        *out_frames = n;
        return MA_SUCCESS;
    }

    for (;;) {
        SRC_DATA data;
        int error;
        if (c->staged_at == c->staged_frames && consumed < in_total) {
            uint64_t n = in_total - consumed;
            if (n > MSA_CONVERTER_CHUNK) n = MSA_CONVERTER_CHUNK;
            msa_converter_channels(c, input + consumed * (uint64_t)c->in_channels, c->staged, n);
            c->staged_frames = n;
            c->staged_at = 0;
            consumed += n;
        }

        if (produced == out_total) break;
        memset(&data, 0, sizeof(data));
        data.data_in = c->staged + c->staged_at * (uint64_t)c->out_channels;
        data.input_frames = (long)(c->staged_frames - c->staged_at);
        data.data_out = output + produced * (uint64_t)c->out_channels;
        data.output_frames = (long)((out_total - produced) > 0x10000000u ? 0x10000000u : (out_total - produced));
        data.src_ratio = (double)c->out_rate / (double)c->in_rate;
        /* The end is only final once every input frame has gone into the filter. */
        data.end_of_input = end_of_input && consumed == in_total && data.input_frames == 0 ? 1 : 0;
        if (data.input_frames == 0 && !data.end_of_input) break; /* needs more input */
        error = src_process(c->src, &data);
        if (error != 0) return MA_ERROR;
        c->staged_at += (uint64_t)data.input_frames_used;
        produced += (uint64_t)data.output_frames_gen;
        if (data.output_frames_gen == 0 && data.input_frames_used == 0) break; /* flushed, or output full */
    }

    /* Input staged but not yet in the filter is returned as unconsumed, so the caller's view is exact. */
    consumed -= c->staged_frames - c->staged_at;
    c->staged_frames = c->staged_at = 0;
    *in_frames = consumed;
    *out_frames = produced;
    return MA_SUCCESS;
}

MSA_API int32_t msa_converter_reset(msa_converter* converter)
{
    if (converter == NULL) return MA_INVALID_ARGS;
    converter->staged_frames = converter->staged_at = 0;
    if (converter->src != NULL && src_reset(converter->src) != 0) return MA_ERROR;
    return MA_SUCCESS;
}

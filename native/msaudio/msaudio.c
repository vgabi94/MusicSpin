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

static void msa_read_opened(msa_device* d, msa_opened* opened)
{
    memset(opened, 0, sizeof(*opened));
    opened->format = (int32_t)d->ma.playback.internalFormat;
    opened->channels = (int32_t)d->ma.playback.internalChannels;
    opened->sample_rate = (int32_t)d->ma.playback.internalSampleRate;
    opened->exclusive = d->ma.playback.shareMode == ma_share_mode_exclusive ? 1 : 0;
    opened->period_frames = (int32_t)d->ma.playback.internalPeriodSizeInFrames;
    snprintf(opened->name, MSA_NAME_BYTES, "%s", d->ma.playback.name);
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
        || params->channels > MA_MAX_CHANNELS || params->sample_rate < 8000 || params->sample_rate > 768000) {
        return MA_INVALID_ARGS;
    }

    d = (msa_device*)calloc(1, sizeof(*d));
    if (d == NULL) return MA_OUT_OF_MEMORY;
    d->data = data;
    d->notify = notify;
    d->user = user;

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
        free(d);
        return result;
    }

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

#pragma once
#include "api.h"

#ifdef __cplusplus
extern "C" {
#endif

typedef struct fb_font_details fb_font_details;

enum fb_font_outline {
    FB_FONT_OUTLINE_UNKNOWN = 0,
    FB_FONT_OUTLINE_TRUETYPE,
    FB_FONT_OUTLINE_CFF,
    FB_FONT_OUTLINE_CFF2,
    FB_FONT_OUTLINE_TYPE1,
    FB_FONT_OUTLINE_VECTOR,
    FB_FONT_OUTLINE_BITMAP,
    FB_FONT_OUTLINE_RAW_CFF,
};
enum fb_font_layout_status {
    FB_FONT_LAYOUT_ABSENT = 0,
    FB_FONT_LAYOUT_READ,
    FB_FONT_LAYOUT_UNAVAILABLE, /* Unreadable, invalid, unsupported, or over the budget. */
};
enum fb_font_feature_table {
    FB_FONT_FEATURE_GSUB = 1,
    FB_FONT_FEATURE_GPOS = 2,
};

typedef struct fb_font_details_info {
    /* Borrowed UTF-16, until snapshot close. NULL for missing/unreadable fields. Legal text and URLs
       are plain text. Name fields are limited to 16384 UTF-16 code units each. */
    const wchar_t *path, *family, *full_name, *postscript, *style_name, *version, *manufacturer, *designer,
        *copyright_notice, *license_description, *license_url;
    /* Metrics use DirectWrite design units; ascent/descent are magnitudes. */
    uint32_t face_index, glyph_count, units_per_em, ascent, descent;
    uint32_t weight, stretch, style, simulations, face_type; /* DirectWrite's numeric values. */
    uint32_t outline;                                        /* fb_font_outline */
    uint32_t axis_count, feature_count;
    uint32_t gsub_status, gpos_status; /* fb_font_layout_status */
} fb_font_details_info;

typedef struct fb_font_feature {
    uint32_t tag;    /* Four original ASCII bytes, DirectWrite tag order (first byte in the low bits). */
    uint32_t tables; /* Bitmask of fb_font_feature_table; duplicate tags are merged. */
} fb_font_feature;

/* Opens a read-only snapshot of this exact selection (no family lookup). The selection must live through
   this call only. On failure, *result is NULL. Snapshots can be read concurrently until closed. */
FB_API int32_t fb_font_details_open(const fb_selection* selection, fb_font_details** result);
FB_API void fb_font_details_close(fb_font_details* details);
FB_API int32_t fb_font_details_describe(const fb_font_details* details, fb_font_details_info* result);
/* Same varying axes and instance values as fb_axis_at. */
FB_API int32_t fb_font_details_axis_at(const fb_font_details* details, uint32_t index, fb_axis* result);
/* In ASCII tag order; list membership does not imply availability for every script/language. */
FB_API int32_t fb_font_details_feature_at(const fb_font_details* details, uint32_t index,
                                          fb_font_feature* result);

#ifdef __cplusplus
}
#endif

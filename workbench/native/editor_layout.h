#pragma once
#include "api.h"

#ifdef __cplusplus
extern "C" {
#endif

typedef struct fb_layout fb_layout;
typedef struct fb_feature {
    uint32_t tag, value;
} fb_feature;
typedef struct fb_text_rect {
    float x, y, width, height;
} fb_text_rect;

/* Copies the NUL-terminated text and owns the selected face independently. Text/viewport bounds and
   axis order are as for fb_render; size_dip is 1..256 and scale is 0.5..8. The physical viewport width,
   divided by scale, determines wrapping in DIP.
   Up to 64 distinct printable OpenType tags (DWRITE_MAKE_OPENTYPE_TAG byte order), values 0 or 1.
   Omitted features use normal shaping defaults; explicit features keep their input order.
   On failure, *out is NULL. */
FB_API int32_t __cdecl fb_layout_create(const fb_selection* selection, const wchar_t* text, float size_dip,
                                        float scale, uint32_t width_px, uint32_t height_px,
                                        const float* axes, uint32_t axis_count, const fb_feature* features,
                                        uint32_t feature_count, fb_layout** out);

/* Transparent PBGRA32 with premultiplied black ink; valid on success. Draw is draw_at with offset 0.
   The vertical offset must be finite and nonnegative. At or past the extent, the viewport is blank. */
FB_API int32_t __cdecl fb_layout_draw(const fb_layout* layout, uint8_t* pixels, uint32_t bytes);
FB_API int32_t __cdecl fb_layout_draw_at(const fb_layout* layout, float scroll_y_dip, uint8_t* pixels,
                                         uint32_t bytes);
/* Full document height in DIP, including the caret line after a final newline or for empty text. */
FB_API int32_t __cdecl fb_layout_extent(const fb_layout* layout, float* total_height_dip);

/* All geometry is in document DIP, before scroll. Positions are UTF-16 offsets, including the end.
   Hit returns the nearest insertion at a cluster edge. Caret snaps interior positions to the
   cluster's leading edge and uses the downstream position at a soft wrap. Caret width is 1 DIP. */
FB_API int32_t __cdecl fb_layout_hit(const fb_layout* layout, float x_dip, float y_dip,
                                     uint32_t* insertion);
FB_API int32_t __cdecl fb_layout_caret(const fb_layout* layout, uint32_t position, fb_text_rect* out);
/* Nonempty ranges expand to cluster boundaries. If capacity is insufficient (including a NULL/0
   probe), returns HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER), sets *count, and writes no rects. */
FB_API int32_t __cdecl fb_layout_ranges(const fb_layout* layout, uint32_t start, uint32_t length,
                                        fb_text_rect* out, uint32_t capacity, uint32_t* count);
/* Close only after all users of this handle have finished. NULL is allowed. */
FB_API void __cdecl fb_layout_close(fb_layout* layout);

#ifdef __cplusplus
}
#endif

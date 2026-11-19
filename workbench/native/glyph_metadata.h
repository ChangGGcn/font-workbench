#pragma once
#include "api.h"

#ifdef __cplusplus
extern "C" {
#endif

typedef struct fb_glyph_book fb_glyph_book;
typedef struct fb_glyph_info {
    const wchar_t* name;                  /* NULL if absent; otherwise borrowed until book close. */
    uint32_t first_scalar, mapping_count; /* UINT32_MAX when mapping_count is zero. */
} fb_glyph_info;

/* Snapshot of the selected face. On failure (including E_ABORT), *result is NULL. */
FB_API int32_t fb_glyph_book_open(const fb_selection* selection, fb_stop stop, void* context,
                                  fb_glyph_book** result);
FB_API void fb_glyph_book_close(fb_glyph_book* book);
FB_API int32_t fb_glyph_info_at(const fb_glyph_book* book, uint32_t gid, fb_glyph_info* result);
/* Direct cmap scalars only, in ascending order without duplicates. GID 0 has no mappings. */
FB_API int32_t fb_glyph_scalar_at(const fb_glyph_book* book, uint32_t gid, uint32_t mapping_index,
                                  uint32_t* scalar);
/* 0: absent or no names, 1: names available, 2: unreadable, invalid or unsupported post;
   negative HRESULT: call error. */
FB_API int32_t fb_glyph_name_status(const fb_glyph_book* book);

#ifdef __cplusplus
}
#endif

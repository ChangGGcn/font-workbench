#pragma once
/* In-process UTF-16 C ABI, Windows x64. Opaque handles own all returned strings;
   copy strings before destroying the owner. HRESULT < 0 means failure. */
#include <stddef.h>
#include <stdint.h>
#ifdef FB_BUILD
#define FB_API __declspec(dllexport)
#else
#define FB_API __declspec(dllimport)
#endif
#ifdef __cplusplus
extern "C" {
#endif
typedef struct fb_catalog fb_catalog;
typedef struct fb_styles fb_styles;
typedef struct fb_sources fb_sources;
typedef struct fb_selection fb_selection;
typedef int(__cdecl* fb_stop)(void* context);
typedef struct {
    uint32_t families, styles, skipped;
} fb_stats;
typedef struct {
    const wchar_t *name, *key;
    uint32_t styles;
} fb_family_info;
typedef struct {
    const wchar_t* name;
    uint32_t index, weight;
} fb_style_info;
typedef struct {
    const wchar_t *path, *postscript;
    uint32_t face_index, glyphs, axes;
} fb_detail;
typedef struct {
    uint32_t tag;
    float minimum, initial, maximum;
} fb_axis;
FB_API uint32_t fb_version(void);
FB_API int32_t fb_open(const wchar_t* locale, fb_stop stop, void* context, fb_catalog** result);
/* Private collection. At most 128 explicit local SFNT/TTC files; never installed or registered. */
FB_API int32_t fb_open_files(const wchar_t* locale, const wchar_t* const* paths, uint32_t path_count,
                             fb_stop stop, void* context, fb_catalog** result);
FB_API void fb_close(fb_catalog* catalog);
FB_API int32_t fb_statistics(const fb_catalog* catalog, fb_stats* result);
FB_API int32_t fb_family(const fb_catalog* catalog, uint32_t id, fb_family_info* result);
/* Writes IDs in relevance order; never writes a partial result on a capacity error. */
FB_API int32_t fb_query(const fb_catalog* catalog, const wchar_t* query, uint32_t* ids, uint32_t capacity,
                        uint32_t* count);
FB_API int32_t fb_list_styles(const fb_catalog* catalog, uint32_t family, fb_styles** result);
/* Physical ownership inventory. Does not instantiate/render individual faces. One entry per style.
   A remote/unresolvable style fails the entire family; never infer a file from a family name. */
FB_API int32_t fb_sources_open(const fb_catalog* catalog, uint32_t family, fb_sources** out);
FB_API void fb_sources_close(fb_sources* sources);
FB_API uint32_t fb_sources_count(const fb_sources* sources);
FB_API int32_t fb_source_at(const fb_sources* sources, uint32_t ordinal, const wchar_t** path,
                            uint32_t* index);
FB_API void fb_styles_close(fb_styles* styles);
FB_API uint32_t fb_styles_count(const fb_styles* styles);
FB_API int32_t fb_style(const fb_styles* styles, uint32_t index, fb_style_info* result);
FB_API int32_t fb_select(const fb_catalog* catalog, uint32_t family, uint32_t style, fb_selection** result);
FB_API void fb_selection_close(fb_selection* selection);
FB_API int32_t fb_describe(const fb_selection* selection, fb_detail* result);
FB_API int32_t fb_axis_at(const fb_selection* selection, uint32_t index, fb_axis* result);
/* Direct cmap lookup only, not a statement about shaping. At most 2048 scalars. */
FB_API int32_t fb_map(const fb_selection* selection, const uint32_t* scalars, uint32_t count,
                      uint16_t* glyph_ids);
/* Bounds: at most 2048 UTF-16 units, 4096px/side and 4M pixels. Output PBGRA32.
   Axis values in fb_axis_at order, or null/count=0 to use the selected instance. */
FB_API int32_t fb_render(const fb_selection* selection, const wchar_t* text, float size_dip, float scale,
                         uint32_t width, uint32_t height, const float* axes, uint32_t axis_count,
                         uint8_t* pixels, uint32_t byte_count);
/* Draw one exact GID without Unicode lookup, shaping, or fallback. A blank outline is valid.
   width/height are physical pixels (8..512); ink is centered and fitted. Same axis contract as fb_render.
 */
FB_API int32_t fb_render_glyph(const fb_selection* selection, uint32_t gid, uint32_t width, uint32_t height,
                               const float* axes, uint32_t axis_count, uint8_t* pixels,
                               uint32_t byte_count);
#ifdef __cplusplus
}
#endif

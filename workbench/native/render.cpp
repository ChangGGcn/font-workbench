#include "editor_layout.h"
#include "internal.hpp"
#include <atomic>
#include <mutex>

struct fb_layout {
    fb::Com<IDWriteFactory6> factory;
    fb::Com<IDWriteFontFace5> face;
    fb::Com<IDWriteFontCollection2> collection;
    std::wstring text;
    fb::Com<IDWriteTextLayout> layout;
    // Background scroll draws and UI hit tests can share the same layout.
    mutable std::mutex layout_mutex;
    float scale{}, extent_dip{};
    uint32_t width{}, height{};
};

namespace {
enum class RasterQuality { standard, specimen };

// Synchronous DirectWrite sink. A glyph run from any other physical face is an error,
// not a reason to show an attractive but false specimen from a fallback font.
class Raster final : public IDWriteTextRenderer {
    std::atomic<ULONG> refs_{1};
    IDWriteFactory3* factory_;
    IDWriteFontFace5* face_;
    uint8_t* pixels_;
    LONG width_, height_;
    RasterQuality quality_;

  public:
    Raster(IDWriteFactory3* f, IDWriteFontFace5* face, uint8_t* p, LONG w, LONG h,
           RasterQuality quality)
        : factory_(f), face_(face), pixels_(p), width_(w), height_(h), quality_(quality) {}
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id, void** out) override {
        if (!out)
            return E_POINTER;
        *out = nullptr;
        if (id != __uuidof(IUnknown) && id != __uuidof(IDWritePixelSnapping) &&
            id != __uuidof(IDWriteTextRenderer))
            return E_NOINTERFACE;
        *out = static_cast<IDWriteTextRenderer*>(this);
        AddRef();
        return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override {
        return ++refs_;
    }
    ULONG STDMETHODCALLTYPE Release() override {
        const ULONG n = --refs_;
        if (!n)
            delete this;
        return n;
    }
    HRESULT STDMETHODCALLTYPE IsPixelSnappingDisabled(void*, BOOL* b) override {
        *b = TRUE;
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetCurrentTransform(void*, DWRITE_MATRIX* m) override {
        *m = {1, 0, 0, 1, 0, 0};
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetPixelsPerDip(void*, FLOAT* n) override {
        *n = 1.f;
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE DrawUnderline(void*, FLOAT, FLOAT, const DWRITE_UNDERLINE*,
                                            IUnknown*) override {
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE DrawStrikethrough(void*, FLOAT, FLOAT, const DWRITE_STRIKETHROUGH*,
                                                IUnknown*) override {
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE DrawInlineObject(void*, FLOAT, FLOAT, IDWriteInlineObject*, BOOL, BOOL,
                                               IUnknown*) override {
        return E_NOTIMPL;
    }
    HRESULT STDMETHODCALLTYPE DrawGlyphRun(void*, FLOAT x, FLOAT y, DWRITE_MEASURING_MODE mode,
                                           const DWRITE_GLYPH_RUN* run, const DWRITE_GLYPH_RUN_DESCRIPTION*,
                                           IUnknown*) override {
        return fb::guard([&] {
            fb::require(run && run->fontFace);
            if (!run->glyphCount)
                return;
            fb::require(face_->Equals(run->fontFace) != FALSE, DWRITE_E_NOFONT);
            // 16 physical px is the editor's 12 pt minimum at 100%. Below that,
            // leave the small-pixel hinting behavior of the original renderer intact.
            const bool smooth = quality_ == RasterQuality::specimen && run->fontEmSize >= 16.f;
            fb::Com<IDWriteGlyphRunAnalysis> a;
            // Rasterize unsnapped specimen outlines in both directions. Grid fitting can
            // move their edges away from the natural layout used for carets and selection.
            fb::check(factory_->CreateGlyphRunAnalysis(
                run, nullptr,
                smooth ? DWRITE_RENDERING_MODE1_NATURAL_SYMMETRIC : DWRITE_RENDERING_MODE1_NATURAL,
                mode, smooth ? DWRITE_GRID_FIT_MODE_DISABLED : DWRITE_GRID_FIT_MODE_DEFAULT,
                DWRITE_TEXT_ANTIALIAS_MODE_GRAYSCALE, x, y, a.put()));
            // Despite its name this is DirectWrite's one-byte grayscale texture when analysis
            // requests grayscale AA. Do not treat it as binary or use RGB subpixel coverage.
            constexpr auto texture = DWRITE_TEXTURE_ALIASED_1x1;
            RECT r{};
            fb::check(a->GetAlphaTextureBounds(texture, &r));
            r.left = std::max(0L, r.left);
            r.top = std::max(0L, r.top);
            r.right = std::min(width_, r.right);
            r.bottom = std::min(height_, r.bottom);
            if (r.left >= r.right || r.top >= r.bottom)
                return;
            auto w = static_cast<size_t>(r.right - r.left);
            std::vector<uint8_t> mask(w * static_cast<size_t>(r.bottom - r.top));
            fb::check(a->CreateAlphaTexture(texture, &r, mask.data(), static_cast<UINT32>(mask.size())));
            for (LONG row = r.top; row < r.bottom; ++row)
                for (LONG col = r.left; col < r.right; ++col) {
                    auto alpha = mask[(row - r.top) * w + col - r.left];
                    if (!alpha)
                        continue;
                    auto p = pixels_ + (static_cast<size_t>(row) * width_ + col) * 4;
                    const auto total = alpha + (p[3] * (255 - alpha) + 127) / 255;
                    // RGB remains zero: premultiplied black, tintable by the caller.
                    p[3] = static_cast<uint8_t>(total);
                }
        });
    }
};
std::vector<DWRITE_FONT_AXIS_VALUE> axis_values(const fb_selection* s, const float* overrides,
                                                uint32_t count) {
    fb::require(count == 0 || (overrides && count == s->axes.size()));
    auto values = s->values;
    for (uint32_t i = 0; i < count; ++i) {
        const auto& a = s->axes[i];
        float v = overrides[i];
        fb::require(std::isfinite(v) && v >= a.range.minimum && v <= a.range.maximum);
        values[a.slot].value = v;
    }
    return values;
}

bool valid_viewport(uint32_t w, uint32_t h) {
    return w && h && w <= 4096 && h <= 4096 && static_cast<uint64_t>(w) * h <= 4194304;
}

bool valid_feature_tag(uint32_t tag) {
    // Printable ASCII with one or more non-spaces, optionally padded on the right.
    bool padding = false;
    for (int i = 0; i < 4; ++i, tag >>= 8) {
        const auto ch = tag & 0xffu;
        if (ch == 0x20 && i != 0)
            padding = true;
        else if (ch < 0x21 || ch > 0x7e || padding)
            return false;
    }
    return true;
}

uint32_t leading_unit(const std::wstring& text, uint32_t position) {
    if (position && position < text.size()) {
        const auto left = text[position - 1], right = text[position];
        // DirectWrite can report CR and LF as separate zero-advance clusters.
        if ((left == L'\r' && right == L'\n') ||
            (left >= 0xd800 && left <= 0xdbff && right >= 0xdc00 && right <= 0xdfff))
            return position - 1;
    }
    return position;
}

struct CaretHit {
    float x{}, y{};
    DWRITE_HIT_TEST_METRICS cluster{};
};

CaretHit hit_position(const fb_layout& l, uint32_t position) {
    CaretHit hit;
    fb::check(
        l.layout->HitTestTextPosition(leading_unit(l.text, position), FALSE, &hit.x, &hit.y, &hit.cluster));
    return hit;
}

int32_t draw_layout(const fb_layout* l, float scroll_y_dip, uint8_t* pixels, uint32_t bytes,
                    RasterQuality quality) {
    if (!l || !pixels)
        return E_POINTER;
    return fb::guard([&] {
        const auto needed = l->width * l->height * 4;
        fb::require(bytes >= needed && std::isfinite(scroll_y_dip) && scroll_y_dip >= 0);
        std::fill_n(pixels, needed, uint8_t{0});
        if (l->text.empty() || scroll_y_dip >= l->extent_dip)
            return;
        fb::Com<IDWriteTextRenderer> sink;
        *sink.put() = new Raster(l->factory.get(), l->face.get(), pixels, static_cast<LONG>(l->width),
                                 static_cast<LONG>(l->height), quality);
        const std::lock_guard lock(l->layout_mutex);
        fb::check(l->layout->Draw(nullptr, sink.get(), 0.f, -scroll_y_dip * l->scale));
    });
}
} // namespace

int32_t __cdecl fb_layout_create(const fb_selection* s, const wchar_t* text, float size, float scale,
                                 uint32_t w, uint32_t h, const float* overrides, uint32_t axis_count,
                                 const fb_feature* features, uint32_t feature_count, fb_layout** out) {
    if (!out)
        return E_POINTER;
    *out = nullptr;
    if (!s || !text || (!features && feature_count))
        return E_POINTER;
    return fb::guard([&] {
        const auto len = static_cast<uint32_t>(wcsnlen(text, 2049));
        fb::require(len <= 2048 && valid_viewport(w, h));
        fb::require(std::isfinite(size) && size >= 1 && size <= 256 && std::isfinite(scale) &&
                    scale >= 0.5f && scale <= 8);
        fb::require(feature_count <= 64);
        for (uint32_t i = 0; i < feature_count; ++i) {
            fb::require(valid_feature_tag(features[i].tag) && features[i].value <= 1);
            for (uint32_t j = 0; j < i; ++j)
                fb::require(features[j].tag != features[i].tag);
        }
        auto values = axis_values(s, overrides, axis_count);
        const auto n = static_cast<uint32_t>(values.size());
        auto result = std::make_unique<fb_layout>();
        result->factory = s->factory;
        result->text.assign(text, len);
        result->scale = scale;
        result->width = w;
        result->height = h;

        fb::Com<IDWriteFontFaceReference1> ref;
        fb::check(s->resource->CreateFontFaceReference(s->simulations, values.data(), n, ref.put()));
        fb::check(ref->CreateFontFace(result->face.put()));
        fb::Com<IDWriteFontSetBuilder2> builder;
        fb::check(result->factory->CreateFontSetBuilder(builder.put()));
        fb::check(builder->AddFontFaceReference(ref.get()));
        fb::Com<IDWriteFontSet> set;
        fb::check(builder->CreateFontSet(set.put()));
        fb::check(result->factory->CreateFontCollectionFromFontSet(
            set.get(), DWRITE_FONT_FAMILY_MODEL_TYPOGRAPHIC, result->collection.put()));
        fb::Com<IDWriteFontFamily2> family;
        fb::check(result->collection->GetFontFamily(0, family.put()));
        fb::Com<IDWriteLocalizedStrings> names;
        fb::check(family->GetFamilyNames(names.put()));
        auto name = fb::pick_name(names.get(), s->locale);
        fb::Com<IDWriteTextFormat3> format;
        fb::check(result->factory->CreateTextFormat(name.c_str(), result->collection.get(), values.data(),
                                                    n, size * scale, s->locale.c_str(), format.put()));
        fb::check(format->SetAutomaticFontAxes(DWRITE_AUTOMATIC_FONT_AXES_NONE));
        fb::check(format->SetFontFallback(s->no_fallback.get()));
        // Keep the original natural, pixel-sized layout for Raster. Convert only at the ABI boundary.
        fb::check(result->factory->CreateTextLayout(result->text.c_str(), len, format.get(),
                                                    static_cast<float>(w), static_cast<float>(h),
                                                    result->layout.put()));
        if (feature_count && len) {
            fb::Com<IDWriteTypography> typography;
            fb::check(result->factory->CreateTypography(typography.put()));
            for (uint32_t i = 0; i < feature_count; ++i)
                fb::check(typography->AddFontFeature(
                    {static_cast<DWRITE_FONT_FEATURE_TAG>(features[i].tag), features[i].value}));
            fb::check(result->layout->SetTypography(typography.get(), {0, len}));
        }
        DWRITE_TEXT_METRICS metrics{};
        fb::check(result->layout->GetMetrics(&metrics));
        result->extent_dip = metrics.height / scale;
        *out = result.release();
    });
}

int32_t __cdecl fb_layout_draw(const fb_layout* l, uint8_t* pixels, uint32_t bytes) {
    return fb_layout_draw_at(l, 0.f, pixels, bytes);
}

int32_t __cdecl fb_layout_draw_at(const fb_layout* l, float scroll_y_dip, uint8_t* pixels, uint32_t bytes) {
    return draw_layout(l, scroll_y_dip, pixels, bytes, RasterQuality::specimen);
}

int32_t __cdecl fb_layout_extent(const fb_layout* l, float* total_height_dip) {
    if (!l || !total_height_dip)
        return E_POINTER;
    *total_height_dip = l->extent_dip;
    return S_OK;
}

int32_t __cdecl fb_layout_hit(const fb_layout* l, float x_dip, float y_dip, uint32_t* insertion) {
    if (!l || !insertion)
        return E_POINTER;
    return fb::guard([&] {
        const float x = x_dip * l->scale, y = y_dip * l->scale;
        fb::require(std::isfinite(x) && std::isfinite(y));
        BOOL trailing = FALSE, inside = FALSE;
        DWRITE_HIT_TEST_METRICS hit{};
        const std::lock_guard lock(l->layout_mutex);
        fb::check(l->layout->HitTestPoint(x, y, &trailing, &inside, &hit));
        const auto offset = hit.textPosition + (trailing ? hit.length : 0);
        *insertion = leading_unit(l->text, std::min(offset, static_cast<uint32_t>(l->text.size())));
    });
}

int32_t __cdecl fb_layout_caret(const fb_layout* l, uint32_t position, fb_text_rect* out) {
    if (!l || !out)
        return E_POINTER;
    return fb::guard([&] {
        fb::require(position <= l->text.size());
        const std::lock_guard lock(l->layout_mutex);
        const auto hit = hit_position(*l, position);
        *out = {hit.x / l->scale, hit.y / l->scale, 1.f, hit.cluster.height / l->scale};
    });
}

int32_t __cdecl fb_layout_ranges(const fb_layout* l, uint32_t start, uint32_t length, fb_text_rect* out,
                                 uint32_t capacity, uint32_t* count) {
    if (!count)
        return E_POINTER;
    *count = 0;
    if (!l || (!out && capacity))
        return E_POINTER;
    return fb::guard([&] {
        fb::require(start <= l->text.size() && length <= l->text.size() - start);
        if (!length)
            return;
        const std::lock_guard lock(l->layout_mutex);
        const uint32_t first = leading_unit(l->text, hit_position(*l, start).cluster.textPosition);
        const auto last = hit_position(*l, start + length - 1).cluster;
        uint32_t end = std::max(start + length, last.textPosition + last.length);
        if (leading_unit(l->text, end) != end)
            ++end;
        constexpr HRESULT insufficient = HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);
        UINT32 needed = 0;
        const auto hr = l->layout->HitTestTextRange(first, end - first, 0, 0, nullptr, 0, &needed);
        if (hr != insufficient)
            fb::check(hr);
        *count = needed;
        fb::require(needed <= capacity, insufficient);
        if (!needed)
            return;
        std::vector<DWRITE_HIT_TEST_METRICS> hits(needed);
        fb::check(l->layout->HitTestTextRange(first, end - first, 0, 0, hits.data(), needed, &needed));
        *count = needed;
        for (uint32_t i = 0; i < needed; ++i) {
            const auto& hit = hits[i];
            out[i] = {hit.left / l->scale, hit.top / l->scale, hit.width / l->scale, hit.height / l->scale};
        }
    });
}

void __cdecl fb_layout_close(fb_layout* l) {
    delete l;
}

int32_t fb_render(const fb_selection* s, const wchar_t* text, float size, float scale, uint32_t w,
                  uint32_t h, const float* overrides, uint32_t count, uint8_t* pixels, uint32_t bytes) {
    if (!s || !pixels || !text)
        return E_POINTER;
    if (!valid_viewport(w, h) || bytes < w * h * 4)
        return E_INVALIDARG;
    fb_layout* handle = nullptr;
    const auto hr = fb_layout_create(s, text, size, scale, w, h, overrides, count, nullptr, 0, &handle);
    const std::unique_ptr<fb_layout> layout(handle);
    // Library previews keep their existing raster policy and do not pay for specimen AA.
    return hr < 0 ? hr : draw_layout(layout.get(), 0.f, pixels, bytes, RasterQuality::standard);
}

int32_t fb_render_glyph(const fb_selection* s, uint32_t gid, uint32_t w, uint32_t h, const float* overrides,
                        uint32_t count, uint8_t* pixels, uint32_t bytes) {
    if (!s || !pixels)
        return E_POINTER;
    return fb::guard([&] {
        fb::require(gid < s->glyphs && w >= 8 && w <= 512 && h >= 8 && h <= 512 && bytes >= w * h * 4);
        auto values = axis_values(s, overrides, count);
        fb::Com<IDWriteFontFace5> face = s->face;
        bool changed = false;
        for (size_t i = 0; i < values.size(); ++i)
            if (values[i].value != s->values[i].value)
                changed = true;
        if (changed)
            fb::check(s->resource->CreateFontFace(s->simulations, values.data(),
                                                  static_cast<uint32_t>(values.size()), face.put()));
        const UINT16 index = static_cast<UINT16>(gid);
        DWRITE_FONT_METRICS metrics{};
        // The legacy IDWriteFontFace methods remain available through the base interface.
        static_cast<IDWriteFontFace*>(face.get())->GetMetrics(&metrics);
        DWRITE_GLYPH_METRICS glyph{};
        fb::check(face->GetDesignGlyphMetrics(&index, 1, &glyph, FALSE));
        fb::require(metrics.designUnitsPerEm != 0, DWRITE_E_FILEFORMAT);
        // Use real bearings, not the advance box: negative bearings, marks and typographic swashes matter.
        const double left = glyph.leftSideBearing,
                     top = static_cast<double>(glyph.topSideBearing) - glyph.verticalOriginY;
        const double right = static_cast<double>(glyph.advanceWidth) - glyph.rightSideBearing;
        const double bottom =
            static_cast<double>(glyph.advanceHeight) - glyph.bottomSideBearing - glyph.verticalOriginY;
        const double em = metrics.designUnitsPerEm;
        const double pixels_per_unit =
            std::min({(w - 4.) / std::max(1., right - left), (h - 4.) / std::max(1., bottom - top),
                      std::min(w, h) * 0.85 / em});
        const float origin_x = static_cast<float>((w - (left + right) * pixels_per_unit) * 0.5);
        const float origin_y = static_cast<float>((h - (top + bottom) * pixels_per_unit) * 0.5);
        const FLOAT advance = 0;
        const DWRITE_GLYPH_OFFSET offset{};
        const DWRITE_GLYPH_RUN run{
            face.get(), static_cast<FLOAT>(em * pixels_per_unit), 1, &index, &advance, &offset, FALSE, 0};
        std::fill_n(pixels, static_cast<size_t>(w) * h * 4, uint8_t{0});
        fb::Com<IDWriteTextRenderer> sink;
        *sink.put() = new Raster(s->factory.get(), face.get(), pixels, static_cast<LONG>(w),
                                 static_cast<LONG>(h), RasterQuality::standard);
        fb::check(sink->DrawGlyphRun(nullptr, origin_x, origin_y, DWRITE_MEASURING_MODE_NATURAL, &run,
                                     nullptr, nullptr));
    });
}

#include "internal.hpp"

namespace fb {
std::wstring file_path(IDWriteFontFile* file) {
    Com<IDWriteFontFileLoader> loader;
    check(file->GetLoader(loader.put()));
    Com<IDWriteLocalFontFileLoader> local;
    const HRESULT local_hr =
        loader->QueryInterface(__uuidof(IDWriteLocalFontFileLoader), reinterpret_cast<void**>(local.put()));
    if (local_hr == E_NOINTERFACE)
        return {}; // A legitimate non-file-backed provider.
    check(local_hr);
    const void* key = nullptr;
    UINT32 bytes = 0, length = 0;
    check(file->GetReferenceKey(&key, &bytes));
    check(local->GetFilePathLengthFromKey(key, bytes, &length));
    require(length < 32768, DWRITE_E_FILEFORMAT);
    std::wstring path(length + 1, 0);
    check(local->GetFilePathFromKey(key, bytes, path.data(), length + 1));
    path.resize(length);
    return path;
}
} // namespace fb
int32_t fb_select(const fb_catalog* c, uint32_t family_id, uint32_t style, fb_selection** result) {
    if (!c || !result)
        return E_POINTER;
    *result = nullptr;
    return fb::guard([&] {
        auto family = c->get(family_id);
        fb::require(style < family->GetFontCount());
        fb::require(family->GetFontLocality(style) == DWRITE_LOCALITY_LOCAL, DWRITE_E_NOFONT);
        fb::Com<IDWriteFont3> font;
        fb::check(family->GetFont(style, font.put()));
        fb::Com<IDWriteFontFace3> face3;
        fb::check(font->CreateFontFace(face3.put()));
        auto s = std::make_unique<fb_selection>();
        s->factory = c->factory;
        s->locale = c->locale;
        s->face = face3.as<IDWriteFontFace5>();
        fb::check(s->face->GetFontResource(s->resource.put()));
        s->face_index = s->resource->GetFontFaceIndex();
        s->glyphs = s->face->GetGlyphCount();
        s->simulations = s->face->GetSimulations();
        fb::Com<IDWriteFontFile> file;
        fb::check(s->resource->GetFontFile(file.put()));
        s->path = fb::file_path(file.get());
        BOOL exists = FALSE;
        fb::Com<IDWriteLocalizedStrings> names;
        fb::check(font->GetInformationalStrings(DWRITE_INFORMATIONAL_STRING_POSTSCRIPT_NAME, names.put(),
                                                &exists));
        if (exists)
            s->postscript = fb::pick_name(names.get(), L"en-us");
        auto n = s->face->GetFontAxisValueCount(), m = s->resource->GetFontAxisCount();
        fb::require(n <= 128 && m <= 128, DWRITE_E_FILEFORMAT);
        s->values.resize(n);
        if (n)
            fb::check(s->face->GetFontAxisValues(s->values.data(), n));
        std::vector<DWRITE_FONT_AXIS_RANGE> ranges(m);
        if (m)
            fb::check(s->resource->GetFontAxisRanges(ranges.data(), m));
        for (size_t i = 0; i < n; ++i) {
            const auto value = s->values[i];
            fb::require(std::isfinite(value.value), DWRITE_E_FILEFORMAT);
            auto pos = std::find_if(ranges.begin(), ranges.end(),
                                    [&](auto r) { return r.axisTag == value.axisTag; });
            if (pos == ranges.end())
                continue;
            fb::require(std::isfinite(pos->minValue) && std::isfinite(pos->maxValue) &&
                            pos->minValue <= pos->maxValue,
                        DWRITE_E_FILEFORMAT);
            if (pos->minValue < pos->maxValue) {
                fb::require(value.value >= pos->minValue && value.value <= pos->maxValue,
                            DWRITE_E_FILEFORMAT);
                s->axes.push_back(
                    {{static_cast<uint32_t>(value.axisTag), pos->minValue, value.value, pos->maxValue}, i});
            }
        }
        fb::Com<IDWriteFontFallbackBuilder> none;
        fb::check(s->factory->CreateFontFallbackBuilder(none.put()));
        fb::check(none->CreateFontFallback(s->no_fallback.put()));
        *result = s.release();
    });
}
void fb_selection_close(fb_selection* s) {
    delete s;
}
int32_t fb_describe(const fb_selection* s, fb_detail* out) {
    if (!s || !out)
        return E_POINTER;
    *out = {s->path.c_str(), s->postscript.c_str(), s->face_index, s->glyphs,
            static_cast<uint32_t>(s->axes.size())};
    return S_OK;
}
int32_t fb_axis_at(const fb_selection* s, uint32_t i, fb_axis* out) {
    if (!s || !out)
        return E_POINTER;
    if (i >= s->axes.size())
        return E_INVALIDARG;
    *out = s->axes[i].range;
    return S_OK;
}
int32_t fb_map(const fb_selection* s, const uint32_t* scalars, uint32_t count, uint16_t* out) {
    if (!s || (!scalars && count) || (!out && count))
        return E_POINTER;
    if (count > 2048)
        return E_INVALIDARG;
    for (uint32_t i = 0; i < count; ++i)
        if (scalars[i] > 0x10ffff || (scalars[i] >= 0xd800 && scalars[i] <= 0xdfff))
            return E_INVALIDARG;
    return count ? s->face->GetGlyphIndices(scalars, count, out) : S_OK;
}

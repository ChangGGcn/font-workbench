// The standalone checks include this file to exercise the same parser and cmap index,
// excluding the DirectWrite adapter and exported ABI.
#ifndef FB_GLYPH_METADATA_CHECKS
#include "glyph_metadata.h"
#include "internal.hpp"
#endif

#include <algorithm>
#include <array>
#include <cstddef>
#include <cstdint>
#include <string>
#include <utility>
#include <vector>

namespace {
constexpr size_t post_header = 32;
constexpr size_t max_post_bytes = 16 * 1024 * 1024; // Same limit as the legacy reader.
constexpr const wchar_t* standard_names[] = {
#include "glyph_standard_names.inc"
};
constexpr uint16_t standard_count = sizeof(standard_names) / sizeof(*standard_names);
static_assert(standard_count == 258);

// Bytes 0x80..0xff of the legacy reader's .NET code page 10000. Keep this deterministic
// without depending on the system ANSI code page or installed conversion tables.
constexpr wchar_t mac_roman[] = {
    0x00c4, 0x00c5, 0x00c7, 0x00c9, 0x00d1, 0x00d6, 0x00dc, 0x00e1, 0x00e0, 0x00e2, 0x00e4, 0x00e3, 0x00e5,
    0x00e7, 0x00e9, 0x00e8, 0x00ea, 0x00eb, 0x00ed, 0x00ec, 0x00ee, 0x00ef, 0x00f1, 0x00f3, 0x00f2, 0x00f4,
    0x00f6, 0x00f5, 0x00fa, 0x00f9, 0x00fb, 0x00fc, 0x2020, 0x00b0, 0x00a2, 0x00a3, 0x00a7, 0x2022, 0x00b6,
    0x00df, 0x00ae, 0x00a9, 0x2122, 0x00b4, 0x00a8, 0x2260, 0x00c6, 0x00d8, 0x221e, 0x00b1, 0x2264, 0x2265,
    0x00a5, 0x00b5, 0x2202, 0x2211, 0x220f, 0x03c0, 0x222b, 0x00aa, 0x00ba, 0x2126, 0x00e6, 0x00f8, 0x00bf,
    0x00a1, 0x00ac, 0x221a, 0x0192, 0x2248, 0x2206, 0x00ab, 0x00bb, 0x2026, 0x00a0, 0x00c0, 0x00c3, 0x00d5,
    0x0152, 0x0153, 0x2013, 0x2014, 0x201c, 0x201d, 0x2018, 0x2019, 0x00f7, 0x25ca, 0x00ff, 0x0178, 0x2044,
    0x20ac, 0x2039, 0x203a, 0xfb01, 0xfb02, 0x2021, 0x00b7, 0x201a, 0x201e, 0x2030, 0x00c2, 0x00ca, 0x00c1,
    0x00cb, 0x00c8, 0x00cd, 0x00ce, 0x00cf, 0x00cc, 0x00d3, 0x00d4, 0xf8ff, 0x00d2, 0x00da, 0x00db, 0x00d9,
    0x0131, 0x02c6, 0x02dc, 0x00af, 0x02d8, 0x02d9, 0x02da, 0x00b8, 0x02dd, 0x02db, 0x02c7,
};
static_assert(sizeof(mac_roman) / sizeof(*mac_roman) == 128);

enum class NameStatus : int32_t { none = 0, available = 1, invalid = 2 };
struct PostNames {
    NameStatus status = NameStatus::none;
    std::vector<uint16_t> indices{};
    std::vector<std::wstring> custom{};

    const wchar_t* at(uint32_t gid) const {
        if (gid >= indices.size())
            return nullptr;
        const auto index = indices[gid];
        if (index < standard_count)
            return standard_names[index];
        const auto& name = custom[index - standard_count];
        return name.empty() ? nullptr : name.c_str();
    }
};

uint16_t read_be16(const uint8_t* bytes) {
    return static_cast<uint16_t>((bytes[0] << 8) | bytes[1]);
}

PostNames parse_post_2(const uint8_t* data, size_t size, uint16_t glyph_count) {
    constexpr size_t indices_start = post_header + 2;
    size_t cursor = indices_start + size_t{glyph_count} * 2;
    if (cursor > size || read_be16(data + post_header) != glyph_count)
        return {NameStatus::invalid};

    PostNames names{NameStatus::available};
    names.indices.reserve(glyph_count);
    uint16_t max_index = 0;
    for (size_t gid = 0; gid < glyph_count; ++gid) {
        const auto index = read_be16(data + indices_start + 2 * gid);
        names.indices.push_back(index);
        max_index = std::max(max_index, index);
    }
    if (max_index < standard_count)
        return names;

    const size_t custom_count = size_t{max_index} - standard_count + 1;
    if (custom_count > size - cursor) // At least the length byte for each referenced name slot.
        return {NameStatus::invalid};
    names.custom.reserve(custom_count);
    for (size_t i = 0; i < custom_count; ++i) {
        if (cursor == size)
            return {NameStatus::invalid};
        const size_t length = data[cursor++];
        if (length > size - cursor)
            return {NameStatus::invalid};
        std::wstring name;
        name.reserve(length);
        for (size_t j = 0; j < length; ++j) {
            const auto ch = data[cursor + j];
            if (!ch) // An embedded NUL cannot be returned as an ABI string.
                return {NameStatus::invalid};
            name.push_back(ch < 0x80 ? static_cast<wchar_t>(ch) : mac_roman[ch - 0x80]);
        }
        names.custom.push_back(std::move(name));
        cursor += length;
    }
    if (std::none_of(names.indices.begin(), names.indices.end(), [&](uint16_t index) {
            return index < standard_count || !names.custom[index - standard_count].empty();
        }))
        return {};
    return names;
}

PostNames parse_post(const uint8_t* data, size_t size, uint16_t glyph_count) {
    if (!data || !glyph_count || size < post_header || size > max_post_bytes)
        return {NameStatus::invalid};
    const auto version = (uint32_t{read_be16(data)} << 16) | read_be16(data + 2);
    switch (version) {
    case 0x00010000: {
        PostNames names{NameStatus::available};
        const auto count = std::min(glyph_count, standard_count);
        names.indices.reserve(count);
        for (uint16_t gid = 0; gid < count; ++gid)
            names.indices.push_back(gid);
        return names;
    }
    case 0x00020000:
        return parse_post_2(data, size, glyph_count);
    case 0x00025000: { // The signed offset array begins immediately after the header.
        if (glyph_count > size - post_header)
            return {NameStatus::invalid};
        PostNames names{NameStatus::available};
        names.indices.reserve(glyph_count);
        for (int gid = 0; gid < glyph_count; ++gid) {
            const int byte = data[post_header + gid];
            const int index = gid + (byte < 128 ? byte : byte - 256);
            if (index < 0 || index >= standard_count)
                return {NameStatus::invalid};
            names.indices.push_back(static_cast<uint16_t>(index));
        }
        return names;
    }
    case 0x00030000:
        return {};
    default:
        return {NameStatus::invalid};
    }
}

constexpr uint32_t max_scalar = 0x10ffff;
constexpr size_t cmap_block = 1024;
using GlyphMappings = std::vector<std::vector<uint32_t>>;

// Sorting the ranges and scanning only their as-yet unseen suffix ensures that
// each scalar is looked up once and every GID's reverse mappings are already ordered.
template <class Range, class Lookup, class Cancel>
GlyphMappings invert_cmap(uint16_t glyph_count, std::vector<Range> ranges, Lookup lookup, Cancel cancel) {
    std::sort(ranges.begin(), ranges.end(),
              [](const Range& a, const Range& b) { return a.first < b.first; });
    GlyphMappings result(glyph_count);
    std::array<uint32_t, cmap_block> scalars{};
    std::array<uint16_t, cmap_block> gids{};
    size_t used = 0;
    auto flush = [&] {
        cancel();
        lookup(scalars.data(), static_cast<uint32_t>(used), gids.data());
        for (size_t i = 0; i < used; ++i) {
            if (gids[i] != 0 && gids[i] < glyph_count)
                result[gids[i]].push_back(scalars[i]);
        }
        used = 0;
    };

    uint32_t next = 0;
    for (const auto& range : ranges) {
        const auto first = std::max<uint32_t>(range.first, next);
        const auto last = std::min<uint32_t>(range.last, max_scalar);
        if (first > last)
            continue;
        next = last + 1;
        for (uint32_t scalar = first; scalar <= last; ++scalar) {
            if (scalar >= 0xd800 && scalar <= 0xdfff) {
                scalar = 0xdfff;
                continue;
            }
            scalars[used++] = scalar;
            if (used == cmap_block)
                flush();
        }
    }
    if (used)
        flush();
    return result;
}
} // namespace

#ifndef FB_GLYPH_METADATA_CHECKS
namespace {
PostNames face_post(IDWriteFontFace* face, uint16_t glyph_count) {
    struct Table {
        IDWriteFontFace* face;
        void* context = nullptr;
        bool acquired = false;
        ~Table() {
            if (acquired || context)
                face->ReleaseFontTable(context);
        }
    } table{face};
    const void* data = nullptr;
    UINT32 size = 0;
    BOOL exists = FALSE;
    const auto hr = face->TryGetFontTable(DWRITE_MAKE_OPENTYPE_TAG('p', 'o', 's', 't'), &data, &size,
                                          &table.context, &exists);
    table.acquired = SUCCEEDED(hr) && exists;
    if (FAILED(hr))
        return {NameStatus::invalid};
    if (!exists)
        return {};
    return parse_post(static_cast<const uint8_t*>(data), size, glyph_count);
}
} // namespace

struct fb_glyph_book {
    PostNames names;
    GlyphMappings mappings;
};

int32_t fb_glyph_book_open(const fb_selection* selection, fb_stop stop, void* context,
                           fb_glyph_book** result) {
    if (!result)
        return E_POINTER;
    *result = nullptr;
    if (!selection || !selection->face.get())
        return E_POINTER;
    return fb::guard([&] {
        const auto cancel = [&] { fb::require(!stop || !stop(context), E_ABORT); };
        cancel();
        auto* face = selection->face.get();
        const auto glyph_count = face->GetGlyphCount();
        // A few local faces (including named/legacy system styles) report a short or
        // zero count in the sizing call. Start with a real buffer and retry with headroom.
        // The ranges only accelerate lookup: GetGlyphIndices is authoritative for a scalar.
        std::vector<DWRITE_UNICODE_RANGE> ranges(128);
        for (;;) {
            cancel();
            UINT32 actual = 0;
            const auto capacity = static_cast<UINT32>(ranges.size());
            const auto hr = face->GetUnicodeRanges(capacity, ranges.data(), &actual);
            if (SUCCEEDED(hr) && actual <= capacity) {
                ranges.resize(actual);
                break;
            }
            if (hr == E_OUTOFMEMORY) fb::check(hr);
            if (hr == HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER) && capacity < 16384 && actual <= 16384) {
                ranges.resize(std::max(capacity * 2, actual));
                continue;
            }
            // A bounded full-domain walk is done only for this explicitly inspected face.
            ranges.assign(1, DWRITE_UNICODE_RANGE{0, max_scalar});
            break;
        }
        auto mappings = invert_cmap(
            glyph_count, std::move(ranges),
            [&](const uint32_t* scalars, uint32_t n, uint16_t* gids) {
                fb::check(face->GetGlyphIndices(scalars, n, gids));
            },
            cancel);
        cancel();
        auto names = face_post(face, glyph_count);
        cancel();
        *result = new fb_glyph_book{std::move(names), std::move(mappings)};
    });
}

void fb_glyph_book_close(fb_glyph_book* book) {
    delete book;
}

int32_t fb_glyph_info_at(const fb_glyph_book* book, uint32_t gid, fb_glyph_info* result) {
    if (!book || !result)
        return E_POINTER;
    if (gid >= book->mappings.size())
        return E_INVALIDARG;
    const auto& scalars = book->mappings[gid];
    *result = {book->names.at(gid), scalars.empty() ? UINT32_MAX : scalars.front(),
               static_cast<uint32_t>(scalars.size())};
    return S_OK;
}

int32_t fb_glyph_scalar_at(const fb_glyph_book* book, uint32_t gid, uint32_t mapping_index,
                           uint32_t* scalar) {
    if (!book || !scalar)
        return E_POINTER;
    if (gid >= book->mappings.size() || mapping_index >= book->mappings[gid].size())
        return E_INVALIDARG;
    *scalar = book->mappings[gid][mapping_index];
    return S_OK;
}

int32_t fb_glyph_name_status(const fb_glyph_book* book) {
    return book ? static_cast<int32_t>(book->names.status) : E_POINTER;
}
#endif

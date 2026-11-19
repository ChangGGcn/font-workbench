#include "font_details.h"
#include "internal.hpp"

namespace {
constexpr uint32_t max_name_units = 16384;
constexpr size_t max_layout_bytes = 64 * 1024 * 1024;
constexpr size_t max_feature_records = 4096;
constexpr size_t max_lookup_references = 65536;

// Errors in optional metadata are local to the field, except for resource exhaustion.
bool optional_success(HRESULT hr) {
    if (hr == E_OUTOFMEMORY)
        fb::check(hr);
    return SUCCEEDED(hr);
}

bool valid_utf16(const std::wstring& value) {
    for (size_t i = 0; i < value.size(); ++i) {
        const auto ch = value[i];
        if ((ch < 0x20 && ch != L'\t' && ch != L'\n' && ch != L'\r') || ch == 0x7f)
            return false;
        if (ch >= 0xd800 && ch <= 0xdbff) {
            if (++i == value.size() || value[i] < 0xdc00 || value[i] > 0xdfff)
                return false;
        } else if (ch >= 0xdc00 && ch <= 0xdfff) {
            return false;
        }
    }
    return true;
}

std::wstring string_at(IDWriteLocalizedStrings* names, uint32_t index) {
    uint32_t length = 0;
    if (!optional_success(names->GetStringLength(index, &length)) || !length || length > max_name_units)
        return {};
    std::wstring value(length + 1, L'\0');
    if (!optional_success(names->GetString(index, value.data(), length + 1)))
        return {};
    value.resize(length);
    if (!valid_utf16(value))
        return {};
    return value;
}

std::wstring localized(IDWriteLocalizedStrings* names, const std::wstring& locale) {
    if (!names)
        return {};
    const auto language = locale.substr(0, locale.find(L'-'));
    for (auto candidate : {locale.c_str(), language.c_str(), L"en-us", L"en"}) {
        if (!*candidate)
            continue;
        BOOL exists = FALSE;
        uint32_t index = 0;
        if (optional_success(names->FindLocaleName(candidate, &index, &exists)) && exists) {
            auto value = string_at(names, index);
            if (!value.empty())
                return value;
        }
    }
    // Prefer readable text over losing a field whose preferred localized records are invalid.
    for (uint32_t i = 0, count = std::min<uint32_t>(names->GetCount(), 8); i < count; ++i) {
        auto value = string_at(names, i);
        if (!value.empty())
            return value;
    }
    return {};
}

std::wstring info(const fb_selection& s, DWRITE_INFORMATIONAL_STRING_ID id) {
    fb::Com<IDWriteLocalizedStrings> names;
    BOOL exists = FALSE;
    if (!optional_success(s.face->GetInformationalStrings(id, names.put(), &exists)) || !exists)
        return {};
    return localized(names.get(), s.locale);
}

struct Bytes {
    const uint8_t* data;
    size_t size;

    bool has(size_t offset, size_t count) const {
        return data && offset <= size && count <= size - offset;
    }
    // Callers check the containing fixed-size field or array before reading.
    uint16_t u16(size_t offset) const {
        return static_cast<uint16_t>((data[offset] << 8) | data[offset + 1]);
    }
    uint32_t u32(size_t offset) const {
        return (static_cast<uint32_t>(u16(offset)) << 16) | u16(offset + 2);
    }
};

class FontTable {
    IDWriteFontFace* face_;
    void* context_ = nullptr;
    Bytes bytes_{};
    bool present_ = false;
    bool readable_ = false;

  public:
    FontTable(const FontTable&) = delete;
    FontTable& operator=(const FontTable&) = delete;
    FontTable(IDWriteFontFace* face, uint32_t tag) : face_(face) {
        const void* data = nullptr;
        uint32_t size = 0;
        BOOL exists = FALSE;
        readable_ = optional_success(face_->TryGetFontTable(tag, &data, &size, &context_, &exists));
        present_ = readable_ && exists;
        bytes_ = {static_cast<const uint8_t*>(data), size};
    }
    ~FontTable() {
        if (present_)
            face_->ReleaseFontTable(context_);
    }
    bool present() const {
        return present_;
    }
    bool readable() const {
        return readable_;
    }
    Bytes bytes() const {
        return bytes_;
    }
};

uint32_t outline(IDWriteFontFace* face, DWRITE_FONT_FACE_TYPE type) {
    // A collection face's GetType does not distinguish its outlines. Inspect only this face's tables.
    const auto has_table = [&](uint32_t tag, size_t minimum) {
        const FontTable table(face, tag);
        return table.present() && table.bytes().has(0, minimum);
    };
    if (has_table(DWRITE_MAKE_OPENTYPE_TAG('C', 'F', 'F', '2'), 5))
        return FB_FONT_OUTLINE_CFF2;
    if (has_table(DWRITE_MAKE_OPENTYPE_TAG('C', 'F', 'F', ' '), 4))
        return FB_FONT_OUTLINE_CFF;
    if (has_table(DWRITE_MAKE_OPENTYPE_TAG('g', 'l', 'y', 'f'), 1))
        return FB_FONT_OUTLINE_TRUETYPE;
    switch (type) {
    case DWRITE_FONT_FACE_TYPE_CFF:
        return FB_FONT_OUTLINE_CFF;
    case DWRITE_FONT_FACE_TYPE_TRUETYPE:
        return FB_FONT_OUTLINE_TRUETYPE;
    case DWRITE_FONT_FACE_TYPE_TYPE1:
        return FB_FONT_OUTLINE_TYPE1;
    case DWRITE_FONT_FACE_TYPE_VECTOR:
        return FB_FONT_OUTLINE_VECTOR;
    case DWRITE_FONT_FACE_TYPE_BITMAP:
        return FB_FONT_OUTLINE_BITMAP;
    case DWRITE_FONT_FACE_TYPE_RAW_CFF:
        return FB_FONT_OUTLINE_RAW_CFF;
    default:
        return FB_FONT_OUTLINE_UNKNOWN;
    }
}

uint32_t reversed(uint32_t tag) {
    return (tag << 24) | ((tag & 0xff00) << 8) | ((tag >> 8) & 0xff00) | (tag >> 24);
}

bool parse_feature_list(Bytes bytes, std::vector<uint32_t>& tags) {
    // GSUB/GPOS v1.0 or v1.1. FeatureVariations may substitute tables, not add feature tags.
    if (bytes.size > max_layout_bytes || !bytes.has(0, 10) || bytes.u16(0) != 1 || bytes.u16(2) > 1)
        return false;
    const size_t header = bytes.u16(2) == 0 ? 10 : 14;
    if (!bytes.has(0, header))
        return false;
    const size_t scripts = bytes.u16(4), features = bytes.u16(6), lookups = bytes.u16(8);
    if (scripts < header || features < header || lookups < header || !bytes.has(scripts, 2) ||
        !bytes.has(features, 2) || !bytes.has(lookups, 2))
        return false;
    if (header == 14) {
        const size_t variations = bytes.u32(10);
        if (variations && (variations < header || !bytes.has(variations, 8)))
            return false;
    }

    const size_t feature_count = bytes.u16(features), lookup_count = bytes.u16(lookups);
    const size_t records_bytes = 2 + feature_count * 6;
    if (feature_count > max_feature_records || !bytes.has(features, records_bytes) ||
        !bytes.has(lookups, 2 + lookup_count * 2))
        return false;

    // This inventories the FeatureList, not ScriptList/lookup subtables or feature-specific params.
    // Bound total index traversal as distinct records can reuse a single Feature table.
    tags.reserve(feature_count);
    size_t remaining = max_lookup_references;
    for (size_t i = 0; i < feature_count; ++i) {
        const size_t record = features + 2 + i * 6;
        for (size_t j = 0; j < 4; ++j)
            if (bytes.data[record + j] < (j == 0 ? 0x21 : 0x20) || bytes.data[record + j] > 0x7e)
                return false;
        const size_t offset = bytes.u16(record + 4);
        const size_t feature = features + offset;
        if (offset < records_bytes || !bytes.has(feature, 4))
            return false;
        const size_t params = bytes.u16(feature), indices = bytes.u16(feature + 2);
        const size_t feature_bytes = 4 + indices * 2;
        if (indices > remaining || !bytes.has(feature, feature_bytes) ||
            (params && (params < feature_bytes || !bytes.has(feature + params, 1))))
            return false;
        remaining -= indices;
        for (size_t j = 0; j < indices; ++j)
            if (bytes.u16(feature + 4 + j * 2) >= lookup_count)
                return false;
        tags.push_back(reversed(bytes.u32(record)));
    }
    return true;
}

uint32_t read_features(IDWriteFontFace* face, uint32_t table_tag, uint32_t table_flag,
                       std::vector<fb_font_feature>& result) {
    const FontTable table(face, table_tag);
    if (!table.readable())
        return FB_FONT_LAYOUT_UNAVAILABLE;
    if (!table.present())
        return FB_FONT_LAYOUT_ABSENT;
    std::vector<uint32_t> tags;
    if (!parse_feature_list(table.bytes(), tags))
        return FB_FONT_LAYOUT_UNAVAILABLE;
    for (auto tag : tags)
        result.push_back({tag, table_flag});
    return FB_FONT_LAYOUT_READ;
}

void merge_tags(std::vector<fb_font_feature>& features) {
    std::sort(features.begin(), features.end(),
              [](fb_font_feature a, fb_font_feature b) { return reversed(a.tag) < reversed(b.tag); });
    size_t length = 0;
    for (auto feature : features) {
        if (length && features[length - 1].tag == feature.tag)
            features[length - 1].tables |= feature.tables;
        else
            features[length++] = feature;
    }
    features.resize(length);
}

const wchar_t* text(const std::wstring& value) {
    return value.empty() ? nullptr : value.c_str();
}
} // namespace

struct fb_font_details {
    std::wstring path, family, full_name, postscript, style_name, version, manufacturer, designer,
        copyright_notice, license_description, license_url;
    fb_font_details_info values{};
    std::vector<fb_axis> axes;
    std::vector<fb_font_feature> features;
};

int32_t fb_font_details_open(const fb_selection* selection, fb_font_details** out) {
    if (!out)
        return E_POINTER;
    *out = nullptr;
    if (!selection)
        return E_POINTER;
    return fb::guard([&] {
        const auto& s = *selection;
        auto details = std::make_unique<fb_font_details>();
        details->path = s.path;
        details->family = info(s, DWRITE_INFORMATIONAL_STRING_TYPOGRAPHIC_FAMILY_NAMES);
        if (details->family.empty()) {
            fb::Com<IDWriteLocalizedStrings> names;
            if (optional_success(s.face->GetFamilyNames(names.put())))
                details->family = localized(names.get(), s.locale);
        }
        {
            // GetFaceNames describes the selected instance, including a named variable-font instance.
            fb::Com<IDWriteLocalizedStrings> names;
            if (optional_success(s.face->GetFaceNames(names.put())))
                details->style_name = localized(names.get(), s.locale);
        }
        details->full_name = info(s, DWRITE_INFORMATIONAL_STRING_FULL_NAME);
        details->postscript = info(s, DWRITE_INFORMATIONAL_STRING_POSTSCRIPT_NAME);
        details->version = info(s, DWRITE_INFORMATIONAL_STRING_VERSION_STRINGS);
        details->manufacturer = info(s, DWRITE_INFORMATIONAL_STRING_MANUFACTURER);
        details->designer = info(s, DWRITE_INFORMATIONAL_STRING_DESIGNER);
        details->copyright_notice = info(s, DWRITE_INFORMATIONAL_STRING_COPYRIGHT_NOTICE);
        details->license_description = info(s, DWRITE_INFORMATIONAL_STRING_LICENSE_DESCRIPTION);
        details->license_url = info(s, DWRITE_INFORMATIONAL_STRING_LICENSE_INFO_URL);

        DWRITE_FONT_METRICS metrics{};
        auto* face = static_cast<IDWriteFontFace*>(s.face.get());
        face->GetMetrics(&metrics);
        auto& values = details->values;
        values.face_index = s.face_index;
        values.glyph_count = face->GetGlyphCount();
        values.units_per_em = metrics.designUnitsPerEm;
        values.ascent = metrics.ascent;
        values.descent = metrics.descent;
        values.weight = s.face->GetWeight();
        values.stretch = s.face->GetStretch();
        values.style = s.face->GetStyle();
        values.simulations = face->GetSimulations();
        const auto type = face->GetType();
        values.face_type = type;
        values.outline = outline(face, type);
        details->axes.reserve(s.axes.size());
        for (const auto& axis : s.axes)
            details->axes.push_back(axis.range);
        values.axis_count = static_cast<uint32_t>(details->axes.size());
        values.gsub_status = read_features(face, DWRITE_MAKE_OPENTYPE_TAG('G', 'S', 'U', 'B'),
                                           FB_FONT_FEATURE_GSUB, details->features);
        values.gpos_status = read_features(face, DWRITE_MAKE_OPENTYPE_TAG('G', 'P', 'O', 'S'),
                                           FB_FONT_FEATURE_GPOS, details->features);
        merge_tags(details->features);
        values.feature_count = static_cast<uint32_t>(details->features.size());
        *out = details.release();
    });
}

void fb_font_details_close(fb_font_details* details) {
    delete details;
}

int32_t fb_font_details_describe(const fb_font_details* details, fb_font_details_info* out) {
    if (!details || !out)
        return E_POINTER;
    *out = details->values;
    out->path = text(details->path);
    out->family = text(details->family);
    out->full_name = text(details->full_name);
    out->postscript = text(details->postscript);
    out->style_name = text(details->style_name);
    out->version = text(details->version);
    out->manufacturer = text(details->manufacturer);
    out->designer = text(details->designer);
    out->copyright_notice = text(details->copyright_notice);
    out->license_description = text(details->license_description);
    out->license_url = text(details->license_url);
    return S_OK;
}

int32_t fb_font_details_axis_at(const fb_font_details* details, uint32_t i, fb_axis* out) {
    if (!details || !out)
        return E_POINTER;
    if (i >= details->axes.size())
        return E_INVALIDARG;
    *out = details->axes[i];
    return S_OK;
}

int32_t fb_font_details_feature_at(const fb_font_details* details, uint32_t i, fb_font_feature* out) {
    if (!details || !out)
        return E_POINTER;
    if (i >= details->features.size())
        return E_INVALIDARG;
    *out = details->features[i];
    return S_OK;
}

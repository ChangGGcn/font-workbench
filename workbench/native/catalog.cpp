#include "internal.hpp"
#include <cwctype>
#include <limits>
#include <map>
#include <set>

namespace {
std::wstring string_at(IDWriteLocalizedStrings* names, uint32_t i) {
    UINT32 len = 0;
    fb::check(names->GetStringLength(i, &len));
    fb::require(len <= 2048, DWRITE_E_FILEFORMAT);
    std::wstring value(len + 1, 0);
    fb::check(names->GetString(i, value.data(), len + 1));
    value.resize(len);
    fb::require(value.find(L'\0') == value.npos, DWRITE_E_FILEFORMAT);
    return value;
}
struct OrdinalLess {
    bool operator()(const std::wstring& a, const std::wstring& b) const {
        return CompareStringOrdinal(a.c_str(), -1, b.c_str(), -1, TRUE) == CSTR_LESS_THAN;
    }
};
struct Entry {
    fb::Family family;
    std::wstring aliases;
};
} // namespace
namespace fb {
std::wstring pick_name(IDWriteLocalizedStrings* names, const std::wstring& locale) {
    if (!names || !names->GetCount())
        return {};
    const auto lang = locale.substr(0, locale.find(L'-'));
    for (auto tag : {locale.c_str(), lang.c_str(), L"en-us", L"en"}) {
        BOOL exists = FALSE;
        UINT32 i = 0;
        check(names->FindLocaleName(tag, &i, &exists));
        if (exists) {
            auto name = string_at(names, i);
            if (!name.empty())
                return name;
        }
    }
    return string_at(names, 0);
}
} // namespace fb
fb::Com<IDWriteFontFamily2> fb_catalog::get(uint32_t id) const {
    fb::require(id < families.size());
    fb::Com<IDWriteFontFamily2> family;
    fb::check(collection->GetFontFamily(families[id].ordinal, family.put()));
    return family;
}
uint32_t fb_version() {
    return 3;
}
static int32_t open_catalog(const wchar_t* locale, const wchar_t* const* paths, uint32_t count,
                            bool installed, fb_stop stop, void* ctx, fb_catalog** result) {
    if (!result)
        return E_POINTER;
    *result = nullptr;
    return fb::guard([&] {
        fb::require(locale && wcsnlen(locale, 86) < 86);
        auto canceled = [&] {
            if (stop && stop(ctx))
                throw fb::Failure{E_ABORT};
        };
        canceled();
        auto c = std::make_unique<fb_catalog>();
        c->locale = (*locale ? locale : L"en-us");
        // A refresh takes a fresh, isolated OS snapshot. Never enumerate arbitrary disk libraries.
        fb::check(DWriteCreateFactory(DWRITE_FACTORY_TYPE_ISOLATED, __uuidof(IDWriteFactory6),
                                      reinterpret_cast<IUnknown**>(c->factory.put())));
        if (installed) {
            fb::check(c->factory->GetSystemFontCollection(FALSE, DWRITE_FONT_FAMILY_MODEL_TYPOGRAPHIC,
                                                          c->collection.put()));
        } else if (count) {
            fb::require(paths && count <= 128);
            std::set<std::wstring, OrdinalLess> seen;
            uint64_t total = 0;
            fb::Com<IDWriteFontSetBuilder2> builder;
            fb::check(c->factory->CreateFontSetBuilder(builder.put()));
            for (uint32_t i = 0; i < count; ++i) {
                canceled();
                fb::require(paths[i] && wcsnlen(paths[i], 32768) < 32768);
                std::wstring path(
                    paths[i]); // No UNC, device, stream, relative path, or implicit enumeration.
                fb::require(
                    path.size() > 3 &&
                    ((path[0] >= L'A' && path[0] <= L'Z') || (path[0] >= L'a' && path[0] <= L'z')) &&
                    path[1] == L':' && path[2] == L'\\' && path.find(L':', 2) == path.npos);
                std::vector<wchar_t> normalized(32768);
                auto length = GetFullPathNameW(path.c_str(), 32768, normalized.data(), nullptr);
                fb::require(length > 0 && length < 32768 && path == normalized.data());
                auto dot = path.find_last_of(L'.');
                fb::require(dot != path.npos);
                auto ext = path.substr(dot);
                fb::require(_wcsicmp(ext.c_str(), L".ttf") == 0 || _wcsicmp(ext.c_str(), L".otf") == 0 ||
                            _wcsicmp(ext.c_str(), L".ttc") == 0 || _wcsicmp(ext.c_str(), L".otc") == 0);
                fb::require(seen.insert(path).second);
                WIN32_FILE_ATTRIBUTE_DATA attrs{};
                if (!GetFileAttributesExW(path.c_str(), GetFileExInfoStandard, &attrs))
                    throw fb::Failure{HRESULT_FROM_WIN32(GetLastError())};
                fb::require(
                    !(attrs.dwFileAttributes &
                      (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT | FILE_ATTRIBUTE_OFFLINE)));
                const auto size = (static_cast<uint64_t>(attrs.nFileSizeHigh) << 32) | attrs.nFileSizeLow;
                fb::require(size && size <= (1ull << 30) && (total += size) <= (4ull << 30));
                fb::Com<IDWriteFontFile> file;
                fb::check(static_cast<IDWriteFactory*>(c->factory.get())
                              ->CreateFontFileReference(path.c_str(), nullptr, file.put()));
                BOOL supported = FALSE;
                DWRITE_FONT_FILE_TYPE file_type{};
                DWRITE_FONT_FACE_TYPE face_type{};
                UINT32 faces = 0;
                fb::check(file->Analyze(&supported, &file_type, &face_type, &faces));
                fb::require(supported && faces && faces <= 4096, DWRITE_E_FILEFORMAT);
                fb::check(static_cast<IDWriteFontSetBuilder1*>(builder.get())->AddFontFile(file.get()));
            }
            fb::Com<IDWriteFontSet> set;
            fb::check(builder->CreateFontSet(set.put()));
            fb::check(c->factory->CreateFontCollectionFromFontSet(
                set.get(), DWRITE_FONT_FAMILY_MODEL_TYPOGRAPHIC, c->collection.put()));
        }
        const auto n = c->collection.get() ? c->collection->GetFontFamilyCount() : 0;
        fb::require(n <= 100000, HRESULT_FROM_WIN32(ERROR_FILE_TOO_LARGE));
        std::vector<Entry> rows;
        rows.reserve(n);
        for (uint32_t i = 0; i < n; ++i) {
            if (i % 16 == 0)
                canceled();
            try {
                fb::Com<IDWriteFontFamily2> f;
                fb::check(c->collection->GetFontFamily(i, f.put()));
                fb::Com<IDWriteLocalizedStrings> names;
                fb::check(f->GetFamilyNames(names.put()));
                auto name = fb::pick_name(names.get(), c->locale),
                     key = fb::pick_name(names.get(), L"en-us");
                const auto styles = f->GetFontCount();
                fb::require(!name.empty() && !key.empty() && styles && styles <= 65536,
                            DWRITE_E_FILEFORMAT);
                std::wstring aliases = key;
                for (uint32_t j = 0; j < std::min<UINT32>(names->GetCount(), 64) && aliases.size() < 16384;
                     ++j) {
                    auto alias = string_at(names.get(), j);
                    if (alias != key && alias != name && !alias.empty()) {
                        aliases += L' ';
                        aliases += alias;
                    }
                }
                rows.push_back({{std::move(name), std::move(key), i, styles}, std::move(aliases)});
            } catch (fb::Failure e) {
                if (!installed || e.hr == E_OUTOFMEMORY)
                    throw;
                ++c->stats.skipped;
            }
        }
        canceled();
        OrdinalLess less;
        std::stable_sort(rows.begin(), rows.end(),
                         [&](const auto& a, const auto& b) { return less(a.family.name, b.family.name); });
        std::map<std::wstring, uint32_t, OrdinalLess> keys;
        for (const auto& row : rows)
            ++keys[row.family.key];
        std::vector<fb::Label> labels;
        labels.reserve(rows.size());
        c->families.reserve(rows.size());
        for (auto& row : rows) {
            fb::require(c->stats.styles <= UINT32_MAX - row.family.styles,
                        HRESULT_FROM_WIN32(ERROR_ARITHMETIC_OVERFLOW));
            c->stats.styles += row.family.styles;
            // Ambiguous identities stay browsable, but cannot silently join the same shelf entry.
            if (keys.at(row.family.key) > 1)
                row.family.key.clear();
            labels.push_back({row.family.name, std::move(row.aliases)});
            c->families.push_back(std::move(row.family));
        }
        c->stats.families = static_cast<uint32_t>(rows.size());
        c->search = std::make_unique<fb::SearchIndex>(std::move(labels));
        canceled();
        *result = c.release();
    });
}
int32_t fb_open(const wchar_t* locale, fb_stop stop, void* ctx, fb_catalog** out) {
    return open_catalog(locale, nullptr, 0, true, stop, ctx, out);
}
int32_t fb_open_files(const wchar_t* locale, const wchar_t* const* paths, uint32_t count, fb_stop stop,
                      void* ctx, fb_catalog** out) {
    return open_catalog(locale, paths, count, false, stop, ctx, out);
}
void fb_close(fb_catalog* c) {
    delete c;
}
int32_t fb_statistics(const fb_catalog* c, fb_stats* out) {
    if (!c || !out)
        return E_POINTER;
    *out = c->stats;
    return S_OK;
}
int32_t fb_family(const fb_catalog* c, uint32_t id, fb_family_info* out) {
    if (!c || !out)
        return E_POINTER;
    if (id >= c->families.size())
        return E_INVALIDARG;
    const auto& f = c->families[id];
    *out = {f.name.c_str(), f.key.c_str(), f.styles};
    return S_OK;
}
int32_t fb_query(const fb_catalog* c, const wchar_t* text, uint32_t* out, uint32_t cap, uint32_t* count) {
    if (!c || !text || !count)
        return E_POINTER;
    *count = 0;
    return fb::guard([&] {
        size_t len = wcsnlen(text, 1025);
        fb::require(len <= 1024);
        auto ids = c->search->query({text, len});
        *count = static_cast<uint32_t>(ids.size());
        fb::require(ids.size() <= cap, HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER));
        if (!ids.empty()) {
            fb::require(out != nullptr);
            std::copy(ids.begin(), ids.end(), out);
        }
    });
}
int32_t fb_list_styles(const fb_catalog* c, uint32_t id, fb_styles** out) {
    if (!c || !out)
        return E_POINTER;
    *out = nullptr;
    return fb::guard([&] {
        auto family = c->get(id);
        auto list = std::make_unique<fb_styles>();
        for (uint32_t i = 0; i < family->GetFontCount(); ++i) {
            if (family->GetFontLocality(i) != DWRITE_LOCALITY_LOCAL)
                continue;
            fb::Com<IDWriteFont3> font;
            fb::check(family->GetFont(i, font.put()));
            fb::Com<IDWriteLocalizedStrings> names;
            fb::check(font->GetFaceNames(names.put()));
            auto name = fb::pick_name(names.get(), c->locale);
            if (name.empty())
                name = L"#" + std::to_wstring(i);
            list->rows.push_back({std::move(name), i, static_cast<uint32_t>(font->GetWeight())});
        }
        *out = list.release();
    });
}
void fb_styles_close(fb_styles* s) {
    delete s;
}
uint32_t fb_styles_count(const fb_styles* s) {
    return s ? static_cast<uint32_t>(s->rows.size()) : 0;
}
int32_t fb_style(const fb_styles* s, uint32_t i, fb_style_info* out) {
    if (!s || !out)
        return E_POINTER;
    if (i >= s->rows.size())
        return E_INVALIDARG;
    const auto& row = s->rows[i];
    *out = {row.name.c_str(), row.index, row.weight};
    return S_OK;
}

struct fb_sources {
    std::vector<std::pair<std::wstring, uint32_t>> rows;
};
int32_t fb_sources_open(const fb_catalog* c, uint32_t id, fb_sources** out) {
    if (!out)
        return E_POINTER;
    *out = nullptr;
    if (!c)
        return E_POINTER;
    return fb::guard([&] {
        auto family = c->get(id);
        const auto count = family->GetFontCount();
        fb::require(count && count == c->families[id].styles, DWRITE_E_FILEFORMAT);
        auto list = std::make_unique<fb_sources>();
        list->rows.reserve(count);
        for (uint32_t i = 0; i < count; ++i) {
            fb::require(family->GetFontLocality(i) == DWRITE_LOCALITY_LOCAL, DWRITE_E_NOFONT);
            fb::Com<IDWriteFontFaceReference> ref;
            fb::check(family->GetFontFaceReference(i, ref.put()));
            fb::Com<IDWriteFontFile> file;
            fb::check(ref->GetFontFile(file.put()));
            auto path = fb::file_path(file.get());
            fb::require(!path.empty(), DWRITE_E_NOFONT);
            list->rows.emplace_back(std::move(path), ref->GetFontFaceIndex());
        }
        *out = list.release();
    });
}
void fb_sources_close(fb_sources* s) {
    delete s;
}
uint32_t fb_sources_count(const fb_sources* s) {
    return s ? static_cast<uint32_t>(s->rows.size()) : 0;
}
int32_t fb_source_at(const fb_sources* s, uint32_t i, const wchar_t** path, uint32_t* index) {
    if (!s || !path || !index)
        return E_POINTER;
    if (i >= s->rows.size())
        return E_INVALIDARG;
    *path = s->rows[i].first.c_str();
    *index = s->rows[i].second;
    return S_OK;
}

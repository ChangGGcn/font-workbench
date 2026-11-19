#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include "../../native/font_details.h"
#include <windows.h>

#include <array>
#include <cmath>
#include <cstdlib>
#include <iostream>
#include <memory>
#include <set>
#include <string>
#include <string_view>
#include <vector>

static_assert(sizeof(void*) == 8 && sizeof(wchar_t) == 2);
static_assert(sizeof(fb_font_details_info) == 152 && offsetof(fb_font_details_info, face_index) == 88 &&
              offsetof(fb_font_details_info, gpos_status) == 144);
static_assert(sizeof(fb_font_feature) == 8 && offsetof(fb_font_feature, tables) == 4);

namespace {
unsigned passed = 0, skipped = 0;

void check(bool condition, const char* expression, int line) {
    if (!condition) {
        std::cerr << "FAIL font_details_checks:" << line << ": " << expression << '\n';
        std::exit(EXIT_FAILURE);
    }
    ++passed;
}
#define CHECK(expression) check((expression), #expression, __LINE__)

void skip(const char* fixture) {
    ++skipped;
    std::cout << "SKIP optional Windows font fixture: " << fixture << '\n';
}

using Catalog = std::unique_ptr<fb_catalog, decltype(&fb_close)>;
using Styles = std::unique_ptr<fb_styles, decltype(&fb_styles_close)>;
using Selection = std::unique_ptr<fb_selection, decltype(&fb_selection_close)>;
using Details = std::unique_ptr<fb_font_details, decltype(&fb_font_details_close)>;

std::wstring system_font(const wchar_t* file) {
    std::array<wchar_t, MAX_PATH> root{};
    const auto length = GetWindowsDirectoryW(root.data(), static_cast<UINT>(root.size()));
    CHECK(length > 0 && length < root.size());
    return std::wstring(root.data(), length) + L"\\Fonts\\" + file;
}

bool file_exists(const std::wstring& path) {
    const DWORD attrs = GetFileAttributesW(path.c_str());
    return attrs != INVALID_FILE_ATTRIBUTES && !(attrs & FILE_ATTRIBUTE_DIRECTORY);
}

std::vector<Selection> select_file(const std::wstring& path) {
    const wchar_t* paths[] = {path.c_str()};
    fb_catalog* raw_catalog = nullptr;
    CHECK(fb_open_files(L"en-us", paths, 1, nullptr, nullptr, &raw_catalog) == S_OK && raw_catalog);
    const Catalog catalog(raw_catalog, fb_close);
    fb_stats stats{};
    CHECK(fb_statistics(catalog.get(), &stats) == S_OK && stats.families > 0);
    std::vector<Selection> selected;
    for (uint32_t f = 0; f < stats.families; ++f) {
        fb_styles* raw_styles = nullptr;
        CHECK(fb_list_styles(catalog.get(), f, &raw_styles) == S_OK && raw_styles);
        const Styles styles(raw_styles, fb_styles_close);
        for (uint32_t i = 0, count = fb_styles_count(styles.get()); i < count; ++i) {
            fb_style_info style{};
            CHECK(fb_style(styles.get(), i, &style) == S_OK);
            fb_selection* raw_selection = nullptr;
            CHECK(fb_select(catalog.get(), f, style.index, &raw_selection) == S_OK && raw_selection);
            selected.emplace_back(raw_selection, fb_selection_close);
        }
    }
    CHECK(!selected.empty());
    return selected; // The caller can read details after this private catalog has closed.
}

bool is_text(const wchar_t* value, std::wstring_view expected) {
    return value && value == expected;
}

bool has_text(const wchar_t* value) {
    return value && *value;
}

Details read(const fb_selection* face, fb_font_details_info& info) {
    fb_font_details* raw = nullptr;
    CHECK(fb_font_details_open(face, &raw) == S_OK && raw);
    Details details(raw, fb_font_details_close);
    CHECK(fb_font_details_describe(details.get(), &info) == S_OK);
    fb_detail selected{};
    CHECK(fb_describe(face, &selected) == S_OK);
    CHECK(is_text(info.path, selected.path) && info.face_index == selected.face_index);
    CHECK(info.glyph_count == selected.glyphs && info.glyph_count > 0);
    CHECK(info.axis_count == selected.axes && info.axis_count <= 128);
    CHECK(info.units_per_em > 0 && info.ascent > 0);
    CHECK(info.feature_count <= 8192);
    CHECK(info.gsub_status <= FB_FONT_LAYOUT_UNAVAILABLE && info.gpos_status <= FB_FONT_LAYOUT_UNAVAILABLE);
    for (uint32_t index = 0; index < info.axis_count; ++index) {
        fb_axis before{}, after{};
        CHECK(fb_axis_at(face, index, &before) == S_OK);
        CHECK(fb_font_details_axis_at(details.get(), index, &after) == S_OK);
        CHECK(after.tag == before.tag && after.minimum == before.minimum &&
              after.initial == before.initial && after.maximum == before.maximum);
        CHECK(std::isfinite(after.initial) && after.minimum < after.maximum &&
              after.initial >= after.minimum && after.initial <= after.maximum);
    }
    fb_axis next_axis{};
    fb_font_feature next_feature{};
    CHECK(fb_font_details_axis_at(details.get(), info.axis_count, &next_axis) == E_INVALIDARG);
    CHECK(fb_font_details_feature_at(details.get(), info.feature_count, &next_feature) == E_INVALIDARG);
    return details;
}

std::string tag_text(uint32_t tag) {
    std::string label;
    for (unsigned i = 0; i < 4; ++i) {
        const auto ch = (tag >> (i * 8)) & 0xff;
        CHECK(ch >= (i == 0 ? 0x21u : 0x20u) && ch <= 0x7e);
        label.push_back(static_cast<char>(ch));
    }
    return label;
}

std::set<std::string> tags(const Details& details, const fb_font_details_info& info, uint32_t tables) {
    std::set<std::string> result;
    std::string previous;
    for (uint32_t i = 0; i < info.feature_count; ++i) {
        fb_font_feature feature{};
        CHECK(fb_font_details_feature_at(details.get(), i, &feature) == S_OK);
        CHECK(feature.tables > 0 && feature.tables <= (FB_FONT_FEATURE_GSUB | FB_FONT_FEATURE_GPOS));
        CHECK(!(feature.tables & FB_FONT_FEATURE_GSUB) || info.gsub_status == FB_FONT_LAYOUT_READ);
        CHECK(!(feature.tables & FB_FONT_FEATURE_GPOS) || info.gpos_status == FB_FONT_LAYOUT_READ);
        auto label = tag_text(feature.tag);
        CHECK(previous < label); // Strict ASCII order (also forbids duplicates).
        previous = label;
        if (feature.tables & tables)
            result.insert(label);
    }
    return result;
}

void check_nulls() {
    fb_font_details_info info{};
    fb_axis axis{};
    fb_font_feature feature{};
    auto* snapshot = reinterpret_cast<fb_font_details*>(&info);
    CHECK(fb_font_details_open(nullptr, &snapshot) == E_POINTER && !snapshot);
    CHECK(fb_font_details_open(nullptr, nullptr) == E_POINTER);
    CHECK(fb_font_details_describe(nullptr, &info) == E_POINTER);
    CHECK(fb_font_details_axis_at(nullptr, 0, &axis) == E_POINTER);
    CHECK(fb_font_details_feature_at(nullptr, 0, &feature) == E_POINTER);
    fb_font_details_close(nullptr);
}

void check_arial(const wchar_t* file, const wchar_t* full_name, const wchar_t* ps, uint32_t weight) {
    auto faces = select_file(system_font(file));
    fb_font_details_info info{};
    Details actual(nullptr, fb_font_details_close);
    for (const auto& face : faces) {
        auto details = read(face.get(), info);
        if (info.simulations == 0 && info.weight == weight && info.style == 0) {
            CHECK(fb_font_details_open(face.get(), nullptr) == E_POINTER);
            actual = std::move(details);
            break;
        }
    }
    CHECK(actual != nullptr);
    faces.clear(); // No DWrite face, catalog, table pointer, or name pointer should be needed from here on.
    CHECK(fb_font_details_describe(actual.get(), &info) == S_OK);
    CHECK(is_text(info.family, L"Arial") && is_text(info.full_name, full_name) &&
          is_text(info.postscript, ps));
    CHECK(has_text(info.style_name) && has_text(info.version) && has_text(info.manufacturer));
    CHECK(has_text(info.copyright_notice) && has_text(info.license_description));
    CHECK(info.face_index == 0 && info.outline == FB_FONT_OUTLINE_TRUETYPE && info.axis_count == 0);
    CHECK(info.weight == weight && info.stretch == 5 && info.units_per_em == 2048 && info.descent > 0);
    CHECK(tags(actual, info, FB_FONT_FEATURE_GSUB).count("liga") == 1);
    CHECK(tags(actual, info, FB_FONT_FEATURE_GPOS).count("kern") == 1);
    CHECK(fb_font_details_describe(actual.get(), nullptr) == E_POINTER);
    CHECK(fb_font_details_axis_at(actual.get(), 0, nullptr) == E_POINTER);
    CHECK(fb_font_details_feature_at(actual.get(), 0, nullptr) == E_POINTER);
    fb_font_feature sentinel{0x01234567, 42};
    CHECK(fb_font_details_feature_at(actual.get(), UINT32_MAX, &sentinel) == E_INVALIDARG);
    CHECK(sentinel.tag == 0x01234567 && sentinel.tables == 42);
}

void check_collection() {
    const auto path = system_font(L"cambria.ttc");
    if (!file_exists(path)) {
        skip("Cambria TTC");
        return;
    }
    bool cambria = false, math = false;
    for (const auto& face : select_file(path)) {
        fb_font_details_info info{};
        const auto details = read(face.get(), info);
        CHECK(info.outline == FB_FONT_OUTLINE_TRUETYPE);
        if (is_text(info.full_name, L"Cambria")) {
            CHECK(info.face_index == 0);
            CHECK(tags(details, info, FB_FONT_FEATURE_GSUB).count("ssty") == 0);
            cambria = true;
        }
        if (is_text(info.full_name, L"Cambria Math")) {
            CHECK(info.face_index == 1);
            CHECK(tags(details, info, FB_FONT_FEATURE_GSUB).count("ssty") == 1);
            math = true;
        }
    }
    CHECK(cambria && math);
}

void check_variable() {
    const auto path = system_font(L"bahnschrift.ttf");
    if (!file_exists(path)) {
        skip("Bahnschrift variable font");
        return;
    }
    std::set<float> weights;
    std::set<std::wstring> styles;
    bool width = false;
    for (const auto& face : select_file(path)) {
        fb_font_details_info info{};
        const auto details = read(face.get(), info);
        CHECK(info.face_index == 0 && info.outline == FB_FONT_OUTLINE_TRUETYPE);
        CHECK(has_text(info.style_name));
        styles.insert(info.style_name);
        for (uint32_t i = 0; i < info.axis_count; ++i) {
            fb_axis axis{};
            CHECK(fb_font_details_axis_at(details.get(), i, &axis) == S_OK);
            const auto tag = tag_text(axis.tag);
            if (tag == "wght") {
                CHECK(axis.minimum <= 400 && axis.maximum >= 700);
                weights.insert(axis.initial);
            }
            if (tag == "wdth")
                width = true;
        }
    }
    CHECK(width && weights.size() > 1 && styles.size() > 1);
}

void check_optional_fields() {
    const auto path = system_font(L"marlett.ttf");
    if (!file_exists(path)) {
        skip("Marlett without layout or authorship fields");
        return;
    }
    for (const auto& face : select_file(path)) {
        fb_font_details_info info{};
        const auto details = read(face.get(), info);
        CHECK(is_text(info.family, L"Marlett") && has_text(info.postscript) && has_text(info.license_url));
        CHECK(!info.manufacturer && !info.designer);
        CHECK(info.feature_count == 0 && info.axis_count == 0);
        CHECK(info.gsub_status == FB_FONT_LAYOUT_ABSENT && info.gpos_status == FB_FONT_LAYOUT_ABSENT);
    }
}
} // namespace

// Compile this translation unit only, without FB_BUILD; link FontBookCore's import library.
int main() {
    check_nulls();
    check_arial(L"arial.ttf", L"Arial", L"ArialMT", 400);
    check_arial(L"arialbd.ttf", L"Arial Bold", L"Arial-BoldMT", 700);
    check_collection();
    check_variable();
    check_optional_fields();
    std::cout << "PASS font_details_checks: " << passed << " checks, " << skipped << " skips\n";
}

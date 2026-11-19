#ifndef NOMINMAX
#define NOMINMAX
#endif
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif

#include "../../native/api.h"
#include "../../native/glyph_metadata.h"

#include <windows.h>

#include <algorithm>
#include <array>
#include <chrono>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <cstdlib>
#include <cwchar>
#include <iomanip>
#include <iostream>
#include <limits>
#include <memory>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

namespace {

static_assert(sizeof(void*) == 8, "FontBookCore requires Windows x64");
static_assert(sizeof(wchar_t) == 2, "FontBookCore uses UTF-16 wchar_t");
static_assert(sizeof(fb_stats) == 12, "fb_stats ABI mismatch");
static_assert(sizeof(fb_family_info) == 24, "fb_family_info ABI mismatch");
static_assert(sizeof(fb_style_info) == 16, "fb_style_info ABI mismatch");
static_assert(sizeof(fb_detail) == 32, "fb_detail ABI mismatch");
static_assert(offsetof(fb_detail, face_index) == 16,
              "fb_detail.face_index ABI mismatch");
static_assert(offsetof(fb_detail, glyphs) == 20,
              "fb_detail.glyphs ABI mismatch");
static_assert(offsetof(fb_detail, axes) == 24,
              "fb_detail.axes ABI mismatch");
static_assert(sizeof(fb_axis) == 16, "fb_axis ABI mismatch");
static_assert(offsetof(fb_axis, initial) == 8,
              "fb_axis.initial ABI mismatch");

constexpr std::size_t kBufferGuard = 16;
constexpr std::uint8_t kGuardByte = 0xA5;
constexpr std::uint32_t kQueryGuard = 0xA5A5A5A5U;
constexpr std::uint32_t kMaxAxes = 128;
constexpr std::uint32_t kMaxProbeStyles = 32;
constexpr std::uint32_t kMaxProbeFamilies = 8192;
constexpr const wchar_t* kLatinSample = L"Hamburgefontsiv 123";
constexpr std::uint32_t kProbeWidth = 320;
constexpr std::uint32_t kProbeHeight = 96;
constexpr float kProbeSize = 32.0f;
constexpr std::uint32_t kSampleWidth = 640;
constexpr std::uint32_t kSampleHeight = 160;

struct Summary {
    std::uint64_t checks = 0;
    std::uint64_t passed = 0;
    std::uint64_t failed = 0;
    std::uint64_t skipped = 0;
};

Summary g_summary;

void record(bool condition, const char* id, std::int32_t hr = 0) {
    ++g_summary.checks;
    if (condition) {
        ++g_summary.passed;
        return;
    }

    ++g_summary.failed;
    std::cerr << "FAIL " << id;
    if (hr != 0) {
        std::cerr << " hr=0x" << std::hex << std::setw(8)
                  << std::setfill('0')
                  << static_cast<std::uint32_t>(hr) << std::dec;
    }
    std::cerr << '\n';
}

bool success(std::int32_t hr, const char* id) {
    const bool ok = hr >= 0;
    record(ok, id, hr);
    return ok;
}

void skip(const char* id) {
    ++g_summary.skipped;
    std::cout << "SKIP " << id << '\n';
}

template <typename T>
bool all_equal(const std::vector<T>& values, T expected) {
    return std::all_of(values.begin(), values.end(),
                       [expected](T value) { return value == expected; });
}

bool copy_bounded(const wchar_t* value, std::size_t limit,
                  std::wstring& output) {
    output.clear();
    if (!value) {
        return false;
    }
    const std::size_t length = wcsnlen(value, limit);
    if (length == limit) {
        return false;
    }
    output.assign(value, length);
    return true;
}

bool is_absolute_windows_path(const std::wstring& path) {
    if (path.size() >= 3 &&
        ((path[0] >= L'A' && path[0] <= L'Z') ||
         (path[0] >= L'a' && path[0] <= L'z')) &&
        path[1] == L':' && (path[2] == L'\\' || path[2] == L'/')) {
        return true;
    }
    return path.size() >= 2 && path[0] == L'\\' && path[1] == L'\\';
}

wchar_t ascii_lower(wchar_t value) {
    if (value >= L'A' && value <= L'Z') {
        return static_cast<wchar_t>(value - L'A' + L'a');
    }
    return value;
}

bool starts_with_ascii_case_insensitive(std::wstring_view value,
                                        std::wstring_view prefix) {
    if (value.size() < prefix.size()) {
        return false;
    }
    for (std::size_t index = 0; index < prefix.size(); ++index) {
        if (ascii_lower(value[index]) != ascii_lower(prefix[index])) {
            return false;
        }
    }
    return true;
}

bool is_bahnschrift_name(const wchar_t* name) {
    std::wstring bounded;
    return copy_bounded(name, 128, bounded) &&
           starts_with_ascii_case_insensitive(bounded, L"Bahnschrift");
}

constexpr std::uint32_t make_tag_be(char a, char b, char c, char d) {
    return (static_cast<std::uint32_t>(static_cast<unsigned char>(a)) << 24) |
           (static_cast<std::uint32_t>(static_cast<unsigned char>(b)) << 16) |
           (static_cast<std::uint32_t>(static_cast<unsigned char>(c)) << 8) |
           static_cast<std::uint32_t>(static_cast<unsigned char>(d));
}

constexpr std::uint32_t make_tag_le(char a, char b, char c, char d) {
    return static_cast<std::uint32_t>(static_cast<unsigned char>(a)) |
           (static_cast<std::uint32_t>(static_cast<unsigned char>(b)) << 8) |
           (static_cast<std::uint32_t>(static_cast<unsigned char>(c)) << 16) |
           (static_cast<std::uint32_t>(static_cast<unsigned char>(d)) << 24);
}

bool is_tag(std::uint32_t value, char a, char b, char c, char d) {
    return value == make_tag_be(a, b, c, d) ||
           value == make_tag_le(a, b, c, d);
}

bool is_weight_axis(const fb_axis& axis) {
    return is_tag(axis.tag, 'w', 'g', 'h', 't');
}

bool is_width_axis(const fb_axis& axis) {
    return is_tag(axis.tag, 'w', 'd', 't', 'h');
}

bool has_variable_target(const std::vector<fb_axis>& axes) {
    for (const fb_axis& axis : axes) {
        if ((is_weight_axis(axis) || is_width_axis(axis)) &&
            axis.minimum < axis.maximum) {
            return true;
        }
    }
    return false;
}

std::uint32_t read_be32(const std::uint8_t* bytes) {
    return (static_cast<std::uint32_t>(bytes[0]) << 24) |
           (static_cast<std::uint32_t>(bytes[1]) << 16) |
           (static_cast<std::uint32_t>(bytes[2]) << 8) |
           static_cast<std::uint32_t>(bytes[3]);
}

struct FileHandle {
    HANDLE value = INVALID_HANDLE_VALUE;

    explicit FileHandle(HANDLE handle) : value(handle) {}
    FileHandle(const FileHandle&) = delete;
    FileHandle& operator=(const FileHandle&) = delete;
    ~FileHandle() {
        if (value != INVALID_HANDLE_VALUE) {
            CloseHandle(value);
        }
    }

    bool valid() const {
        return value != INVALID_HANDLE_VALUE;
    }
};

struct FaceFileInfo {
    bool opened = false;
    bool collection = false;
    std::uint32_t face_count = 0;
};

bool inspect_face_file(const std::wstring& path, std::uint32_t face_index,
                       FaceFileInfo& info) {
    info = {};
    FileHandle file(CreateFileW(
        path.c_str(), GENERIC_READ,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr,
        OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL | FILE_FLAG_SEQUENTIAL_SCAN,
        nullptr));
    if (!file.valid()) {
        return false;
    }

    std::array<std::uint8_t, 12> header{};
    DWORD read = 0;
    if (!ReadFile(file.value, header.data(),
                  static_cast<DWORD>(header.size()), &read, nullptr) ||
        read != static_cast<DWORD>(header.size())) {
        return false;
    }

    info.opened = true;
    const std::uint32_t magic = read_be32(header.data());
    if (magic == make_tag_be('t', 't', 'c', 'f')) {
        info.collection = true;
        info.face_count = read_be32(header.data() + 8);
        return info.face_count > 0 && info.face_count <= 4096 &&
               face_index < info.face_count;
    }

    const bool single_face =
        magic == 0x00010000U || magic == 0x00020000U ||
        magic == make_tag_be('O', 'T', 'T', 'O') ||
        magic == make_tag_be('t', 'r', 'u', 'e') ||
        magic == make_tag_be('t', 'y', 'p', '1');
    if (!single_face) {
        return false;
    }
    info.collection = false;
    info.face_count = 1;
    return face_index == 0;
}

struct SelectionMeta {
    fb_detail detail{};
    std::vector<fb_axis> axes;
    std::wstring path;
};

bool read_axes(fb_selection* selection, std::uint32_t count,
               std::vector<fb_axis>& axes) {
    axes.clear();
    if (count > kMaxAxes) {
        return false;
    }
    axes.resize(count);
    for (std::uint32_t index = 0; index < count; ++index) {
        const std::int32_t hr = fb_axis_at(selection, index, &axes[index]);
        if (hr < 0) {
            axes.clear();
            return false;
        }
    }
    return true;
}

bool capture_selection(fb_selection* selection, SelectionMeta& meta,
                       std::int32_t& error_hr) {
    fb_detail detail{};
    std::int32_t hr = fb_describe(selection, &detail);
    if (hr < 0) {
        error_hr = hr;
        return false;
    }
    if (!read_axes(selection, detail.axes, meta.axes)) {
        error_hr = E_FAIL;
        return false;
    }
    if (!copy_bounded(detail.path, 32768, meta.path)) {
        error_hr = E_FAIL;
        return false;
    }
    meta.detail = detail;
    error_hr = S_OK;
    return true;
}

struct CoreSelection {
    std::uint32_t family_id = 0;
    fb_family_info family{};
    fb_styles* styles = nullptr;
    std::uint32_t style_count = 0;
    fb_style_info style{};
    fb_selection* selection = nullptr;
    SelectionMeta meta;
    FaceFileInfo file_info{};
};

void close_core_selection(CoreSelection& core) {
    if (core.selection) {
        fb_selection_close(core.selection);
        core.selection = nullptr;
    }
    if (core.styles) {
        fb_styles_close(core.styles);
        core.styles = nullptr;
    }
}

bool find_core_selection(fb_catalog* catalog, const fb_stats& stats,
                         CoreSelection& core) {
    std::int32_t last_error = E_FAIL;
    for (std::uint32_t family_id = 0; family_id < stats.families;
         ++family_id) {
        fb_family_info family{};
        std::int32_t hr = fb_family(catalog, family_id, &family);
        if (hr < 0) {
            last_error = hr;
            break;
        }
        if (family.styles == 0) {
            continue;
        }

        fb_styles* styles = nullptr;
        hr = fb_list_styles(catalog, family_id, &styles);
        if (hr < 0 || !styles) {
            last_error = hr < 0 ? hr : E_FAIL;
            continue;
        }
        const std::uint32_t style_count = fb_styles_count(styles);
        const std::uint32_t probe_count =
            std::min(style_count, kMaxProbeStyles);
        for (std::uint32_t slot = 0; slot < probe_count; ++slot) {
            fb_style_info style{};
            hr = fb_style(styles, slot, &style);
            if (hr < 0) {
                last_error = hr;
                break;
            }
            fb_selection* selection = nullptr;
            hr = fb_select(catalog, family_id, style.index, &selection);
            if (hr < 0 || !selection) {
                last_error = hr < 0 ? hr : E_FAIL;
                if (selection) {
                    fb_selection_close(selection);
                }
                continue;
            }

            std::uint32_t latin_scalar = static_cast<std::uint32_t>(L'A');
            std::uint16_t latin_glyph = 0;
            hr = fb_map(selection, &latin_scalar, 1, &latin_glyph);
            if (hr < 0 || latin_glyph == 0) {
                last_error = hr < 0 ? hr : E_FAIL;
                fb_selection_close(selection);
                continue;
            }

            core.family_id = family_id;
            core.family = family;
            core.styles = styles;
            core.style_count = style_count;
            core.style = style;
            core.selection = selection;
            return true;
        }
        fb_styles_close(styles);
    }
    record(false, "selection.available", last_error);
    return false;
}

void test_query_contract(fb_catalog* catalog, const fb_stats& stats) {
    const std::uint32_t total = stats.families;
    record(total > 0, "query.catalog-nonempty");

    std::uint32_t null_count = 0;
    record(fb_query(nullptr, L"", nullptr, 0, &null_count) == E_POINTER,
           "query.null-catalog");
    record(fb_query(catalog, nullptr, nullptr, 0, &null_count) == E_POINTER,
           "query.null-text");
    record(fb_query(catalog, L"", nullptr, 0, nullptr) == E_POINTER,
           "query.null-count");

    const std::uint32_t short_capacity = total ? total - 1 : 0;
    std::vector<std::uint32_t> short_buffer(
        static_cast<std::size_t>(short_capacity) + 4, kQueryGuard);
    std::uint32_t required = 0;
    const std::int32_t short_hr =
        fb_query(catalog, L"", short_buffer.data() + 2, short_capacity,
                 &required);
    record(short_hr < 0, "query.short-buffer-status", short_hr);
    record(required == total, "query.short-buffer-count");
    record(all_equal(short_buffer, kQueryGuard),
           "query.short-buffer-unmodified");

    std::vector<std::uint32_t> exact_buffer(
        static_cast<std::size_t>(total) + 4, kQueryGuard);
    required = 0;
    const std::int32_t exact_hr =
        fb_query(catalog, L"", exact_buffer.data() + 2, total, &required);
    record(exact_hr == S_OK, "query.exact-buffer-status", exact_hr);
    record(required == total, "query.exact-buffer-count");
    record(exact_buffer[0] == kQueryGuard &&
               exact_buffer[1] == kQueryGuard &&
               exact_buffer[exact_buffer.size() - 2] == kQueryGuard &&
               exact_buffer[exact_buffer.size() - 1] == kQueryGuard,
           "query.exact-buffer-guards");

    bool ids_valid = true;
    for (std::uint32_t index = 0; index < total; ++index) {
        fb_family_info family{};
        if (fb_family(catalog, exact_buffer[2 + index], &family) < 0) {
            ids_valid = false;
            break;
        }
    }
    record(ids_valid, "query.ids-in-range");

    std::wstring oversized_query(1025, L'a');
    std::uint32_t oversized_count = 0;
    std::uint32_t untouched_id = kQueryGuard;
    const std::int32_t oversized_hr =
        fb_query(catalog, oversized_query.c_str(), &untouched_id, 1,
                 &oversized_count);
    record(oversized_hr == E_INVALIDARG, "query.oversized-status",
           oversized_hr);
    record(oversized_count == 0, "query.oversized-count");
    record(untouched_id == kQueryGuard, "query.oversized-unmodified");
}

void test_invalid_lookup(fb_catalog* catalog, const fb_stats& stats,
                         CoreSelection& core) {
    fb_family_info invalid_family{};
    record(fb_family(catalog, stats.families, &invalid_family) == E_INVALIDARG,
           "lookup.family-out-of-range");
    record(fb_family(catalog, (std::numeric_limits<std::uint32_t>::max)(),
                     &invalid_family) == E_INVALIDARG,
           "lookup.family-max");

    fb_styles* invalid_styles =
        reinterpret_cast<fb_styles*>(static_cast<std::uintptr_t>(1));
    const std::int32_t styles_hr =
        fb_list_styles(catalog, stats.families, &invalid_styles);
    record(styles_hr == E_INVALIDARG, "lookup.styles-family", styles_hr);
    record(invalid_styles == nullptr, "lookup.styles-no-handle");

    fb_style_info invalid_style{};
    record(fb_style(core.styles, core.style_count, &invalid_style) ==
               E_INVALIDARG,
           "lookup.style-index");

    fb_selection* invalid_selection =
        reinterpret_cast<fb_selection*>(static_cast<std::uintptr_t>(1));
    const std::int32_t family_select_hr =
        fb_select(catalog, stats.families, 0, &invalid_selection);
    record(family_select_hr < 0, "lookup.select-family", family_select_hr);
    record(invalid_selection == nullptr, "lookup.select-family-no-handle");

    const std::int32_t style_select_hr =
        fb_select(catalog, core.family_id, core.family.styles,
                  &invalid_selection);
    record(style_select_hr < 0, "lookup.select-style", style_select_hr);
    record(invalid_selection == nullptr, "lookup.select-style-no-handle");

    fb_detail invalid_detail{};
    record(fb_describe(nullptr, &invalid_detail) == E_POINTER,
           "lookup.describe-null");
    fb_axis invalid_axis{};
    record(fb_axis_at(core.selection, core.meta.detail.axes, &invalid_axis) ==
               E_INVALIDARG,
           "lookup.axis-index");
    record(fb_axis_at(core.selection, 0, nullptr) == E_POINTER,
           "lookup.axis-null-output");
}

bool test_core_metadata(CoreSelection& core, std::wstring& path,
                        FaceFileInfo& file_info) {
    fb_detail first{};
    const std::int32_t first_hr = fb_describe(core.selection, &first);
    if (!success(first_hr, "selection.describe")) {
        return false;
    }
    const bool path_bounded = copy_bounded(first.path, 32768, path);
    record(path_bounded, "selection.path-bounded");
    if (!path_bounded) {
        return false;
    }

    record(!path.empty(), "selection.path-nonempty");
    record(is_absolute_windows_path(path), "selection.path-absolute");
    record(first.glyphs > 0, "selection.glyphs");
    record(first.axes <= kMaxAxes, "selection.axis-count");
    const bool axes_read =
        read_axes(core.selection, first.axes, core.meta.axes);
    record(axes_read, "selection.axes");
    if (!axes_read) {
        return false;
    }
    core.meta.detail = first;
    core.meta.path = path;

    bool axis_ranges_valid = true;
    for (const fb_axis& axis : core.meta.axes) {
        if (!std::isfinite(axis.minimum) ||
            !std::isfinite(axis.initial) ||
            !std::isfinite(axis.maximum) ||
            axis.minimum > axis.initial || axis.initial > axis.maximum) {
            axis_ranges_valid = false;
            break;
        }
    }
    record(axis_ranges_valid, "selection.axis-ranges");

    fb_detail second{};
    const std::int32_t second_hr = fb_describe(core.selection, &second);
    if (!success(second_hr, "selection.describe-repeat")) {
        return false;
    }
    std::wstring repeated_path;
    const bool repeated_path_ok =
        copy_bounded(second.path, 32768, repeated_path);
    record(repeated_path_ok, "selection.path-repeat-bounded");
    record(repeated_path_ok && repeated_path == path,
           "selection.path-stable");
    record(second.face_index == first.face_index,
           "selection.face-index-stable");

    file_info = {};
    const bool file_face_ok =
        inspect_face_file(path, first.face_index, file_info);
    record(file_face_ok, "selection.path-face-index");
    return file_face_ok;
}

void test_file_basket(const std::wstring& path, std::uint32_t face_count) {
    using CatalogOwner = std::unique_ptr<fb_catalog, decltype(&fb_close)>;

    fb_catalog* empty = nullptr;
    const auto empty_hr = fb_open_files(L"en-us", nullptr, 0, nullptr,
                                        nullptr, &empty);
    const CatalogOwner empty_snapshot(empty, fb_close);
    record(empty_hr >= 0 && empty, "files.empty-open", empty_hr);
    if (empty_hr >= 0 && empty) {
        fb_stats stats{};
        const auto hr = fb_statistics(empty, &stats);
        record(hr >= 0 && stats.families == 0 && stats.styles == 0 &&
                   stats.skipped == 0,
               "files.empty-snapshot", hr);
    }

    const auto expect_invalid = [](const wchar_t* const* paths,
                                   std::uint32_t count, const char* id) {
        fb_catalog* rejected = nullptr;
        const auto hr = fb_open_files(L"en-us", paths, count, nullptr,
                                      nullptr, &rejected);
        const CatalogOwner unexpected_snapshot(rejected, fb_close);
        record(hr == E_INVALIDARG && !rejected, id, hr);
    };
    const wchar_t* const relative[] = {L".\\relative.ttf"};
    const wchar_t* const duplicate[] = {path.c_str(), path.c_str()};
    const wchar_t* const null_element[] = {path.c_str(), nullptr};
    expect_invalid(relative, 1, "files.relative-path");
    expect_invalid(duplicate, 2, "files.duplicate-path");
    expect_invalid(null_element, 2, "files.null-element");

    const wchar_t* const one_path[] = {path.c_str()};
    fb_catalog* files = nullptr;
    auto hr = fb_open_files(L"en-us", one_path, 1, nullptr, nullptr, &files);
    const CatalogOwner snapshot(files, fb_close);
    record(hr >= 0 && files, "files.single-open", hr);
    if (hr < 0 || !files) {
        return;
    }

    fb_styles* styles = nullptr;
    hr = fb_list_styles(files, 0, &styles);
    const std::unique_ptr<fb_styles, decltype(&fb_styles_close)>
        style_list(styles, fb_styles_close);
    fb_style_info style{};
    const bool has_styles = hr >= 0 && styles && fb_styles_count(styles) > 0;
    if (has_styles) {
        hr = fb_style(styles, 0, &style);
    }
    record(has_styles && hr >= 0, "files.single-list-style", hr);
    if (!has_styles || hr < 0) {
        return;
    }

    fb_selection* selected = nullptr;
    hr = fb_select(files, 0, style.index, &selected);
    const std::unique_ptr<fb_selection, decltype(&fb_selection_close)>
        selection(selected, fb_selection_close);
    fb_detail detail{};
    if (hr >= 0 && selected) {
        hr = fb_describe(selected, &detail);
    }
    // In a TTC the first listed style need not be the original core face.
    record(hr >= 0 && selected && detail.path &&
               CompareStringOrdinal(path.c_str(), -1, detail.path, -1, TRUE) ==
                   CSTR_EQUAL && detail.face_index < face_count,
           "files.single-select-supplied-file", hr);
}

struct GuardedImage {
    std::vector<std::uint8_t> storage;
    std::size_t byte_count = 0;

    std::uint8_t* data() {
        return storage.data() + kBufferGuard;
    }
    const std::uint8_t* data() const {
        return storage.data() + kBufferGuard;
    }
};

std::size_t pixel_byte_count(std::uint32_t width, std::uint32_t height,
                             bool& ok) {
    const std::uint64_t pixels =
        static_cast<std::uint64_t>(width) * height;
    const std::uint64_t bytes = pixels * 4;
    ok = bytes <= (std::numeric_limits<std::uint32_t>::max)();
    return ok ? static_cast<std::size_t>(bytes) : 0;
}

bool image_has_ink_and_transparency(const GuardedImage& image,
                                    bool& has_ink, bool& has_transparency) {
    has_ink = false;
    has_transparency = false;
    for (std::size_t offset = 0; offset < image.byte_count; offset += 4) {
        const std::uint8_t alpha = image.data()[offset + 3];
        has_ink = has_ink || alpha != 0;
        has_transparency = has_transparency || alpha == 0;
    }
    return has_ink && has_transparency;
}

bool render_checked(fb_selection* selection, const wchar_t* text, float size,
                    float scale, std::uint32_t width, std::uint32_t height,
                    const float* axes, std::uint32_t axis_count,
                    GuardedImage& image, const char* status_id,
                    const char* pixels_id) {
    bool byte_count_ok = false;
    image.byte_count = pixel_byte_count(width, height, byte_count_ok);
    record(byte_count_ok, "render.checked-dimensions");
    if (!byte_count_ok) {
        return false;
    }

    image.storage.assign(image.byte_count + kBufferGuard * 2, kGuardByte);
    const std::int32_t hr = fb_render(
        selection, text, size, scale, width, height, axes, axis_count,
        image.data(), static_cast<std::uint32_t>(image.byte_count));
    success(hr, status_id);
    const bool guards_ok =
        std::all_of(image.storage.begin(),
                    image.storage.begin() + kBufferGuard,
                    [](std::uint8_t value) { return value == kGuardByte; }) &&
        std::all_of(image.storage.end() - kBufferGuard, image.storage.end(),
                    [](std::uint8_t value) { return value == kGuardByte; });
    record(guards_ok, "render.checked-guards");
    if (hr < 0 || !guards_ok) {
        return false;
    }

    bool has_ink = false;
    bool has_transparency = false;
    image_has_ink_and_transparency(image, has_ink, has_transparency);
    record(has_ink, pixels_id);
    record(has_transparency, "render.background-transparent");
    return has_ink && has_transparency;
}

bool expect_render_failure(fb_selection* selection, const wchar_t* text,
                           float size, float scale, std::uint32_t width,
                           std::uint32_t height, const float* axes,
                           std::uint32_t axis_count,
                           std::uint32_t byte_count, const char* id) {
    std::vector<std::uint8_t> buffer(
        static_cast<std::size_t>(byte_count) + kBufferGuard * 2, kGuardByte);
    const std::int32_t hr = fb_render(
        selection, text, size, scale, width, height, axes, axis_count,
        buffer.data() + kBufferGuard, byte_count);
    record(hr < 0, id, hr);
    const bool unchanged = all_equal(buffer, kGuardByte);
    record(unchanged, "render.invalid-input-unmodified");
    return hr < 0 && unchanged;
}

bool images_differ(const GuardedImage& first, const GuardedImage& second) {
    if (first.byte_count != second.byte_count) {
        return true;
    }
    return !std::equal(first.data(), first.data() + first.byte_count,
                       second.data());
}

std::int32_t probe_render(fb_selection* selection, bool& has_ink,
                          bool& has_transparency) {
    has_ink = false;
    has_transparency = false;
    bool bytes_ok = false;
    const std::size_t bytes =
        pixel_byte_count(kProbeWidth, kProbeHeight, bytes_ok);
    if (!bytes_ok) {
        return E_INVALIDARG;
    }
    std::vector<std::uint8_t> pixels(bytes, 0);
    const std::int32_t hr = fb_render(
        selection, kLatinSample, kProbeSize, 1.0f, kProbeWidth,
        kProbeHeight, nullptr, 0, pixels.data(),
        static_cast<std::uint32_t>(pixels.size()));
    if (hr < 0) {
        return hr;
    }
    for (std::size_t offset = 0; offset < pixels.size(); offset += 4) {
        const std::uint8_t alpha = pixels[offset + 3];
        has_ink = has_ink || alpha != 0;
        has_transparency = has_transparency || alpha == 0;
    }
    return S_OK;
}

void test_render_contract(fb_selection* selection, const SelectionMeta& meta) {
    GuardedImage sample;
    const bool sample_ok = render_checked(
        selection, kLatinSample, kProbeSize, 1.0f, kSampleWidth,
        kSampleHeight, nullptr, 0, sample, "render.sample",
        "render.sample-ink");
    record(sample_ok, "render.sample-complete");

    std::vector<float> initial_values(meta.axes.size(), 0.0f);
    for (std::size_t index = 0; index < meta.axes.size(); ++index) {
        initial_values[index] = meta.axes[index].initial;
    }

    expect_render_failure(selection, kLatinSample, kProbeSize, 1.0f, 8, 8,
                          nullptr, 1, 256, "render.axes-null");

    std::vector<float> wrong_count = initial_values;
    wrong_count.push_back(0.0f);
    expect_render_failure(selection, kLatinSample, kProbeSize, 1.0f, 8, 8,
                          wrong_count.data(),
                          static_cast<std::uint32_t>(wrong_count.size()), 256,
                          "render.axes-count");

    std::wstring oversized_text(2049, L'A');
    expect_render_failure(selection, oversized_text.c_str(), kProbeSize, 1.0f,
                          8, 8, nullptr, 0, 256, "render.text-limit");
    expect_render_failure(selection, nullptr, kProbeSize, 1.0f, 8, 8, nullptr,
                          0, 256, "render.text-null");
    expect_render_failure(selection, kLatinSample, kProbeSize, 1.0f, 0, 8,
                          nullptr, 0, 64, "render.width-zero");
    expect_render_failure(selection, kLatinSample, kProbeSize, 1.0f, 8, 0,
                          nullptr, 0, 64, "render.height-zero");
    expect_render_failure(selection, kLatinSample, kProbeSize, 1.0f, 4097, 1,
                          nullptr, 0, 64, "render.width-limit");
    expect_render_failure(selection, kLatinSample, kProbeSize, 1.0f, 1, 4097,
                          nullptr, 0, 64, "render.height-limit");
    expect_render_failure(selection, kLatinSample, kProbeSize, 1.0f, 2049,
                          2048, nullptr, 0, 64, "render.pixel-area-limit");
    expect_render_failure(selection, kLatinSample, 0.0f, 1.0f, 8, 8, nullptr,
                          0, 256, "render.size-zero");
    expect_render_failure(selection, kLatinSample, -1.0f, 1.0f, 8, 8, nullptr,
                          0, 256, "render.size-negative");
    expect_render_failure(
        selection, kLatinSample,
        (std::numeric_limits<float>::quiet_NaN)(), 1.0f, 8, 8, nullptr, 0,
        256, "render.size-nan");
    expect_render_failure(selection, kLatinSample, kProbeSize, 0.0f, 8, 8,
                          nullptr, 0, 256, "render.scale-zero");
    expect_render_failure(selection, kLatinSample, kProbeSize, 0.49f, 8, 8,
                          nullptr, 0, 256, "render.scale-low");
    expect_render_failure(
        selection, kLatinSample, kProbeSize,
        (std::numeric_limits<float>::quiet_NaN)(), 8, 8, nullptr, 0, 256,
        "render.scale-nan");
    expect_render_failure(selection, kLatinSample, kProbeSize, 1.0f, 16, 16,
                          nullptr, 0, 1023, "render.byte-count");

    std::vector<std::uint8_t> pixel_storage(
        static_cast<std::size_t>(16 * 16 * 4), kGuardByte);
    const std::int32_t null_pixels_hr = fb_render(
        selection, kLatinSample, kProbeSize, 1.0f, 16, 16, nullptr, 0,
        nullptr, static_cast<std::uint32_t>(pixel_storage.size()));
    record(null_pixels_hr == E_POINTER, "render.pixels-null",
           null_pixels_hr);
    record(all_equal(pixel_storage, kGuardByte),
           "render.pixels-null-unmodified");

    if (!meta.axes.empty()) {
        std::vector<float> nan_axes = initial_values;
        nan_axes[0] = (std::numeric_limits<float>::quiet_NaN)();
        expect_render_failure(selection, kLatinSample, kProbeSize, 1.0f, 8, 8,
                              nan_axes.data(),
                              static_cast<std::uint32_t>(nan_axes.size()), 256,
                              "render.axis-nan");

        std::vector<float> below_axes = initial_values;
        below_axes[0] = std::nextafter(
            meta.axes[0].minimum,
            -(std::numeric_limits<float>::infinity)());
        expect_render_failure(selection, kLatinSample, kProbeSize, 1.0f, 8, 8,
                              below_axes.data(),
                              static_cast<std::uint32_t>(below_axes.size()),
                              256, "render.axis-below-range");

        std::vector<float> above_axes = initial_values;
        above_axes[0] = std::nextafter(
            meta.axes[0].maximum,
            (std::numeric_limits<float>::infinity)());
        expect_render_failure(selection, kLatinSample, kProbeSize, 1.0f, 8, 8,
                              above_axes.data(),
                              static_cast<std::uint32_t>(above_axes.size()),
                              256, "render.axis-above-range");
    }
}

void test_glyph_book(fb_selection* sel, uint32_t total) {
    static_assert(sizeof(fb_glyph_info)==16, "glyph metadata ABI mismatch");
    fb_glyph_book* raw=nullptr;
    auto stop=[](void*)->int{return 1;};
    record(fb_glyph_book_open(sel,stop,nullptr,&raw)==E_ABORT && raw==nullptr,"glyph.cancel-before");
    if(!success(fb_glyph_book_open(sel,nullptr,nullptr,&raw),"glyph.open"))return;
    std::unique_ptr<fb_glyph_book,decltype(&fb_glyph_book_close)> book(raw,fb_glyph_book_close);
    fb_glyph_info info{};
    record(fb_glyph_info_at(raw,0,&info)==S_OK && info.mapping_count==0 && info.first_scalar==UINT32_MAX,"glyph.zero-is-not-unicode");
    record(fb_glyph_info_at(raw,total,&info)==E_INVALIDARG,"glyph.limit");
    const int status=fb_glyph_name_status(raw);record(status>=0&&status<=2,"glyph.name-status");
    uint32_t cp=65;uint16_t gid=0;
    if(!success(fb_map(sel,&cp,1,&gid),"glyph.forward")||!gid){record(false,"glyph.latin-fixture");return;}
    if(!success(fb_glyph_info_at(raw,gid,&info),"glyph.inspect"))return;
    bool contains=false, sorted=true;uint32_t previous=0;
    for(uint32_t i=0;i<info.mapping_count;i++){
        uint32_t x=0;if(fb_glyph_scalar_at(raw,gid,i,&x)!=S_OK || (i&&x<=previous) || (!i&&x!=info.first_scalar))sorted=false;
        contains|=x==cp;previous=x;
    }
    record(contains&&sorted,"glyph.roundtrip-and-sorted-aliases");
    record(fb_glyph_scalar_at(raw,gid,info.mapping_count,&previous)==E_INVALIDARG,"glyph.alias-limit");
    constexpr uint32_t side=96, bytes=side*side*4;
    std::vector<uint8_t> image(bytes+8,kGuardByte);
    record(fb_render_glyph(sel,gid,side,side,nullptr,0,image.data()+4,bytes)==S_OK,"glyph.draw-by-id");
    bool ink=false;for(uint32_t i=7;i<bytes+4;i+=4)ink|=image[i]!=0;
    record(ink && image[0]==kGuardByte && image[3]==kGuardByte && image[bytes+4]==kGuardByte,"glyph.visible-and-guarded");
    record(fb_render_glyph(sel,total,side,side,nullptr,0,image.data(),bytes)==E_INVALIDARG,"glyph.reject-bad-id");
    record(fb_render_glyph(sel,gid,side,side,nullptr,0,image.data(),bytes-1)==E_INVALIDARG,"glyph.reject-short-buffer");
    record(fb_render_glyph(sel,gid,513,side,nullptr,0,image.data(),bytes)==E_INVALIDARG,"glyph.reject-size");
}

void test_glyph_mapping(fb_selection* selection) {
    std::uint32_t scalar = static_cast<std::uint32_t>(L'A');
    std::uint16_t glyph = 0xFFFF;
    record(fb_map(selection, &scalar, 1, &glyph) == S_OK, "map.valid");
    record(glyph != 0, "map.valid-glyph");
    record(fb_map(selection, nullptr, 0, nullptr) == S_OK, "map.empty");
    record(fb_map(selection, nullptr, 1, &glyph) == E_POINTER,
           "map.null-input");
    record(fb_map(selection, &scalar, 1, nullptr) == E_POINTER,
           "map.null-output");

    const std::array<std::uint32_t, 2> invalid_scalars = {0xD800U, 0x110000U};
    for (std::uint32_t invalid : invalid_scalars) {
        std::uint16_t untouched = 0xFFFF;
        const std::int32_t hr = fb_map(selection, &invalid, 1, &untouched);
        record(hr == E_INVALIDARG, "map.invalid-scalar", hr);
        record(untouched == 0xFFFF, "map.invalid-unmodified");
    }

    std::vector<std::uint32_t> too_many(2049, static_cast<std::uint32_t>(L'A'));
    std::vector<std::uint16_t> untouched_glyphs(too_many.size(), 0xFFFF);
    const std::int32_t count_hr =
        fb_map(selection, too_many.data(),
               static_cast<std::uint32_t>(too_many.size()),
               untouched_glyphs.data());
    record(count_hr == E_INVALIDARG, "map.count-limit", count_hr);
    record(std::all_of(untouched_glyphs.begin(), untouched_glyphs.end(),
                       [](std::uint16_t value) { return value == 0xFFFF; }),
           "map.count-limit-unmodified");
}

struct VariableCandidate {
    fb_selection* selection = nullptr;
    SelectionMeta meta;

    void close() {
        if (selection) {
            fb_selection_close(selection);
            selection = nullptr;
        }
    }
    ~VariableCandidate() {
        close();
    }
};

enum class ProbeStatus {
    NotApplicable,
    Found,
    Error,
};

ProbeStatus probe_variable_family(fb_catalog* catalog, std::uint32_t family_id,
                                  VariableCandidate& candidate,
                                  std::int32_t& error_hr) {
    error_hr = S_OK;
    fb_styles* styles = nullptr;
    std::int32_t hr = fb_list_styles(catalog, family_id, &styles);
    if (hr < 0 || !styles) {
        error_hr = hr < 0 ? hr : E_FAIL;
        return ProbeStatus::Error;
    }

    const std::uint32_t style_count = fb_styles_count(styles);
    const std::uint32_t probe_count =
        std::min(style_count, kMaxProbeStyles);
    std::int32_t last_select_error = S_OK;
    for (std::uint32_t slot = 0; slot < probe_count; ++slot) {
        fb_style_info style{};
        hr = fb_style(styles, slot, &style);
        if (hr < 0) {
            error_hr = hr;
            fb_styles_close(styles);
            return ProbeStatus::Error;
        }

        fb_selection* selection = nullptr;
        hr = fb_select(catalog, family_id, style.index, &selection);
        if (hr < 0 || !selection) {
            last_select_error = hr < 0 ? hr : E_FAIL;
            if (selection) {
                fb_selection_close(selection);
            }
            continue;
        }

        SelectionMeta meta;
        std::int32_t meta_error = S_OK;
        if (!capture_selection(selection, meta, meta_error)) {
            error_hr = meta_error;
            fb_selection_close(selection);
            fb_styles_close(styles);
            return ProbeStatus::Error;
        }
        if (!has_variable_target(meta.axes)) {
            fb_selection_close(selection);
            continue;
        }

        std::uint32_t latin_scalar = static_cast<std::uint32_t>(L'A');
        std::uint16_t latin_glyph = 0;
        hr = fb_map(selection, &latin_scalar, 1, &latin_glyph);
        if (hr < 0) {
            error_hr = hr;
            fb_selection_close(selection);
            fb_styles_close(styles);
            return ProbeStatus::Error;
        }
        if (latin_glyph == 0) {
            fb_selection_close(selection);
            continue;
        }

        bool has_ink = false;
        bool has_transparency = false;
        hr = probe_render(selection, has_ink, has_transparency);
        if (hr < 0 || !has_transparency) {
            error_hr = hr < 0 ? hr : E_FAIL;
            fb_selection_close(selection);
            fb_styles_close(styles);
            return ProbeStatus::Error;
        }
        if (!has_ink) {
            fb_selection_close(selection);
            continue;
        }

        candidate.selection = selection;
        candidate.meta = std::move(meta);
        fb_styles_close(styles);
        return ProbeStatus::Found;
    }

    fb_styles_close(styles);
    if (last_select_error < 0) {
        error_hr = last_select_error;
        return ProbeStatus::Error;
    }
    return ProbeStatus::NotApplicable;
}

bool find_variable_candidate(fb_catalog* catalog, const fb_stats& stats,
                             VariableCandidate& candidate) {
    bool had_error = false;
    std::int32_t last_error = S_OK;

    for (std::uint32_t family_id = 0; family_id < stats.families;
         ++family_id) {
        fb_family_info family{};
        const std::int32_t hr = fb_family(catalog, family_id, &family);
        if (hr < 0) {
            record(false, "variable.family", hr);
            return false;
        }
        if (!is_bahnschrift_name(family.name)) {
            continue;
        }
        std::int32_t probe_error = S_OK;
        const ProbeStatus status =
            probe_variable_family(catalog, family_id, candidate, probe_error);
        if (status == ProbeStatus::Found) {
            return true;
        }
        if (status == ProbeStatus::Error) {
            had_error = true;
            last_error = probe_error;
        }
    }

    const std::uint32_t scan_limit =
        std::min(stats.families, kMaxProbeFamilies);
    for (std::uint32_t family_id = 0; family_id < scan_limit; ++family_id) {
        fb_family_info family{};
        const std::int32_t hr = fb_family(catalog, family_id, &family);
        if (hr < 0) {
            record(false, "variable.family-scan", hr);
            return false;
        }
        if (family.styles == 0 || is_bahnschrift_name(family.name)) {
            continue;
        }

        std::int32_t probe_error = S_OK;
        const ProbeStatus status =
            probe_variable_family(catalog, family_id, candidate, probe_error);
        if (status == ProbeStatus::Found) {
            return true;
        }
        if (status == ProbeStatus::Error) {
            had_error = true;
            last_error = probe_error;
        }
    }

    if (had_error) {
        record(false, "variable.probe", last_error);
    } else {
        skip("variable-axes");
    }
    return false;
}

void test_variable_axes(VariableCandidate& candidate) {
    std::vector<float> values(candidate.meta.axes.size(), 0.0f);
    for (std::size_t index = 0; index < candidate.meta.axes.size(); ++index) {
        values[index] = candidate.meta.axes[index].initial;
    }

    bool compared_any = false;
    for (std::size_t index = 0; index < candidate.meta.axes.size(); ++index) {
        const fb_axis& axis = candidate.meta.axes[index];
        if ((!is_weight_axis(axis) && !is_width_axis(axis)) ||
            axis.minimum >= axis.maximum) {
            continue;
        }

        std::vector<float> minimum_values = values;
        std::vector<float> maximum_values = values;
        minimum_values[index] = axis.minimum;
        maximum_values[index] = axis.maximum;

        GuardedImage minimum_image;
        GuardedImage maximum_image;
        const bool minimum_ok = render_checked(
            candidate.selection, kLatinSample, kProbeSize, 1.0f,
            kSampleWidth, kSampleHeight, minimum_values.data(),
            static_cast<std::uint32_t>(minimum_values.size()),
            minimum_image, "render.variable-minimum",
            "render.variable-minimum-ink");
        const bool maximum_ok = render_checked(
            candidate.selection, kLatinSample, kProbeSize, 1.0f,
            kSampleWidth, kSampleHeight, maximum_values.data(),
            static_cast<std::uint32_t>(maximum_values.size()),
            maximum_image, "render.variable-maximum",
            "render.variable-maximum-ink");
        record(minimum_ok && maximum_ok, "render.variable-endpoints");
        if (minimum_ok && maximum_ok) {
            record(images_differ(minimum_image, maximum_image),
                   "render.variable-pixels-change");
        }
        uint32_t ch='H';uint16_t g=0;constexpr uint32_t side=96, bytes=side*side*4;
        std::vector<uint8_t> low(bytes),high(bytes);
        if(fb_map(candidate.selection,&ch,1,&g)==S_OK && g){
            record(fb_render_glyph(candidate.selection,g,side,side,minimum_values.data(),static_cast<uint32_t>(values.size()),low.data(),bytes)==S_OK &&
                   fb_render_glyph(candidate.selection,g,side,side,maximum_values.data(),static_cast<uint32_t>(values.size()),high.data(),bytes)==S_OK && low!=high,
                   "glyph.variable-outline-changes");
        }else record(false,"glyph.variable-H-lookup");
        compared_any = true;
    }
    record(compared_any, "render.variable-axis-applied");

    if (!candidate.meta.axes.empty()) {
        std::vector<float> nan_values = values;
        nan_values[0] = (std::numeric_limits<float>::quiet_NaN)();
        expect_render_failure(
            candidate.selection, kLatinSample, kProbeSize, 1.0f, 8, 8,
            nan_values.data(),
            static_cast<std::uint32_t>(nan_values.size()), 256,
            "render.variable-axis-nan");

        std::vector<float> outside_values = values;
        outside_values[0] = std::nextafter(
            candidate.meta.axes[0].maximum,
            (std::numeric_limits<float>::infinity)());
        expect_render_failure(
            candidate.selection, kLatinSample, kProbeSize, 1.0f, 8, 8,
            outside_values.data(),
            static_cast<std::uint32_t>(outside_values.size()), 256,
            "render.variable-axis-range");
    }
}

void test_ttc_face_index(fb_catalog* catalog, const fb_stats& stats,
                         const FaceFileInfo& core_file_info) {
    if (core_file_info.collection) {
        record(core_file_info.face_count > 1,
               "ttc.collection-has-multiple-faces");
        return;
    }

    bool had_error = false;
    std::int32_t last_error = S_OK;
    const std::uint32_t scan_limit =
        std::min(stats.families, kMaxProbeFamilies);
    for (std::uint32_t family_id = 0; family_id < scan_limit; ++family_id) {
        fb_family_info family{};
        std::int32_t hr = fb_family(catalog, family_id, &family);
        if (hr < 0) {
            had_error = true;
            last_error = hr;
            continue;
        }
        if (family.styles == 0) {
            continue;
        }

        fb_styles* styles = nullptr;
        hr = fb_list_styles(catalog, family_id, &styles);
        if (hr < 0 || !styles) {
            had_error = true;
            last_error = hr < 0 ? hr : E_FAIL;
            continue;
        }
        const std::uint32_t style_count = fb_styles_count(styles);
        const std::uint32_t probe_count =
            std::min(style_count, kMaxProbeStyles);
        for (std::uint32_t slot = 0; slot < probe_count; ++slot) {
            fb_style_info style{};
            hr = fb_style(styles, slot, &style);
            if (hr < 0) {
                had_error = true;
                last_error = hr;
                break;
            }

            fb_selection* selection = nullptr;
            hr = fb_select(catalog, family_id, style.index, &selection);
            if (hr < 0 || !selection) {
                had_error = true;
                last_error = hr < 0 ? hr : E_FAIL;
                if (selection) {
                    fb_selection_close(selection);
                }
                continue;
            }

            fb_detail detail{};
            hr = fb_describe(selection, &detail);
            std::wstring path;
            const bool described =
                hr >= 0 && copy_bounded(detail.path, 32768, path);
            FaceFileInfo info{};
            const bool inspected =
                described &&
                inspect_face_file(path, detail.face_index, info);

            if (!described || (!inspected && !info.opened)) {
                had_error = true;
                last_error = hr < 0 ? hr : E_FAIL;
                fb_selection_close(selection);
                continue;
            }
            if (info.collection) {
                if (!inspected) {
                    had_error = true;
                    last_error = E_FAIL;
                    fb_selection_close(selection);
                    continue;
                }
                record(detail.face_index < info.face_count,
                       "ttc.face-index");
                record(info.face_count > 1, "ttc.multiple-faces");
                fb_selection_close(selection);
                fb_styles_close(styles);
                return;
            }
            fb_selection_close(selection);
        }
        fb_styles_close(styles);
    }

    if (had_error) {
        record(false, "ttc.probe", last_error);
    } else {
        skip("ttc-face-index");
    }
}

void test_catalog_recreation(fb_catalog* catalog, const fb_stats& stats) {
    fb_catalog* second = nullptr;
    std::int32_t hr = fb_open(L"en-us", nullptr, nullptr, &second);
    record(hr == S_OK && second != nullptr, "recreate.second-open", hr);
    if (hr == S_OK && second) {
        fb_stats second_stats{};
        hr = fb_statistics(second, &second_stats);
        record(hr == S_OK, "recreate.statistics", hr);
        record(second != catalog, "recreate.distinct-handle");
        record(second_stats.families == stats.families,
               "recreate.same-family-count");
        fb_close(second);
        record(true, "recreate.second-close");
    }
}

int finish(const std::chrono::steady_clock::time_point& started) {
    const double elapsed_ms =
        std::chrono::duration<double, std::milli>(
            std::chrono::steady_clock::now() - started)
            .count();
    std::cout << (g_summary.failed == 0 ? "PASS" : "FAIL")
              << " checks=" << g_summary.checks
              << " passed=" << g_summary.passed
              << " failed=" << g_summary.failed
              << " skipped=" << g_summary.skipped
              << " elapsed_ms=" << std::fixed << std::setprecision(3)
              << elapsed_ms << '\n';
    return g_summary.failed == 0 ? EXIT_SUCCESS : EXIT_FAILURE;
}

int cancellation_callback(void* context) {
    auto* observed = static_cast<int*>(context);
    if (observed) {
        *observed = 1;
    }
    return 1;
}

}  // namespace

int main() {
    const auto started = std::chrono::steady_clock::now();

    record(fb_version() == 3, "abi.version");
    record(fb_statistics(nullptr, nullptr) == E_POINTER,
           "abi.statistics-null");

    fb_catalog* canceled =
        reinterpret_cast<fb_catalog*>(static_cast<std::uintptr_t>(1));
    int cancellation_observed = 0;
    const std::int32_t cancel_hr =
        fb_open(L"en-us", cancellation_callback, &cancellation_observed,
                &canceled);
    record(cancel_hr == E_ABORT, "open.cancel-status", cancel_hr);
    record(canceled == nullptr, "open.cancel-no-handle");
    record(cancellation_observed == 1, "open.cancel-called");

    fb_catalog* invalid_result =
        reinterpret_cast<fb_catalog*>(static_cast<std::uintptr_t>(1));
    record(fb_open(nullptr, nullptr, nullptr, &invalid_result) == E_INVALIDARG,
           "open.null-locale");
    record(invalid_result == nullptr, "open.null-locale-no-handle");
    record(fb_open(L"en-us", nullptr, nullptr, nullptr) == E_POINTER,
           "open.null-result");

    fb_catalog* catalog = nullptr;
    const std::int32_t open_hr =
        fb_open(L"en-us", nullptr, nullptr, &catalog);
    if (!success(open_hr, "open.valid") || !catalog) {
        record(false, "open.valid-handle");
        return finish(started);
    }

    fb_stats stats{};
    if (!success(fb_statistics(catalog, &stats), "statistics.valid") ||
        stats.families == 0) {
        record(false, "statistics.nonempty");
        fb_close(catalog);
        return finish(started);
    }
    record(stats.styles >= stats.families, "statistics.style-count");

    test_query_contract(catalog, stats);

    CoreSelection core;
    if (!find_core_selection(catalog, stats, core)) {
        fb_close(catalog);
        return finish(started);
    }

    record(core.style_count > 0, "selection.styles-nonempty");
    std::wstring style_name;
    const bool style_name_ok =
        copy_bounded(core.style.name, 4096, style_name) && !style_name.empty();
    record(style_name_ok, "selection.style-name");
    record(core.style.weight > 0, "selection.style-weight");
    record(core.style.index < core.family.styles, "selection.style-index");

    std::wstring path;
    if (!test_core_metadata(core, path, core.file_info)) {
        close_core_selection(core);
        fb_close(catalog);
        return finish(started);
    }

    {
        fb_sources* list=nullptr;
        record(fb_sources_open(catalog, stats.families, &list)==E_INVALIDARG && list==nullptr, "sources.invalid-family");
        if(success(fb_sources_open(catalog, core.family_id, &list), "sources.open")) {
            const uint32_t count=fb_sources_count(list);
            record(count==core.style_count, "sources.all-style-references");
            const wchar_t* actual=nullptr; uint32_t index=UINT32_MAX;
            const int32_t hr=fb_source_at(list,core.style.index,&actual,&index);
            record(hr==S_OK && actual && CompareStringOrdinal(actual,-1,path.c_str(),-1,TRUE)==CSTR_EQUAL && index==core.meta.detail.face_index,"sources.match-selected-physical-face");
            record(fb_source_at(list,count,&actual,&index)==E_INVALIDARG,"sources.ordinal-limit");
            fb_sources_close(list);
        }
    }
    test_file_basket(path, core.file_info.face_count);
    test_invalid_lookup(catalog, stats, core);
    test_glyph_mapping(core.selection);
    test_glyph_book(core.selection,core.meta.detail.glyphs);
    test_render_contract(core.selection, core.meta);

    VariableCandidate variable;
    if (find_variable_candidate(catalog, stats, variable) &&
        variable.selection) {
        test_variable_axes(variable);
    }
    variable.close();

    test_ttc_face_index(catalog, stats, core.file_info);
    test_catalog_recreation(catalog, stats);

    close_core_selection(core);
    record(true, "release.selection-and-styles");
    fb_close(catalog);
    record(true, "release.catalog");

    fb_catalog* recreated = nullptr;
    const std::int32_t recreate_hr =
        fb_open(L"en-us", nullptr, nullptr, &recreated);
    record(recreate_hr == S_OK && recreated != nullptr,
           "recreate.after-release", recreate_hr);
    if (recreated) {
        fb_close(recreated);
        record(true, "recreate.after-release-close");
    }

    return finish(started);
}

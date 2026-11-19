#define NOMINMAX
#include <dwrite.h>
#include <windows.h>

#include "editor_layout.h"
#include <algorithm>
#include <cmath>
#include <future>
#include <initializer_list>
#include <iostream>
#include <limits>
#include <memory>
#include <stdexcept>
#include <string>
#include <vector>

namespace {
using Catalog = std::unique_ptr<fb_catalog, decltype(&fb_close)>;
using Styles = std::unique_ptr<fb_styles, decltype(&fb_styles_close)>;
using Face = std::unique_ptr<fb_selection, decltype(&fb_selection_close)>;
using Layout = std::unique_ptr<fb_layout, decltype(&fb_layout_close)>;
using Pixels = std::vector<uint8_t>;

constexpr auto insufficient = HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);
constexpr auto liga = DWRITE_MAKE_OPENTYPE_TAG('l', 'i', 'g', 'a');
constexpr auto kern = DWRITE_MAKE_OPENTYPE_TAG('k', 'e', 'r', 'n');
constexpr auto pnum = DWRITE_MAKE_OPENTYPE_TAG('p', 'n', 'u', 'm');
constexpr auto tnum = DWRITE_MAKE_OPENTYPE_TAG('t', 'n', 'u', 'm');
constexpr auto onum = DWRITE_MAKE_OPENTYPE_TAG('o', 'n', 'u', 'm');

unsigned checks = 0;
void expect(bool condition, const char* label) {
    ++checks;
    if (!condition)
        throw std::runtime_error(label);
}
void status(int32_t actual, const char* label, int32_t expected = S_OK) {
    if (actual != expected)
        std::cerr << label << ": got 0x" << std::hex << static_cast<uint32_t>(actual) << ", expected 0x"
                  << static_cast<uint32_t>(expected) << std::dec << '\n';
    expect(actual == expected, label);
}

// Reopen the real SFNT as a private catalog, select the exact file/index and release the catalog.
Face select_file(const wchar_t* name, uint32_t index = 0) {
    wchar_t windows[MAX_PATH]{};
    const auto n = GetWindowsDirectoryW(windows, MAX_PATH);
    expect(n > 0 && n < MAX_PATH, "Windows directory");
    const std::wstring path = std::wstring(windows) + L"\\Fonts\\" + name;
    const wchar_t* paths[] = {path.c_str()};
    fb_catalog* raw_catalog = nullptr;
    const auto opened = fb_open_files(L"en-us", paths, 1, nullptr, nullptr, &raw_catalog);
    Catalog catalog(raw_catalog, fb_close);
    if (opened != S_OK)
        std::wcerr << L"Fixture: " << path << L'\n';
    status(opened, "open private catalog");
    expect(catalog != nullptr, "catalog handle");
    fb_stats stats{};
    status(fb_statistics(catalog.get(), &stats), "private stats");
    for (uint32_t family = 0; family < stats.families; ++family) {
        fb_styles* raw_styles = nullptr;
        status(fb_list_styles(catalog.get(), family, &raw_styles), "list private styles");
        Styles styles(raw_styles, fb_styles_close);
        expect(styles != nullptr, "styles handle");
        for (uint32_t i = 0, count = fb_styles_count(styles.get()); i < count; ++i) {
            fb_style_info info{};
            status(fb_style(styles.get(), i, &info), "read private style");
            fb_selection* raw_face = nullptr;
            status(fb_select(catalog.get(), family, info.index, &raw_face), "select private style");
            Face face(raw_face, fb_selection_close);
            expect(face != nullptr, "selection handle");
            fb_detail detail{};
            status(fb_describe(face.get(), &detail), "describe private face");
            if (detail.face_index != index)
                continue;
            expect(detail.path &&
                       CompareStringOrdinal(path.c_str(), -1, detail.path, -1, TRUE) == CSTR_EQUAL,
                   "fixture file identity");
            return face;
        }
    }
    throw std::runtime_error("required face index absent in private catalog");
}

struct View {
    uint32_t w = 720, h = 108;
    float scale = 1.5f, size = 26.f;
    uint32_t bytes() const {
        return w * h * 4;
    }
};

Layout create(const Face& face, const wchar_t* text, View view = {},
              std::initializer_list<fb_feature> features = {}, const std::vector<float>& axes = {}) {
    fb_layout* raw = nullptr;
    const auto hr = fb_layout_create(face.get(), text, view.size, view.scale, view.w, view.h, axes.data(),
                                     static_cast<uint32_t>(axes.size()), features.begin(),
                                     static_cast<uint32_t>(features.size()), &raw);
    Layout layout(raw, fb_layout_close);
    status(hr, "create layout");
    expect(layout != nullptr, "layout handle");
    return layout;
}
Pixels draw(const Layout& layout, View view = {}) {
    Pixels pixels(view.bytes());
    status(fb_layout_draw(layout.get(), pixels.data(), view.bytes()), "draw layout");
    return pixels;
}
fb_text_rect caret(const Layout& layout, uint32_t pos) {
    fb_text_rect rect{};
    status(fb_layout_caret(layout.get(), pos, &rect), "caret");
    expect(std::isfinite(rect.x) && std::isfinite(rect.y) && rect.width > 0 && rect.height > 0,
           "caret geometry");
    return rect;
}
uint32_t hit(const Layout& layout, float x, float y) {
    uint32_t pos = UINT32_MAX;
    status(fb_layout_hit(layout.get(), x, y, &pos), "hit");
    return pos;
}
float extent(const Layout& layout) {
    float height = 0;
    status(fb_layout_extent(layout.get(), &height), "extent");
    return height;
}
std::vector<fb_text_rect> ranges(const Layout& layout, uint32_t start, uint32_t length) {
    uint32_t count = 0;
    status(fb_layout_ranges(layout.get(), start, length, nullptr, 0, &count), "probe ranges", insufficient);
    expect(count > 0 && count <= 2048, "range count");
    std::vector<fb_text_rect> rects(count);
    status(fb_layout_ranges(layout.get(), start, length, rects.data(), count, &count), "read ranges");
    expect(count == rects.size(), "range count stable");
    return rects;
}
bool close_enough(float a, float b) {
    return std::abs(a - b) < .06f;
}
float mid_y(fb_text_rect rect) {
    return rect.y + rect.height / 2;
}
size_t inspect_mask(const Pixels& pixels) {
    size_t ink = 0;
    bool neutral = true, intermediate = false;
    for (size_t i = 0; i < pixels.size(); i += 4) {
        const auto a = pixels[i + 3];
        ink += a != 0;
        intermediate |= a > 0 && a < 255;
        neutral &= pixels[i] == 0 && pixels[i + 1] == 0 && pixels[i + 2] == 0;
    }
    expect(neutral && ink > 0 && ink < pixels.size() / 4 && intermediate,
           "premultiplied black, transparent background and intermediate alpha");
    return ink;
}
void toggle(const Face& face, const wchar_t* text, uint32_t tag, const char* label) {
    const auto off = create(face, text, {}, {{tag, 0}});
    const auto on = create(face, text, {}, {{tag, 1}});
    expect(draw(off) != draw(on), label);
}

void test_features(const Face& arial) {
    const auto calibri = select_file(L"calibri.ttf");
    const auto on = create(calibri, L"ffi", {}, {{liga, 1}});
    const auto off = create(calibri, L"ffi", {}, {{liga, 0}});
    const auto default_ffi = draw(create(calibri, L"ffi"));
    const auto ligature = draw(on);
    expect(ligature == default_ffi && ligature != draw(off), "Calibri liga off/on/default");
    const auto end = caret(on, 3);
    expect(close_enough(caret(on, 1).x, caret(on, 0).x) && caret(off, 1).x > 0,
           "liga controls caret stops");
    expect(hit(on, end.x * .75f, mid_y(end)) == 3 && hit(on, end.x + 100, mid_y(end)) == 3,
           "ligature trailing hit returns cluster end, including EOL");
    const auto partial = ranges(on, 1, 1);
    expect(partial.size() == 1 && close_enough(partial[0].width, end.x), "range expands partial ligature");

    const auto kern_on = create(arial, L"AVToWa", {}, {{kern, 1}});
    const auto kern_off = create(arial, L"AVToWa", {}, {{kern, 0}});
    expect(caret(kern_on, 6).x < caret(kern_off, 6).x && draw(kern_on) != draw(kern_off), "Arial kern");
    toggle(arial, L"abcd", DWRITE_MAKE_OPENTYPE_TAG('s', 'm', 'c', 'p'), "Arial smcp");
    toggle(arial, L"1234567890", onum, "Arial onum");
    toggle(arial, L"Il", DWRITE_MAKE_OPENTYPE_TAG('s', 's', '0', '1'), "Arial ss01");
    // Arial is tabular by default; start with proportional figures to expose the tnum switch.
    const auto prop = create(arial, L"1234567890", {}, {{pnum, 1}, {tnum, 0}});
    const auto tabular = create(arial, L"1234567890", {}, {{pnum, 1}, {tnum, 1}});
    expect(draw(prop) != draw(tabular), "Arial tnum (with pnum)");
    toggle(select_file(L"gabriola.ttf"), L"a very vexing example of WOW xox",
           DWRITE_MAKE_OPENTYPE_TAG('c', 'a', 'l', 't'), "Gabriola calt");
    std::cout << "PASS: liga/kern/calt/smcp/onum/tnum/ss01 and default shaping\n";
}

void test_geometry(const Face& arial) {
    const auto blank = create(arial, L"");
    const auto entry = caret(blank, 0);
    expect(close_enough(extent(blank), entry.height) && hit(blank, 140, 60) == 0,
           "empty line extent and caret");
    expect(draw(blank) == Pixels(View{}.bytes()), "empty viewport transparent");

    const auto lines = create(arial, L"a\r\nBC\n");
    const auto first_end = caret(lines, 1), second = caret(lines, 3), last = caret(lines, 6);
    expect(close_enough(caret(lines, 2).x, first_end.x) && close_enough(caret(lines, 2).y, first_end.y),
           "CRLF same stop");
    expect(close_enough(last.x, 0) && last.y > second.y && second.y > first_end.y &&
               close_enough(extent(lines), last.y + last.height),
           "final blank line is in document extent");
    expect(hit(lines, 1000, mid_y(first_end)) == 1 && hit(lines, -10, mid_y(second)) == 3 &&
               hit(lines, 1000, mid_y(second)) == 5 && hit(lines, 1000, mid_y(last)) == 6,
           "nearest line edges and trailing empty line");
    const auto rects = ranges(lines, 0, 5);
    expect(rects.size() == 2 && close_enough(rects[0].width, first_end.x) &&
               close_enough(rects[1].y, second.y),
           "cross-line range in document DIP");
    fb_text_rect sentinel{91, 92, 93, 94};
    uint32_t needed = 0;
    status(fb_layout_ranges(lines.get(), 0, 5, &sentinel, 1, &needed), "short ranges", insufficient);
    expect(needed == 2 && sentinel.x == 91 && sentinel.y == 92 && sentinel.width == 93 &&
               sentinel.height == 94,
           "short ranges do not write partial results");
    status(fb_layout_ranges(lines.get(), 6, 0, nullptr, 0, &needed), "zero selection at EOF");
    expect(needed == 0, "zero range count");

    const auto rtl = create(arial, L"\u05d0\u05d1\u05d2");
    const auto right = caret(rtl, 0), left = caret(rtl, 3);
    expect(right.x > left.x && hit(rtl, left.x - 2, mid_y(left)) == 3 &&
               hit(rtl, right.x + 2, mid_y(right)) == 0,
           "RTL leading/trailing caret and hit");
    const auto mixed = create(arial, L"A\u05d0\u05d1\u05d2Z");
    const auto bidi = ranges(mixed, 0, 2);
    expect(bidi.size() == 2 && close_enough(bidi[0].y, bidi[1].y),
           "mixed bidi produces separate rectangles");
    const auto combining = create(arial, L"x\u0301Z");
    const auto after_mark = caret(combining, 2);
    expect(close_enough(caret(combining, 1).x, caret(combining, 0).x) &&
               hit(combining, after_mark.x * .75f, mid_y(after_mark)) == 2,
           "combining cluster is atomic");
    std::cout << "PASS: empty/EOF/CRLF, combining clusters, multiline and bidi geometry\n";
}

void test_dpi_and_scroll(const Face& arial) {
    const View one{60, 40, 1.f}, scaled{90, 60, 1.5f};
    const auto narrow = create(arial, L"one two three four", one);
    const auto narrow_scaled = create(arial, L"one two three four", scaled);
    const auto wide = create(arial, L"one two three four");
    const auto second = caret(narrow, 4), second_scaled = caret(narrow_scaled, 4);
    expect(second.y > 0 && close_enough(caret(wide, 4).y, 0) && close_enough(second.y, second_scaled.y) &&
               close_enough(second.height, second_scaled.height) &&
               close_enough(extent(narrow), extent(narrow_scaled)),
           "wrap uses actual physical width and geometry is DPI-independent");
    expect(hit(narrow_scaled, -1, mid_y(second_scaled)) == 4, "hit on the wrapped line at 150% DPI");
    expect(inspect_mask(draw(narrow_scaled, scaled)) > inspect_mask(draw(narrow, one)), "DPI-scaled ink");

    const View view{240, 42, 1.5f, 24.f};
    auto owned = select_file(L"arial.ttf");
    wchar_t text[] = L"\n\nInk\n";
    const auto doc = create(owned, text, view);
    const auto reference = create(arial, L"Ink\n", view);
    owned.reset();
    text[2] = L'X';
    const auto ink = caret(doc, 2), eof = caret(doc, 6);
    const auto height = extent(doc);
    expect(ink.y > view.h / view.scale && close_enough(height, eof.y + eof.height),
           "extent beyond viewport");
    const auto top = draw(doc, view);
    Pixels pixels(view.bytes(), 0xA7);
    status(fb_layout_draw_at(doc.get(), 0.f, pixels.data(), view.bytes()), "zero scroll");
    expect(top == pixels && top == Pixels(view.bytes()),
           "draw equals draw_at(0) and top viewport is blank");
    auto render = std::async(std::launch::async, [&] {
        return fb_layout_draw_at(doc.get(), ink.y, pixels.data(), view.bytes());
    });
    expect(hit(doc, -1, mid_y(ink)) == 2 && close_enough(caret(doc, 2).y, ink.y) &&
               close_enough(extent(doc), height),
           "draw_at keeps hit, caret and extent in document coordinates");
    status(render.get(), "background scroll after releasing face");
    expect(pixels == draw(reference, view), "drawing translates same ink into the viewport");
    inspect_mask(pixels);
    status(fb_layout_draw_at(doc.get(), height + 1, pixels.data(), view.bytes()), "overscroll past extent");
    expect(pixels == top, "overscroll transparent");

    status(fb_render(arial.get(), L"Ink\n", view.size, view.scale, view.w, view.h, nullptr, 0,
                     pixels.data(), view.bytes()),
           "legacy render");
    inspect_mask(pixels); // The library shaper still produces neutral ink, with its own raster policy.
    status(fb_render(arial.get(), L"", view.size, view.scale, view.w, view.h, nullptr, 0, pixels.data(),
                     view.bytes()),
           "legacy empty render");
    expect(pixels == top, "legacy empty bitmap");
    std::cout << "PASS: width/DPI, grayscale, scrolling, independent lifetimes and legacy entry\n";
}

struct AreaError {
    uint64_t error{}, ink{}, core{};
    void compare(uint8_t alpha, unsigned reference) {
        error += static_cast<unsigned>(std::abs(static_cast<int>(alpha) - static_cast<int>(reference)));
        ink += alpha;
        core += alpha >= 240;
    }
};

void test_specimen_edges(const Face& arial) {
    constexpr uint32_t samples = 5;
    constexpr wchar_t text[] = L"AVOS";
    for (float scale : {1.f, 1.5f}) {
        for (float size : {16.f, 64.f}) { // 12 pt and 48 pt, in DIP
            const View view{static_cast<uint32_t>(208 * scale), static_cast<uint32_t>(88 * scale), scale, size};
            const View high{view.w * samples, view.h * samples, scale * samples, size};
            const auto layout = create(arial, text, view);
            const auto high_layout = create(arial, text, high);
            expect(close_enough(caret(layout, 4).x, caret(high_layout, 4).x) &&
                       extent(layout) < view.h / scale,
                   "coverage comparison uses the same unclipped, unwrapped layout");
            const auto pixels = draw(layout, view);
            const auto oversampled = draw(high_layout, high);
            Pixels legacy(view.bytes());
            status(fb_render(arial.get(), text, size, scale, view.w, view.h, nullptr, 0,
                             legacy.data(), view.bytes()), "draw legacy coverage baseline");
            AreaError before, after;
            unsigned edges = 0;
            for (uint32_t y = 0; y < view.h; ++y) {
                for (uint32_t x = 0; x < view.w; ++x) {
                    unsigned sum = 0;
                    for (uint32_t dy = 0; dy < samples; ++dy)
                        for (uint32_t dx = 0; dx < samples; ++dx)
                            sum += oversampled[((y * samples + dy) * high.w + x * samples + dx) * 4 + 3];
                    const auto area = (sum + samples * samples / 2) / (samples * samples);
                    edges += area > 0 && area < 255;
                    const auto index = (y * view.w + x) * 4 + 3;
                    before.compare(legacy[index], area);
                    after.compare(pixels[index], area);
                }
            }
            std::cout << "Specimen " << scale * 100 << "% @ " << size * 3 / 4
                      << "pt: area error " << before.error << " -> " << after.error
                      << ", reference edge pixels " << edges << ", core pixels "
                      << before.core << " -> " << after.core << '\n';
            expect(edges > 48 && after.error * 4 <= before.error * 3,
                   "specimen coverage is closer to the 5x area reference on curves and diagonals");
            expect(after.core >= 16 && after.core * 2 >= before.core &&
                       after.ink * 10 >= before.ink * 9 && after.ink * 10 <= before.ink * 11,
                   "specimen retains solid cores and stroke mass");
        }
    }
    const View tiny{208, 88, 1.f, 12.f};
    Pixels baseline(tiny.bytes());
    status(fb_render(arial.get(), text, tiny.size, tiny.scale, tiny.w, tiny.h, nullptr, 0,
                     baseline.data(), tiny.bytes()), "draw below the editor's minimum physical size");
    expect(draw(create(arial, text, tiny), tiny) == baseline, "sub-16px text keeps the prior hinting policy");
    std::cout << "PASS: normalized coverage gains at 100% and 150%, 12 pt and 48 pt\n";
}

void test_exact_faces(const Face& arial) {
    // The installed emoji face covers U+1F600, but a private Arial layout must not use it.
    constexpr uint32_t missing[] = {0x1f600, 0x0378};
    uint16_t gids[2] = {1, 1};
    status(fb_map(arial.get(), missing, 2, gids), "map missing glyphs");
    expect(gids[0] == 0 && gids[1] == 0, "fallback probes are absent from the selected font");
    const auto emoji = draw(create(arial, L"\U0001f600"));
    expect(emoji == draw(create(arial, L"\u0378")),
           "missing emoji uses selected .notdef, not a fallback face");
    inspect_mask(emoji);

    auto cambria = select_file(L"cambria.ttc", 0), math = select_file(L"cambria.ttc", 1);
    // These faces share most Latin outlines, but their integral signs have different advances.
    const auto text_integral = create(cambria, L"\u222b"), math_integral = create(math, L"\u222b");
    cambria.reset();
    expect(draw(text_integral) != draw(math_integral) &&
               caret(text_integral, 1).x < caret(math_integral, 1).x,
           "two TTC indices select different physical faces");

    constexpr uint32_t scalar = 0x1d400;
    uint16_t gid = 0;
    status(fb_map(math.get(), &scalar, 1, &gid), "map supplementary character");
    expect(gid != 0, "Cambria Math covers supplementary fixture");
    const auto surrogate = create(math, L"\U0001d400Z");
    const auto after = caret(surrogate, 2);
    expect(close_enough(caret(surrogate, 1).x, caret(surrogate, 0).x) &&
               hit(surrogate, after.x * .75f, mid_y(after)) == 2,
           "surrogate pair never split");
    inspect_mask(draw(surrogate));

    auto variable = select_file(L"bahnschrift.ttf");
    fb_detail detail{};
    status(fb_describe(variable.get(), &detail), "describe variable face");
    expect(detail.axes >= 2 && detail.axes <= 128, "Bahnschrift exposes variation axes");
    std::vector<float> low(detail.axes), high(detail.axes);
    for (uint32_t i = 0; i < detail.axes; ++i) {
        fb_axis axis{};
        status(fb_axis_at(variable.get(), i, &axis), "read axis bounds");
        low[i] = axis.minimum;
        high[i] = axis.maximum;
    }
    const auto thin = create(variable, L"AVToWa", {}, {{kern, 1}}, low);
    const auto thick = create(variable, L"AVToWa", {}, {{kern, 1}}, high);
    auto invalid = low;
    invalid[0] = std::numeric_limits<float>::quiet_NaN();
    fb_layout* raw = nullptr;
    status(fb_layout_create(variable.get(), L"AV", 26, 1.5f, 300, 90, invalid.data(), detail.axes, nullptr,
                            0, &raw),
           "reject nonfinite variation axis", E_INVALIDARG);
    expect(raw == nullptr, "bad axes leave no layout");
    invalid[0] = low[0] - 1.f;
    status(fb_layout_create(variable.get(), L"AV", 26, 1.5f, 300, 90, invalid.data(), detail.axes, nullptr,
                            0, &raw),
           "reject axis below its minimum", E_INVALIDARG);
    expect(raw == nullptr, "out-of-range axes leave no layout");
    variable.reset();
    expect(draw(thin) != draw(thick) && !close_enough(caret(thin, 6).x, caret(thick, 6).x),
           "exact variation changes ink and advance on the same layouts");
    std::cout << "PASS: no font fallback, TTC face identity, UTF-16 surrogate and Bahnschrift axes\n";
}

void test_bad_inputs(const Face& face) {
    const View view;
    const fb_feature good{liga, 1}, bad_tag{0, 1},
        bad_space{DWRITE_MAKE_OPENTYPE_TAG('l', ' ', 'g', 'a'), 0};
    const fb_feature bad_value{liga, 2}, duplicate[] = {{liga, 0}, {liga, 1}};
    const float nan = std::numeric_limits<float>::quiet_NaN();
    const auto reject = [&](const char* label, const wchar_t* text, View spec, const fb_feature* features,
                            uint32_t count, int32_t expected = E_INVALIDARG) {
        auto* raw = reinterpret_cast<fb_layout*>(static_cast<uintptr_t>(1));
        status(fb_layout_create(face.get(), text, spec.size, spec.scale, spec.w, spec.h, nullptr, 0,
                                features, count, &raw),
               label, expected);
        expect(raw == nullptr, "failed create zeros output");
    };
    reject("null text", nullptr, view, nullptr, 0, E_POINTER);
    reject("null features", L"A", view, nullptr, 1, E_POINTER);
    reject("invalid tag", L"A", view, &bad_tag, 1);
    reject("invalid tag whitespace", L"A", view, &bad_space, 1);
    reject("nonbinary feature", L"A", view, &bad_value, 1);
    reject("duplicate feature", L"A", view, duplicate, 2);
    reject("feature count bound", L"A", view, &good, 65);
    reject("scale zero", L"A", {16, 16, 0}, nullptr, 0);
    reject("scale NaN", L"A", {16, 16, nan}, nullptr, 0);
    reject("size NaN", L"A", {16, 16, 1, nan}, nullptr, 0);
    reject("size above limit", L"A", {16, 16, 1, 257}, nullptr, 0);
    reject("empty viewport", L"A", {0, 16}, nullptr, 0);
    reject("viewport too wide", L"A", {4097, 16}, nullptr, 0);
    reject("viewport area", L"A", {4096, 4096}, nullptr, 0);
    reject("text too long", std::wstring(2049, L'a').c_str(), view, nullptr, 0);
    status(fb_layout_create(face.get(), L"A", 26, 1, 30, 30, nullptr, 0, nullptr, 0, nullptr),
           "null create output", E_POINTER);

    const auto layout = create(face, L"A", view);
    Pixels guarded(view.bytes() + 2, 0xA7);
    auto* pixels = guarded.data() + 1;
    status(fb_layout_draw(layout.get(), pixels, view.bytes() - 1), "short pixel buffer", E_INVALIDARG);
    status(fb_layout_draw_at(layout.get(), -1, pixels, view.bytes()), "negative scroll", E_INVALIDARG);
    status(fb_layout_draw_at(layout.get(), nan, pixels, view.bytes()), "NaN scroll", E_INVALIDARG);
    expect(std::all_of(guarded.begin(), guarded.end(), [](uint8_t v) { return v == 0xA7; }),
           "rejected draws do not write pixels");
    status(fb_layout_draw(layout.get(), pixels, view.bytes() + 1), "oversized pixel buffer");
    expect(guarded.front() == 0xA7 && guarded.back() == 0xA7, "draw only writes viewport bytes");
    status(fb_layout_draw(layout.get(), nullptr, view.bytes()), "null pixels", E_POINTER);

    uint32_t count = 0;
    fb_text_rect rect{};
    status(fb_layout_hit(layout.get(), nan, 0, &count), "nonfinite hit", E_INVALIDARG);
    status(fb_layout_hit(layout.get(), 0, 0, nullptr), "null hit output", E_POINTER);
    status(fb_layout_caret(layout.get(), 2, &rect), "caret past end", E_INVALIDARG);
    status(fb_layout_caret(layout.get(), 0, nullptr), "null caret output", E_POINTER);
    status(fb_layout_ranges(layout.get(), 1, UINT32_MAX, &rect, 1, &count), "range overflow", E_INVALIDARG);
    status(fb_layout_ranges(layout.get(), 0, 1, nullptr, 1, &count), "null range buffer", E_POINTER);
    status(fb_layout_ranges(layout.get(), 0, 1, &rect, 1, nullptr), "null range count", E_POINTER);
    status(fb_layout_extent(layout.get(), nullptr), "null extent output", E_POINTER);
    fb_layout_close(nullptr);
    std::cout << "PASS: invalid arguments, buffer guards and null close\n";
}
} // namespace

int main() {
    try {
        const auto arial = select_file(L"arial.ttf");
        test_features(arial);
        test_geometry(arial);
        test_dpi_and_scroll(arial);
        test_specimen_edges(arial);
        test_exact_faces(arial);
        test_bad_inputs(arial);
        std::cout << "editor_layout_checks: " << checks << " passed\n";
        return 0;
    } catch (const std::exception& e) {
        std::cerr << "editor_layout_checks failed: " << e.what() << '\n';
        return 1;
    }
}

#include "../../native/glyph_metadata.h"

// Standalone target: compile this source only, without FB_BUILD or a DLL import library.
#define FB_GLYPH_METADATA_CHECKS
#include "../../native/glyph_metadata.cpp"
#undef FB_GLYPH_METADATA_CHECKS

#include <cstdlib>
#include <initializer_list>
#include <iostream>
#include <string_view>

static_assert(sizeof(void*) == 8 && sizeof(wchar_t) == 2);
static_assert(sizeof(fb_glyph_info) == 16);
static_assert(offsetof(fb_glyph_info, first_scalar) == 8);
static_assert(offsetof(fb_glyph_info, mapping_count) == 12);

namespace {
unsigned checks = 0;

void check(bool condition, const char* expression, int line) {
    if (!condition) {
        std::cerr << "FAIL glyph_metadata_checks:" << line << ": " << expression << '\n';
        std::exit(EXIT_FAILURE);
    }
    ++checks;
}

#define CHECK(expression) check((expression), #expression, __LINE__)

using Bytes = std::vector<uint8_t>;

Bytes post(uint32_t version) {
    Bytes bytes(32, 0);
    for (unsigned i = 0; i < 4; ++i)
        bytes[i] = static_cast<uint8_t>(version >> (24 - 8 * i));
    return bytes;
}

void append_u16(Bytes& bytes, uint16_t value) {
    bytes.push_back(static_cast<uint8_t>(value >> 8));
    bytes.push_back(static_cast<uint8_t>(value));
}

Bytes post2(std::initializer_list<uint16_t> indices, std::initializer_list<uint8_t> names = {}) {
    auto bytes = post(0x00020000);
    append_u16(bytes, static_cast<uint16_t>(indices.size()));
    for (const auto index : indices)
        append_u16(bytes, index);
    bytes.insert(bytes.end(), names);
    return bytes;
}

PostNames parse(const Bytes& bytes, uint16_t count) {
    return parse_post(bytes.data(), bytes.size(), count);
}

void expect_name(const PostNames& names, uint32_t gid, std::wstring_view expected) {
    const auto* name = names.at(gid);
    CHECK(name && name == expected);
}

void expect_invalid(const Bytes& bytes, uint16_t count) {
    auto names = parse(bytes, count);
    CHECK(names.status == NameStatus::invalid);
    CHECK(!names.at(0));
}

void check_format1() {
    const auto bytes = post(0x00010000);
    const auto names = parse(bytes, 300);
    CHECK(names.status == NameStatus::available);
    expect_name(names, 0, L".notdef");
    expect_name(names, 36, L"A");
    expect_name(names, 257, L"dcroat");
    CHECK(!names.at(258));
    const auto short_face = parse(bytes, 2);
    expect_name(short_face, 1, L".null");
    CHECK(!short_face.at(2));
}

void check_format2() {
    const auto names = parse(post2({0, 36, 259, 258, 260, 259},
                                   {5, 'A', '.', 'a', 'l', 't', 7, 'g', 'l', 'y', 'p', 'h', '-', 0xdb, 0}),
                             6);
    CHECK(names.status == NameStatus::available);
    expect_name(names, 0, L".notdef");
    expect_name(names, 1, L"A");
    expect_name(names, 2, L"glyph-\u20ac"); // Legacy MacRoman code page 10000.
    expect_name(names, 3, L"A.alt");
    CHECK(!names.at(4));
    CHECK(names.at(2) == names.at(5)); // Shared name, with stable storage in the book.
    CHECK(!names.at(6));

    const auto empty_names = parse(post2({258}, {0}), 1);
    CHECK(empty_names.status == NameStatus::none);
    CHECK(!empty_names.at(0));
}

void check_format25() {
    auto bytes = post(0x00025000);
    bytes.insert(bytes.end(), {0, 35, 0, 0xfd, 0xff});
    const auto names = parse(bytes, 5);
    CHECK(names.status == NameStatus::available);
    expect_name(names, 1, L"A");
    expect_name(names, 3, L".notdef");
    expect_name(names, 4, L"space");
    expect_invalid(bytes, 6); // No length field or extra offset for this glyph.

    bytes[32] = 0xff;
    expect_invalid(bytes, 5); // GID 0 + (-1).
    bytes.resize(32 + 132, 0);
    bytes[32] = 0;
    bytes.back() = 127;
    expect_invalid(bytes, 132); // GID 131 + 127 == 258, outside the 258 names.
    bytes.back() = 126;
    expect_name(parse(bytes, 132), 131, L"dcroat");
}

void check_nameless_and_invalid() {
    auto bytes = post(0x00030000);
    const auto no_names = parse(bytes, 3);
    CHECK(no_names.status == NameStatus::none);
    CHECK(!no_names.at(0) && !no_names.at(2));
    bytes.pop_back();
    expect_invalid(bytes, 3);
    expect_invalid(post(0x00040000), 3);
    expect_invalid(post2({0, 36}), 1);                    // Inconsistent with the face's glyph count.
    expect_invalid(post2({0, 259}, {1, 'a'}), 2);         // Required second Pascal name absent.
    expect_invalid(post2({0, 0xffff}), 2);                // Out-of-bounds custom name slot.
    expect_invalid(post2({0, 258}, {3, 'a', 0, 'b'}), 2); // Cannot expose an embedded NUL.

    bytes = post2({0, 258}, {3, 'a', 'l', 't'});
    while (!bytes.empty()) {
        bytes.pop_back();
        expect_invalid(bytes, 2); // Every truncated prefix, including within the index array.
    }
}

struct Range {
    uint32_t first, last;
};

void check_aliases() {
    const std::vector<Range> ranges{{0x1f600, 0x1f600},
                                    {0x43, 0x44},
                                    {0x41, 0x43},
                                    {0x41, 0x41},
                                    {0, 0},
                                    {0xd7ff, 0xe000},
                                    {max_scalar - 1, UINT32_MAX},
                                    {max_scalar + 1, UINT32_MAX},
                                    {0x100, 0xff}};
    std::vector<uint32_t> visited;
    const auto mappings = invert_cmap(
        5, ranges,
        [&](const uint32_t* scalars, uint32_t count, uint16_t* gids) {
            visited.insert(visited.end(), scalars, scalars + count);
            for (uint32_t i = 0; i < count; ++i) {
                switch (scalars[i]) {
                case 0:
                case 0x42:
                case 0x1f600:
                    gids[i] = 1;
                    break;
                case 0x41:
                    gids[i] = 2;
                    break;
                case 0xe000:
                case max_scalar:
                    gids[i] = 3;
                    break;
                case 0x44:
                    gids[i] = UINT16_MAX; // Out-of-range GID.
                    break;
                default:
                    gids[i] = 0;
                    break;
                }
            }
        },
        [] {});
    CHECK((visited == std::vector<uint32_t>{0, 0x41, 0x42, 0x43, 0x44, 0xd7ff, 0xe000, 0x1f600,
                                            max_scalar - 1, max_scalar}));
    CHECK(mappings.size() == 5);
    CHECK(mappings[0].empty());
    CHECK((mappings[1] == std::vector<uint32_t>{0, 0x42, 0x1f600}));
    CHECK((mappings[2] == std::vector<uint32_t>{0x41}));
    CHECK((mappings[3] == std::vector<uint32_t>{0xe000, max_scalar}));
    expect_name(parse(post2({0, 0, 0, 0, 36}), 5), 4, L"A");
    CHECK(mappings[4].empty()); // A name does not imply any cmap entry.
}

void check_blocks_and_cancel() {
    constexpr auto total = static_cast<uint32_t>(2 * cmap_block + 3);
    const std::vector<Range> ranges{{1, total}};
    std::vector<uint32_t> batches;
    unsigned polls = 0;
    const auto lookup = [&](const uint32_t*, uint32_t count, uint16_t* gids) {
        CHECK(polls == batches.size() + 1); // Cancellation check before every lookup.
        CHECK(count > 0 && count <= cmap_block);
        batches.push_back(count);
        std::fill_n(gids, count, uint16_t{1});
    };
    const auto mappings = invert_cmap(2, ranges, lookup, [&] { ++polls; });
    CHECK((batches == std::vector<uint32_t>{cmap_block, cmap_block, 3}));
    CHECK(mappings[1].size() == total);
    CHECK(mappings[1].front() == 1 && mappings[1].back() == total);

    struct Cancelled {};
    batches.clear();
    polls = 0;
    bool stopped = false;
    try {
        invert_cmap(2, ranges, lookup, [&] {
            if (++polls == 2)
                throw Cancelled{};
        });
    } catch (Cancelled) {
        stopped = true;
    }
    CHECK(stopped);
    CHECK((batches == std::vector<uint32_t>{cmap_block}));
}
} // namespace

int main() {
    check_format1();
    check_format2();
    check_format25();
    check_nameless_and_invalid();
    check_aliases();
    check_blocks_and_cancel();
    std::cout << "glyph_metadata_checks: " << checks << " passed\n";
    return EXIT_SUCCESS;
}

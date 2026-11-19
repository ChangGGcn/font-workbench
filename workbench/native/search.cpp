#include "search.hpp"

#include <array>
#include <limits>
#include <stdexcept>
#include <utility>

#if defined(_WIN32)
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#endif

namespace fb {
namespace {

bool is_search_space(wchar_t value) noexcept {
    const unsigned int code_unit = static_cast<unsigned int>(value);
    return (code_unit >= 0x0009U && code_unit <= 0x000DU) || code_unit == 0x0020U || code_unit == 0x0085U ||
           code_unit == 0x00A0U || code_unit == 0x1680U || (code_unit >= 0x2000U && code_unit <= 0x200AU) ||
           code_unit == 0x2028U || code_unit == 0x2029U || code_unit == 0x202FU || code_unit == 0x205FU ||
           code_unit == 0x3000U;
}

std::wstring fold_case(std::wstring_view value) {
    if (value.empty()) {
        return {};
    }

#if defined(_WIN32)
    const auto int_max = static_cast<std::size_t>((std::numeric_limits<int>::max)());
    if (value.size() > int_max) {
        throw std::length_error("SearchIndex: source string exceeds the Windows API length limit");
    }

    const int source_length = static_cast<int>(value.size());
    const int required_length = ::LCMapStringEx(LOCALE_NAME_INVARIANT, LCMAP_LOWERCASE, value.data(),
                                                source_length, nullptr, 0, nullptr, nullptr, 0);
    if (required_length <= 0) {
        throw std::runtime_error("SearchIndex: invariant Unicode case folding failed");
    }

    std::wstring folded(static_cast<std::size_t>(required_length), L'\0');
    const int written_length =
        ::LCMapStringEx(LOCALE_NAME_INVARIANT, LCMAP_LOWERCASE, value.data(), source_length, folded.data(),
                        required_length, nullptr, nullptr, 0);
    if (written_length <= 0 || written_length > required_length) {
        throw std::runtime_error("SearchIndex: invariant Unicode case folding failed");
    }
    folded.resize(static_cast<std::size_t>(written_length));
    return folded;
#else
    std::wstring folded(value);
    for (wchar_t& character : folded) {
        if (character >= L'A' && character <= L'Z') {
            character = static_cast<wchar_t>(character - L'A' + L'a');
        } else if ((character >= 0x00C0 && character <= 0x00D6) ||
                   (character >= 0x00D8 && character <= 0x00DE)) {
            character = static_cast<wchar_t>(character + 0x20);
        }
    }
    return folded;
#endif
}

std::wstring fold_text(std::wstring_view value) {
    std::wstring folded = fold_case(value);

    std::size_t write_position = 0;
    bool pending_space = false;
    for (const wchar_t character : folded) {
        if (is_search_space(character)) {
            pending_space = write_position != 0;
            continue;
        }

        if (pending_space) {
            folded[write_position++] = L' ';
            pending_space = false;
        }
        folded[write_position++] = character;
    }
    folded.resize(write_position);
    return folded;
}

bool is_all_search_space(std::wstring_view text) noexcept {
    for (const wchar_t character : text) {
        if (!is_search_space(character)) {
            return false;
        }
    }
    return true;
}

std::vector<std::wstring_view> split_terms(std::wstring_view text) {
    std::size_t term_count = 0;
    bool inside_term = false;
    for (const wchar_t character : text) {
        if (is_search_space(character)) {
            inside_term = false;
        } else if (!inside_term) {
            inside_term = true;
            ++term_count;
            if (term_count > SearchIndex::max_query_terms) {
                throw std::length_error("SearchIndex::query: query exceeds the 64-term limit");
            }
        }
    }

    std::vector<std::wstring_view> terms;
    terms.reserve(term_count);

    std::size_t term_start = 0;
    inside_term = false;
    for (std::size_t position = 0; position < text.size(); ++position) {
        if (is_search_space(text[position])) {
            if (inside_term) {
                terms.emplace_back(text.data() + term_start, position - term_start);
                inside_term = false;
            }
        } else if (!inside_term) {
            term_start = position;
            inside_term = true;
        }
    }
    if (inside_term) {
        terms.emplace_back(text.data() + term_start, text.size() - term_start);
    }
    return terms;
}

bool contains(const std::wstring& haystack, std::wstring_view needle) noexcept {
    return haystack.find(needle.data(), 0, needle.size()) != std::wstring::npos;
}

std::vector<std::uint32_t> all_indices(std::size_t count) {
    std::vector<std::uint32_t> result;
    result.reserve(count);
    for (std::size_t index = 0; index < count; ++index) {
        result.push_back(static_cast<std::uint32_t>(index));
    }
    return result;
}

} // namespace

SearchIndex::SearchIndex(std::vector<Label> rows) {
    const auto uint32_max = static_cast<std::size_t>((std::numeric_limits<std::uint32_t>::max)());
    if (rows.size() > uint32_max) {
        throw std::length_error("SearchIndex: row count exceeds the uint32 index range");
    }

    entries_.reserve(rows.size());
    for (const Label& row : rows) {
        FoldedEntry entry;
        entry.display = fold_text(row.display);
        entry.aliases = fold_text(row.aliases);
        entries_.push_back(std::move(entry));
    }
}

std::vector<std::uint32_t> SearchIndex::query(std::wstring_view text) const {
    if (text.size() > max_query_code_units) {
        throw std::length_error("SearchIndex::query: query exceeds the 1024-code-unit limit");
    }
    if (is_all_search_space(text)) {
        return all_indices(entries_.size());
    }

    const std::wstring normalized_query = fold_text(text);
    const std::vector<std::wstring_view> terms = split_terms(normalized_query);
    const auto matches_all_terms = [&terms](const FoldedEntry& entry) noexcept {
        for (const std::wstring_view term : terms) {
            if (!contains(entry.display, term) && !contains(entry.aliases, term)) {
                return false;
            }
        }
        return true;
    };
    const auto match_tier = [&normalized_query,
                             &matches_all_terms](const FoldedEntry& entry) noexcept -> std::size_t {
        if (!matches_all_terms(entry)) {
            return 3;
        }
        const bool display_has_query_prefix =
            normalized_query.size() <= entry.display.size() &&
            entry.display.compare(0, normalized_query.size(), normalized_query.data(),
                                  normalized_query.size()) == 0;
        if (display_has_query_prefix && entry.display.size() == normalized_query.size()) {
            return 0;
        }
        if (display_has_query_prefix) {
            return 1;
        }
        return 2;
    };

    std::array<std::size_t, 3> tier_counts{};
    for (const FoldedEntry& entry : entries_) {
        const std::size_t tier = match_tier(entry);
        if (tier < tier_counts.size()) {
            ++tier_counts[tier];
        }
    }

    const std::size_t total_matches = tier_counts[0] + tier_counts[1] + tier_counts[2];
    std::vector<std::uint32_t> result;
    result.resize(total_matches);
    if (total_matches == 0) {
        return result;
    }

    std::array<std::size_t, 3> write_positions = {0, tier_counts[0], tier_counts[0] + tier_counts[1]};
    for (std::size_t index = 0; index < entries_.size(); ++index) {
        const std::size_t tier = match_tier(entries_[index]);
        if (tier < write_positions.size()) {
            result[write_positions[tier]++] = static_cast<std::uint32_t>(index);
        }
    }
    return result;
}

} // namespace fb

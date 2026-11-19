#pragma once

#include <cstddef>
#include <cstdint>
#include <stdexcept>
#include <string>
#include <string_view>
#include <vector>

namespace fb {

struct Label {
    std::wstring display;
    std::wstring aliases;
};

class SearchIndex {
  public:
    // Limits are inclusive. Oversized queries throw std::length_error rather
    // than doing unbounded work.
    static constexpr std::size_t max_query_code_units = 1024;
    static constexpr std::size_t max_query_terms = 64;

    explicit SearchIndex(std::vector<Label> rows);

    // An empty or all-whitespace query returns every input index in input
    // order. Otherwise every whitespace-separated term must occur in display
    // or aliases. Results are grouped by these stable tiers:
    //   0. normalized display exactly equals the complete normalized query
    //   1. normalized display starts with the complete normalized query
    //   2. any other match
    // Input order is preserved within each tier.
    //
    // Construction folds display and aliases once. Query performs one fold of
    // the query and never folds or allocates a transformed string per row.
    // Concurrent query calls on a fully constructed, otherwise unmodified
    // instance are read-only and thread-safe.
    std::vector<std::uint32_t> query(std::wstring_view text) const;

  private:
    struct FoldedEntry {
        std::wstring display;
        std::wstring aliases;
    };

    std::vector<FoldedEntry> entries_;
};

} // namespace fb

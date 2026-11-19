#include "../../native/search.hpp"

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cstddef>
#include <cstdint>
#include <cstdlib>
#include <iomanip>
#include <initializer_list>
#include <iostream>
#include <sstream>
#include <stdexcept>
#include <string>
#include <thread>
#include <utility>
#include <vector>

namespace {

int assertions_passed = 0;

[[noreturn]] void fail(const char* expression, const char* file, int line) {
    std::cerr << "FAIL: assertion failed: " << expression << " (" << file
              << ':' << line << ")\n";
    std::exit(EXIT_FAILURE);
}

// This remains active in Release, unlike the standard assert macro when
// NDEBUG is defined.
void require(bool condition, const char* expression, const char* file,
             int line) {
    if (!condition) {
        fail(expression, file, line);
    }
    ++assertions_passed;
}

#define REQUIRE(expression) \
    require((expression), #expression, __FILE__, __LINE__)

void expect_indices(const char* name,
                    const std::vector<std::uint32_t>& actual,
                    std::initializer_list<std::uint32_t> expected) {
    const std::vector<std::uint32_t> expected_vector(expected);
    if (actual != expected_vector) {
        std::cerr << "FAIL: " << name << " expected [";
        for (std::size_t index = 0; index < expected_vector.size(); ++index) {
            if (index != 0) {
                std::cerr << ", ";
            }
            std::cerr << expected_vector[index];
        }
        std::cerr << "] but got [";
        for (std::size_t index = 0; index < actual.size(); ++index) {
            if (index != 0) {
                std::cerr << ", ";
            }
            std::cerr << actual[index];
        }
        std::cerr << "]\n";
        fail(name, __FILE__, __LINE__);
    }
    ++assertions_passed;
}

std::vector<std::uint32_t> index_sequence(std::size_t count) {
    std::vector<std::uint32_t> result;
    result.reserve(count);
    for (std::size_t index = 0; index < count; ++index) {
        result.push_back(static_cast<std::uint32_t>(index));
    }
    return result;
}

std::vector<fb::Label> make_fixture() {
    return {
        {L"Source Sans",
         L"source sample \u6977\u4F53"},
        {L"Source",
         L"sans-only alias"},
        {L"Open Source Sans",
         L"open source samples"},
        {L"Other",
         L"source sans"},
        {L"Source Sans",
         L"duplicate display alias"},
        {L"\u00C9cole",
         L"\u5B66\u6821 \u00E9cole"},
        {L"Noto\u00A0Sans\tCJK",
         L"\u601D\u6E90\u9ED1\u4F53 Noto"},
        {L"Noto Serif CJK",
         L"\u601D\u6E90\u5B8B\u4F53 Noto"},
    };
}

void check_search_contract() {
    const fb::SearchIndex index(make_fixture());
    const std::vector<std::uint32_t> all = index_sequence(8);

    REQUIRE(index.query(L"").size() == all.size());
    expect_indices("empty query", index.query(L""), {0, 1, 2, 3, 4, 5, 6, 7});
    expect_indices("whitespace query",
                   index.query(L" \t\r\n\u00A0\u1680\u2003\u3000"),
                   {0, 1, 2, 3, 4, 5, 6, 7});
    expect_indices("missing term", index.query(L"definitely-missing"), {});

    // Exact display, then display prefix, then all remaining matches. Duplicate
    // displays stay in input order.
    expect_indices("ranked single term", index.query(L"source"),
                   {1, 0, 4, 2, 3});
    expect_indices("case-insensitive ranking", index.query(L"SOURCE"),
                   {1, 0, 4, 2, 3});
    expect_indices("multi-term AND and stable ranking",
                   index.query(L"source sans"), {0, 4, 1, 2, 3});
    expect_indices("multi-term ordinal input order",
                   index.query(L"sans source"), {0, 1, 2, 3, 4});
    expect_indices("multi-term miss", index.query(L"source cjk"), {});

    expect_indices("display prefix", index.query(L"noto"), {6, 7});
    expect_indices("contiguous display prefix", index.query(L"noto sans"),
                   {6});
    expect_indices("multi-term display contains", index.query(L"noto cjk"),
                   {6, 7});
    expect_indices("reversed multi-term display contains",
                   index.query(L"cjk noto"), {6, 7});

    expect_indices("CJK alias shared term",
                   index.query(L"\u601D\u6E90"), {6, 7});
    expect_indices("CJK alias exact term",
                   index.query(L"\u601D\u6E90\u9ED1\u4F53"), {6});
    expect_indices("CJK alias other row", index.query(L"\u6977\u4F53"), {0});
    expect_indices("display plus CJK alias terms",
                   index.query(L"cjk \u5B8B\u4F53"), {7});

    expect_indices("accented exact display", index.query(L"\u00C9COLE"), {5});
    expect_indices("accented lowercase exact", index.query(L"\u00E9cole"),
                   {5});
    expect_indices("accented display prefix", index.query(L"\u00E9C"), {5});

    expect_indices("canonicalized query whitespace",
                   index.query(L"  SOURCE\t SANS\u3000"), {0, 4, 1, 2, 3});
}

void check_query_limits() {
    const fb::SearchIndex index(make_fixture());

    bool code_unit_limit_accepted = true;
    try {
        (void)index.query(std::wstring(
            fb::SearchIndex::max_query_code_units, L'a'));
    } catch (...) {
        code_unit_limit_accepted = false;
    }
    REQUIRE(code_unit_limit_accepted);

    bool oversized_code_units_rejected = false;
    try {
        (void)index.query(std::wstring(
            fb::SearchIndex::max_query_code_units + 1, L'a'));
    } catch (const std::length_error& error) {
        oversized_code_units_rejected =
            std::string(error.what()).find("1024") != std::string::npos;
    } catch (...) {
    }
    REQUIRE(oversized_code_units_rejected);

    std::wstring maximum_terms;
    for (std::size_t term = 0; term < fb::SearchIndex::max_query_terms;
         ++term) {
        if (term != 0) {
            maximum_terms.push_back(L' ');
        }
        maximum_terms.push_back(L'a');
    }
    bool term_limit_accepted = true;
    try {
        (void)index.query(maximum_terms);
    } catch (...) {
        term_limit_accepted = false;
    }
    REQUIRE(term_limit_accepted);

    maximum_terms.push_back(L' ');
    maximum_terms.push_back(L'a');
    bool oversized_terms_rejected = false;
    try {
        (void)index.query(maximum_terms);
    } catch (const std::length_error& error) {
        oversized_terms_rejected =
            std::string(error.what()).find("64") != std::string::npos;
    } catch (...) {
    }
    REQUIRE(oversized_terms_rejected);
}

void check_concurrent_reads() {
    const fb::SearchIndex index(make_fixture());
    const std::vector<std::wstring> queries = {
        L"source", L"noto cjk", L"\u00C9COLE", L"  source   sans "};
    const std::vector<std::vector<std::uint32_t>> expected = {
        {1, 0, 4, 2, 3},
        {6, 7},
        {5},
        {0, 4, 1, 2, 3},
    };

    for (std::size_t query_index = 0; query_index < queries.size();
         ++query_index) {
        REQUIRE(index.query(queries[query_index]) == expected[query_index]);
    }

    std::atomic<int> mismatches{0};
    std::vector<std::thread> workers;
    constexpr int worker_count = 8;
    constexpr int queries_per_worker = 100;
    workers.reserve(worker_count);
    for (int worker = 0; worker < worker_count; ++worker) {
        workers.emplace_back([&, worker]() {
            for (int iteration = 0; iteration < queries_per_worker;
                 ++iteration) {
                const std::size_t query_index =
                    static_cast<std::size_t>(worker + iteration) %
                    queries.size();
                if (index.query(queries[query_index]) !=
                    expected[query_index]) {
                    mismatches.fetch_add(1, std::memory_order_relaxed);
                }
            }
        });
    }
    for (std::thread& worker : workers) {
        worker.join();
    }
    REQUIRE(mismatches.load(std::memory_order_relaxed) == 0);
}

std::wstring padded_id(std::size_t value) {
    std::wostringstream output;
    output << std::setw(5) << std::setfill(L'0') << value;
    // A typed fixture ID cannot accidentally match a different row's numeric alias.
    return L"fb-" + output.str();
}

std::wstring make_display_name(std::size_t family) {
    return L"Family " + padded_id(family) + L" Sans";
}

std::wstring make_alias(std::size_t family) {
    return L"\u5B57\u4F53 " + padded_id(family) + L" Alias " +
           std::to_wstring(family * 3 + 7);
}

struct BenchmarkCase {
    std::wstring query;
    std::uint32_t expected_index;
};

void check_large_fixed_dataset_and_benchmark() {
    constexpr std::size_t family_count = 18000;
    constexpr std::size_t cases_per_shape = 32;

    REQUIRE(family_count >= 15000);

    std::vector<fb::Label> labels;
    labels.reserve(family_count);
    for (std::size_t family = 0; family < family_count; ++family) {
        labels.push_back({make_display_name(family), make_alias(family)});
    }

    std::vector<BenchmarkCase> queries;
    queries.reserve(cases_per_shape * 4);
    for (std::size_t case_index = 0; case_index < cases_per_shape;
         ++case_index) {
        const std::size_t family =
            (case_index * 521 + 17) % family_count;
        const auto expected_index = static_cast<std::uint32_t>(family);
        queries.push_back({make_display_name(family), expected_index});
        queries.push_back(
            {L"Family " + padded_id(family), expected_index});
        queries.push_back({make_alias(family), expected_index});
        queries.push_back(
            {L"\u5B57\u4F53 " + padded_id(family), expected_index});
    }

    const auto build_start = std::chrono::steady_clock::now();
    const fb::SearchIndex index(std::move(labels));
    const auto build_end = std::chrono::steady_clock::now();
    const double build_ms =
        std::chrono::duration<double, std::milli>(build_end - build_start)
            .count();

    for (std::size_t warmup = 0; warmup < 8; ++warmup) {
        (void)index.query(queries[warmup].query);
    }

    std::vector<std::chrono::steady_clock::duration> durations;
    durations.reserve(queries.size());
    std::size_t non_empty_queries = 0;
    for (const BenchmarkCase& benchmark : queries) {
        const auto query_start = std::chrono::steady_clock::now();
        const std::vector<std::uint32_t> result =
            index.query(benchmark.query);
        const auto query_end = std::chrono::steady_clock::now();
        durations.push_back(query_end - query_start);

        if (result.size() == 1 &&
            result.front() == benchmark.expected_index) {
            ++non_empty_queries;
        }
        REQUIRE(result.size() == 1);
        REQUIRE(result.front() == benchmark.expected_index);
    }

    REQUIRE(queries.size() >= 100);
    REQUIRE(non_empty_queries == queries.size());

    std::sort(durations.begin(), durations.end());
    const auto percentile = [&durations](std::size_t percent) {
        const std::size_t rank =
            (durations.size() * percent + 99) / 100;
        return durations[rank - 1];
    };
    const double p50_ms =
        std::chrono::duration<double, std::milli>(percentile(50)).count();
    const double p95_ms =
        std::chrono::duration<double, std::milli>(percentile(95)).count();

    std::cout << std::fixed << std::setprecision(3)
              << "Synthetic families: " << family_count << '\n'
              << "Synthetic build: " << build_ms << " ms\n"
              << "Measured non-empty queries: " << non_empty_queries << '/'
              << queries.size() << '\n'
              << "Query p50: " << p50_ms << " ms\n"
              << "Query p95: " << p95_ms << " ms\n"
              << "Scope: synthetic in-memory labels only; these timings are "
                 "not a real font scan or system-wide search benchmark.\n";
}

}  // namespace

int main() {
    std::cout << "FontBook Next native SearchIndex checks\n";
    check_search_contract();
    check_query_limits();
    check_concurrent_reads();
    const fb::SearchIndex overlapping({{L"Family 10437 Sans", L""}, {L"Family 00999 Sans", L"Alias 10437"}});
    expect_indices("a numeric alias is a legitimate secondary match", overlapping.query(L"Family 10437"), {0,1});
    check_large_fixed_dataset_and_benchmark();
    std::cout << "PASS: " << assertions_passed
              << " always-on assertions\n";
    return EXIT_SUCCESS;
}

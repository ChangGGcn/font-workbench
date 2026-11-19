#pragma once
#define NOMINMAX
#define WIN32_LEAN_AND_MEAN
#include "api.h"
#include "search.hpp"
#include <algorithm>
#include <cmath>
#include <dwrite_3.h>
#include <memory>
#include <stdexcept>
#include <string>
#include <utility>
#include <vector>
#include <windows.h>

namespace fb {
struct Failure {
    HRESULT hr;
};
inline void check(HRESULT hr) {
    if (FAILED(hr))
        throw Failure{hr};
}
inline void require(bool cond, HRESULT hr = E_INVALIDARG) {
    if (!cond)
        throw Failure{hr};
}
template <class Fn> int32_t guard(Fn&& fn) noexcept {
    try {
        fn();
        return S_OK;
    } catch (Failure e) {
        return e.hr;
    } catch (const std::bad_alloc&) {
        return E_OUTOFMEMORY;
    } catch (const std::invalid_argument&) {
        return E_INVALIDARG;
    } catch (const std::length_error&) {
        return E_INVALIDARG;
    } catch (...) {
        return E_UNEXPECTED;
    }
}
template <class T> class Com {
    T* ptr_ = nullptr;

  public:
    Com() = default;
    Com(const Com& other) : ptr_(other.ptr_) {
        if (ptr_)
            ptr_->AddRef();
    }
    Com(Com&& other) noexcept : ptr_(std::exchange(other.ptr_, nullptr)) {}
    Com& operator=(Com other) noexcept {
        std::swap(ptr_, other.ptr_);
        return *this;
    }
    ~Com() {
        if (ptr_)
            ptr_->Release();
    }
    T* get() const {
        return ptr_;
    }
    T* operator->() const {
        return ptr_;
    }
    T** put() {
        if (ptr_) {
            ptr_->Release();
            ptr_ = nullptr;
        }
        return &ptr_;
    }
    template <class U> Com<U> as() const {
        Com<U> out;
        check(ptr_->QueryInterface(__uuidof(U), reinterpret_cast<void**>(out.put())));
        return out;
    }
};
struct Family {
    std::wstring name, key;
    uint32_t ordinal, styles;
};
struct Style {
    std::wstring name;
    uint32_t index, weight;
};
struct Axis {
    fb_axis range;
    size_t slot;
};
std::wstring pick_name(IDWriteLocalizedStrings* names, const std::wstring& locale);
std::wstring file_path(IDWriteFontFile* file);
} // namespace fb
struct fb_catalog {
    fb::Com<IDWriteFactory6> factory;
    fb::Com<IDWriteFontCollection2> collection;
    std::wstring locale;
    std::vector<fb::Family> families;
    std::unique_ptr<fb::SearchIndex> search;
    fb_stats stats{};
    fb::Com<IDWriteFontFamily2> get(uint32_t id) const;
};
struct fb_styles {
    std::vector<fb::Style> rows;
};
struct fb_selection {
    fb::Com<IDWriteFactory6> factory;
    fb::Com<IDWriteFontFace5> face;
    fb::Com<IDWriteFontResource> resource;
    fb::Com<IDWriteFontFallback> no_fallback;
    DWRITE_FONT_SIMULATIONS simulations{};
    std::wstring path, postscript, locale;
    uint32_t face_index{}, glyphs{};
    std::vector<DWRITE_FONT_AXIS_VALUE> values;
    std::vector<fb::Axis> axes;
};

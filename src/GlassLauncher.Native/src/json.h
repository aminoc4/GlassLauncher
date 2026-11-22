// 极简 JSON（只服务本应用的两种文件：条目数组、设置对象）。
// 支持 null/bool/number/string/array/object；字符串支持 \" \\ \/ \b \f \n \r \t \uXXXX。
#pragma once

#include <string>
#include <vector>
#include <map>
#include <memory>
#include <cstdint>
#include <cwchar>

namespace gl {

struct JsonValue;
using JsonPtr = std::shared_ptr<JsonValue>;

struct JsonValue
{
    enum class Type { Null, Bool, Number, String, Array, Object };

    Type type = Type::Null;
    bool boolean = false;
    double number = 0;
    std::wstring string;
    std::vector<JsonPtr> array;
    std::vector<std::pair<std::wstring, JsonPtr>> object;

    bool IsNull() const { return type == Type::Null; }

    const JsonPtr* Find(const wchar_t* key) const
    {
        if (type != Type::Object) return nullptr;
        for (const auto& kv : object)
        {
            if (kv.first == key) return &kv.second;
        }
        return nullptr;
    }

    std::wstring GetString(const wchar_t* key, const std::wstring& fallback = L"") const
    {
        const JsonPtr* v = Find(key);
        return (v && (*v)->type == Type::String) ? (*v)->string : fallback;
    }

    int GetInt(const wchar_t* key, int fallback = 0) const
    {
        const JsonPtr* v = Find(key);
        return (v && (*v)->type == Type::Number) ? (int)(*v)->number : fallback;
    }

    double GetNumber(const wchar_t* key, double fallback = 0) const
    {
        const JsonPtr* v = Find(key);
        return (v && (*v)->type == Type::Number) ? (*v)->number : fallback;
    }

    bool GetBool(const wchar_t* key, bool fallback = false) const
    {
        const JsonPtr* v = Find(key);
        return (v && (*v)->type == Type::Bool) ? (*v)->boolean : fallback;
    }
};

class JsonParser
{
public:
    static JsonPtr Parse(const std::wstring& text, std::wstring* error = nullptr)
    {
        JsonParser p(text);
        JsonPtr v = p.ParseValue();
        if (!v && error) *error = p.error_;
        return v;
    }

private:
    explicit JsonParser(const std::wstring& s) : s_(s) {}

    const std::wstring& s_;
    size_t i_ = 0;
    std::wstring error_;

    void Skip()
    {
        while (i_ < s_.size() && (s_[i_] == L' ' || s_[i_] == L'\t' || s_[i_] == L'\n' || s_[i_] == L'\r')) i_++;
    }

    bool Fail(const wchar_t* msg)
    {
        if (error_.empty()) error_ = msg;
        return false;
    }

    JsonPtr ParseValue()
    {
        Skip();
        if (i_ >= s_.size()) { Fail(L"unexpected end"); return nullptr; }
        wchar_t c = s_[i_];
        if (c == L'{') return ParseObject();
        if (c == L'[') return ParseArray();
        if (c == L'"') return ParseString();
        if (c == L't' && Match(L"true")) return MakeBool(true);
        if (c == L'f' && Match(L"false")) return MakeBool(false);
        if (c == L'n' && Match(L"null")) { i_ += 4; return std::make_shared<JsonValue>(); }
        if (c == L'-' || (c >= L'0' && c <= L'9')) return ParseNumber();
        Fail(L"unexpected token");
        return nullptr;
    }

    bool Match(const wchar_t* word)
    {
        size_t len = wcslen(word);
        if (s_.compare(i_, len, word) != 0) return false;
        i_ += len;
        return true;
    }

    static JsonPtr MakeBool(bool b)
    {
        auto v = std::make_shared<JsonValue>();
        v->type = JsonValue::Type::Bool;
        v->boolean = b;
        return v;
    }

    JsonPtr ParseObject()
    {
        auto v = std::make_shared<JsonValue>();
        v->type = JsonValue::Type::Object;
        i_++; // {
        for (;;)
        {
            Skip();
            if (i_ < s_.size() && s_[i_] == L'}') { i_++; return v; }
            if (i_ >= s_.size() || s_[i_] != L'"') { Fail(L"object key expected"); return nullptr; }
            auto key = ParseString();
            if (!key) return nullptr;
            Skip();
            if (i_ >= s_.size() || s_[i_] != L':') { Fail(L"':' expected"); return nullptr; }
            i_++;
            auto val = ParseValue();
            if (!val) return nullptr;
            v->object.emplace_back(key->string, val);
            Skip();
            if (i_ < s_.size() && s_[i_] == L',') { i_++; continue; }
            if (i_ < s_.size() && s_[i_] == L'}') { i_++; return v; }
            Fail(L"',' or '}' expected");
            return nullptr;
        }
    }

    JsonPtr ParseArray()
    {
        auto v = std::make_shared<JsonValue>();
        v->type = JsonValue::Type::Array;
        i_++; // [
        for (;;)
        {
            Skip();
            if (i_ < s_.size() && s_[i_] == L']') { i_++; return v; }
            auto val = ParseValue();
            if (!val) return nullptr;
            v->array.push_back(val);
            Skip();
            if (i_ < s_.size() && s_[i_] == L',') { i_++; continue; }
            if (i_ < s_.size() && s_[i_] == L']') { i_++; return v; }
            Fail(L"',' or ']' expected");
            return nullptr;
        }
    }

    JsonPtr ParseString()
    {
        auto v = std::make_shared<JsonValue>();
        v->type = JsonValue::Type::String;
        i_++; // "
        std::wstring out;
        while (i_ < s_.size())
        {
            wchar_t c = s_[i_++];
            if (c == L'"') { v->string = std::move(out); return v; }
            if (c != L'\\') { out.push_back(c); continue; }
            if (i_ >= s_.size()) break;
            wchar_t e = s_[i_++];
            switch (e)
            {
                case L'"': out.push_back(L'"'); break;
                case L'\\': out.push_back(L'\\'); break;
                case L'/': out.push_back(L'/'); break;
                case L'b': out.push_back(L'\b'); break;
                case L'f': out.push_back(L'\f'); break;
                case L'n': out.push_back(L'\n'); break;
                case L'r': out.push_back(L'\r'); break;
                case L't': out.push_back(L'\t'); break;
                case L'u':
                {
                    unsigned code = 0;
                    for (int k = 0; k < 4 && i_ < s_.size(); k++)
                    {
                        wchar_t h = s_[i_++];
                        code <<= 4;
                        if (h >= L'0' && h <= L'9') code |= (unsigned)(h - L'0');
                        else if (h >= L'a' && h <= L'f') code |= (unsigned)(h - L'a' + 10);
                        else if (h >= L'A' && h <= L'F') code |= (unsigned)(h - L'A' + 10);
                    }
                    // 代理对：高代理 + 后续 \u 低代理
                    if (code >= 0xD800 && code <= 0xDBFF && i_ + 1 < s_.size() && s_[i_] == L'\\' && s_[i_ + 1] == L'u')
                    {
                        i_ += 2;
                        unsigned low = 0;
                        for (int k = 0; k < 4 && i_ < s_.size(); k++)
                        {
                            wchar_t h = s_[i_++];
                            low <<= 4;
                            if (h >= L'0' && h <= L'9') low |= (unsigned)(h - L'0');
                            else if (h >= L'a' && h <= L'f') low |= (unsigned)(h - L'a' + 10);
                            else if (h >= L'A' && h <= L'F') low |= (unsigned)(h - L'A' + 10);
                        }
                        code = 0x10000 + ((code - 0xD800) << 10) + (low - 0xDC00);
                    }

                    if (code < 0x10000)
                    {
                        out.push_back((wchar_t)code);
                    }
                    else
                    {
                        code -= 0x10000;
                        out.push_back((wchar_t)(0xD800 + (code >> 10)));
                        out.push_back((wchar_t)(0xDC00 + (code & 0x3FF)));
                    }
                    break;
                }
                default: out.push_back(e); break;
            }
        }
        Fail(L"unterminated string");
        return nullptr;
    }

    JsonPtr ParseNumber()
    {
        size_t start = i_;
        if (s_[i_] == L'-') i_++;
        while (i_ < s_.size() && ((s_[i_] >= L'0' && s_[i_] <= L'9') || s_[i_] == L'.' || s_[i_] == L'e' || s_[i_] == L'E' || s_[i_] == L'+' || s_[i_] == L'-')) i_++;
        auto v = std::make_shared<JsonValue>();
        v->type = JsonValue::Type::Number;
        v->number = _wtof(s_.substr(start, i_ - start).c_str());
        return v;
    }
};

// ── 写 ────────────────────────────────────────────────────────
inline void JsonEscape(const std::wstring& in, std::wstring& out)
{
    out.push_back(L'"');
    for (wchar_t c : in)
    {
        switch (c)
        {
            case L'"': out += L"\\\""; break;
            case L'\\': out += L"\\\\"; break;
            case L'\n': out += L"\\n"; break;
            case L'\r': out += L"\\r"; break;
            case L'\t': out += L"\\t"; break;
            default: out.push_back(c); break;
        }
    }
    out.push_back(L'"');
}

} // namespace gl

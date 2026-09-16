#include "InstallerActions.h"

#include <windows.h>
#include <objbase.h>

#include <algorithm>
#include <array>
#include <cstddef>
#include <cstring>
#include <cwctype>
#include <limits>
#include <optional>
#include <string_view>
#include <vector>

#pragma comment(lib, "Ole32.lib")
#pragma comment(lib, "Advapi32.lib")

namespace
{
    using WeezTail::Setup::ActionResult;
    using WeezTail::Setup::InstallerSession;

    constexpr wchar_t InstallConfigFileName[] = L"WeezTail.install.json";
    constexpr wchar_t UserSelectionFileName[] = L"WeezTail.msi-user.json";
    constexpr wchar_t SetupDirectoryName[] = L"WeezTailSetup";
    constexpr wchar_t StorageDirectoryName[] = L"WeezTail";
    constexpr wchar_t LegacyInstallConfigFileName[] = L"LogReader.install.json";
    constexpr wchar_t LegacyUserSelectionFileName[] = L"LogReader.msi-user.json";
    constexpr wchar_t LegacySetupDirectoryName[] = L"LogReaderSetup";
    constexpr wchar_t LegacyStorageDirectoryName[] = L"LogReader";
    constexpr wchar_t LegacyMainExecutableComponentCode[] =
        L"{FE07166C-F174-4245-A4A8-BD7EED904D05}";
    constexpr wchar_t DataDirectoryName[] = L"Data";
    constexpr wchar_t CacheDirectoryName[] = L"Cache";

    bool EqualsIgnoreCase(std::wstring_view left, std::wstring_view right)
    {
        return left.size() == right.size()
            && CompareStringOrdinal(
                left.data(),
                static_cast<int>(left.size()),
                right.data(),
                static_cast<int>(right.size()),
                TRUE) == CSTR_EQUAL;
    }

    bool ContainsIgnoreCase(std::wstring_view value, std::wstring_view expected)
    {
        if (expected.empty() || value.size() < expected.size())
        {
            return false;
        }

        for (size_t index = 0; index + expected.size() <= value.size(); ++index)
        {
            if (EqualsIgnoreCase(value.substr(index, expected.size()), expected))
            {
                return true;
            }
        }

        return false;
    }

    std::wstring TrimTrailingSlash(std::wstring value)
    {
        while (value.size() > 3 && (value.back() == L'\\' || value.back() == L'/'))
        {
            value.pop_back();
        }

        return value;
    }

    std::wstring JoinPath(std::wstring left, std::wstring_view right)
    {
        left = TrimTrailingSlash(std::move(left));
        if (!left.empty() && left.back() != L'\\')
        {
            left.push_back(L'\\');
        }

        left.append(right);
        return left;
    }

    std::wstring ParentPath(const std::wstring& path)
    {
        const auto position = path.find_last_of(L'\\');
        if (position == std::wstring::npos)
        {
            return {};
        }
        if (position == 2 && path.size() >= 3 && path[1] == L':')
        {
            return path.substr(0, 3);
        }
        if (position <= 2)
        {
            return {};
        }

        return path.substr(0, position);
    }

    std::wstring GetEnvironmentPath(const wchar_t* name)
    {
        const DWORD required = GetEnvironmentVariableW(name, nullptr, 0);
        if (required == 0)
        {
            return {};
        }

        std::vector<wchar_t> buffer(required);
        const DWORD written = GetEnvironmentVariableW(name, buffer.data(), required);
        if (written == 0 || written >= required)
        {
            return {};
        }

        return TrimTrailingSlash(std::wstring(buffer.data(), written));
    }

    bool IsReservedSegment(std::wstring_view segment)
    {
        const auto dot = segment.find(L'.');
        const auto stem = segment.substr(0, dot);
        static constexpr std::array<std::wstring_view, 4> reserved = {
            L"CON", L"PRN", L"AUX", L"NUL"
        };
        for (const auto name : reserved)
        {
            if (EqualsIgnoreCase(stem, name))
            {
                return true;
            }
        }

        if (stem.size() == 4
            && (EqualsIgnoreCase(stem.substr(0, 3), L"COM")
                || EqualsIgnoreCase(stem.substr(0, 3), L"LPT"))
            && stem[3] >= L'1'
            && stem[3] <= L'9')
        {
            return true;
        }

        return false;
    }

    std::optional<std::wstring> NormalizeCleanupPath(std::wstring path)
    {
        std::replace(path.begin(), path.end(), L'/', L'\\');
        if (path.size() < 3
            || !std::iswalpha(path[0])
            || path[1] != L':'
            || path[2] != L'\\')
        {
            return std::nullopt;
        }

        size_t segmentStart = 3;
        for (size_t index = 3; index <= path.size(); ++index)
        {
            if (index < path.size())
            {
                const wchar_t character = path[index];
                if (character < 0x20
                    || character == L'<'
                    || character == L'>'
                    || character == L'"'
                    || character == L'|'
                    || character == L'?'
                    || character == L'*'
                    || character == L':')
                {
                    return std::nullopt;
                }
                if (character != L'\\')
                {
                    continue;
                }
            }

            const auto segment = std::wstring_view(path).substr(segmentStart, index - segmentStart);
            const bool finalEmptySegment = segment.empty() && index == path.size();
            if (!finalEmptySegment
                && (segment.empty()
                    || segment == L"."
                    || segment == L".."
                    || segment.back() == L'.'
                    || segment.back() == L' '
                    || IsReservedSegment(segment)))
            {
                return std::nullopt;
            }
            segmentStart = index + 1;
        }

        path = TrimTrailingSlash(std::move(path));
        const DWORD required = GetFullPathNameW(path.c_str(), 0, nullptr, nullptr);
        if (required == 0)
        {
            return std::nullopt;
        }

        std::vector<wchar_t> buffer(required);
        const DWORD written = GetFullPathNameW(path.c_str(), required, buffer.data(), nullptr);
        if (written == 0 || written >= required)
        {
            return std::nullopt;
        }

        return TrimTrailingSlash(std::wstring(buffer.data(), written));
    }

    bool IsSameOrDescendant(const std::wstring& path, const std::wstring& root)
    {
        if (EqualsIgnoreCase(path, root))
        {
            return true;
        }

        std::wstring prefix = TrimTrailingSlash(root);
        prefix.push_back(L'\\');
        return path.size() > prefix.size()
            && EqualsIgnoreCase(std::wstring_view(path).substr(0, prefix.size()), prefix);
    }

    bool HasApplicationSegment(const std::wstring& path, const std::wstring& root)
    {
        if (!IsSameOrDescendant(path, root) || EqualsIgnoreCase(path, root))
        {
            return false;
        }

        std::wstring relative = path.substr(TrimTrailingSlash(root).size());
        while (!relative.empty() && relative.front() == L'\\')
        {
            relative.erase(relative.begin());
        }

        size_t start = 0;
        while (start <= relative.size())
        {
            const size_t end = relative.find(L'\\', start);
            const auto segment = std::wstring_view(relative).substr(
                start,
                end == std::wstring::npos ? relative.size() - start : end - start);
            if (ContainsIgnoreCase(segment, StorageDirectoryName)
                || ContainsIgnoreCase(segment, LegacyStorageDirectoryName))
            {
                return true;
            }
            if (end == std::wstring::npos)
            {
                break;
            }
            start = end + 1;
        }

        return false;
    }

    bool IsProtectedPath(const std::wstring& path)
    {
        for (const auto* variable : {L"ProgramFiles", L"ProgramFiles(x86)", L"WINDIR"})
        {
            const auto root = NormalizeCleanupPath(GetEnvironmentPath(variable));
            if (root && IsSameOrDescendant(path, *root))
            {
                return true;
            }
        }

        return false;
    }

    bool IsUnsafeBroadPath(const std::wstring& path)
    {
        if (path.size() == 3 && path[1] == L':' && path[2] == L'\\')
        {
            return true;
        }

        for (const auto* variable : {L"USERPROFILE", L"LOCALAPPDATA", L"APPDATA", L"TEMP"})
        {
            const auto root = NormalizeCleanupPath(GetEnvironmentPath(variable));
            if (root && IsSameOrDescendant(path, *root))
            {
                return !HasApplicationSegment(path, *root);
            }
        }

        return false;
    }

    bool Exists(const std::wstring& path)
    {
        return GetFileAttributesW(path.c_str()) != INVALID_FILE_ATTRIBUTES;
    }

    bool FileExists(const std::wstring& path)
    {
        const DWORD attributes = GetFileAttributesW(path.c_str());
        return attributes != INVALID_FILE_ATTRIBUTES
            && (attributes & FILE_ATTRIBUTE_DIRECTORY) == 0;
    }

    bool FolderExists(const std::wstring& path)
    {
        const DWORD attributes = GetFileAttributesW(path.c_str());
        return attributes != INVALID_FILE_ATTRIBUTES
            && (attributes & FILE_ATTRIBUTE_DIRECTORY) != 0;
    }

    bool IsSafeTree(const std::wstring& path)
    {
        const auto normalized = NormalizeCleanupPath(path);
        if (!normalized || IsProtectedPath(*normalized) || IsUnsafeBroadPath(*normalized))
        {
            return false;
        }

        std::wstring ancestor = *normalized;
        while (!ancestor.empty())
        {
            const DWORD attributes = GetFileAttributesW(ancestor.c_str());
            if (attributes != INVALID_FILE_ATTRIBUTES
                && (attributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0)
            {
                return false;
            }
            const std::wstring parent = ParentPath(ancestor);
            if (parent.empty() || EqualsIgnoreCase(parent, ancestor))
            {
                break;
            }
            ancestor = parent;
        }

        if (!FolderExists(*normalized))
        {
            return true;
        }

        WIN32_FIND_DATAW item{};
        const std::wstring pattern = JoinPath(*normalized, L"*");
        const HANDLE find = FindFirstFileW(pattern.c_str(), &item);
        if (find == INVALID_HANDLE_VALUE)
        {
            return GetLastError() == ERROR_FILE_NOT_FOUND;
        }

        bool safe = true;
        do
        {
            if (wcscmp(item.cFileName, L".") == 0 || wcscmp(item.cFileName, L"..") == 0)
            {
                continue;
            }
            if ((item.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0)
            {
                safe = false;
                break;
            }
            if ((item.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0
                && !IsSafeTree(JoinPath(*normalized, item.cFileName)))
            {
                safe = false;
                break;
            }
        } while (FindNextFileW(find, &item));

        FindClose(find);
        return safe;
    }

    bool DecodeUtf8(const char* bytes, int length, std::wstring& output)
    {
        if (length == 0)
        {
            output.clear();
            return true;
        }
        const int required = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, bytes, length, nullptr, 0);
        if (required <= 0)
        {
            return false;
        }
        output.resize(static_cast<size_t>(required));
        return MultiByteToWideChar(
            CP_UTF8,
            MB_ERR_INVALID_CHARS,
            bytes,
            length,
            output.data(),
            required) == required;
    }

    std::optional<std::wstring> ReadTextFile(const std::wstring& path)
    {
        const HANDLE file = CreateFileW(
            path.c_str(),
            GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            nullptr,
            OPEN_EXISTING,
            FILE_ATTRIBUTE_NORMAL,
            nullptr);
        if (file == INVALID_HANDLE_VALUE)
        {
            return std::nullopt;
        }

        LARGE_INTEGER size{};
        if (!GetFileSizeEx(file, &size) || size.QuadPart < 0 || size.QuadPart > 4 * 1024 * 1024)
        {
            CloseHandle(file);
            return std::nullopt;
        }

        std::vector<char> bytes(static_cast<size_t>(size.QuadPart));
        DWORD read = 0;
        const bool readOk = bytes.empty()
            || (ReadFile(file, bytes.data(), static_cast<DWORD>(bytes.size()), &read, nullptr)
                && read == bytes.size());
        CloseHandle(file);
        if (!readOk)
        {
            return std::nullopt;
        }

        if (bytes.size() >= 2
            && static_cast<unsigned char>(bytes[0]) == 0xFF
            && static_cast<unsigned char>(bytes[1]) == 0xFE)
        {
            if ((bytes.size() - 2) % sizeof(wchar_t) != 0)
            {
                return std::nullopt;
            }
            return std::wstring(
                reinterpret_cast<const wchar_t*>(bytes.data() + 2),
                (bytes.size() - 2) / sizeof(wchar_t));
        }

        size_t offset = 0;
        if (bytes.size() >= 3
            && static_cast<unsigned char>(bytes[0]) == 0xEF
            && static_cast<unsigned char>(bytes[1]) == 0xBB
            && static_cast<unsigned char>(bytes[2]) == 0xBF)
        {
            offset = 3;
        }
        std::wstring decoded;
        if (!DecodeUtf8(bytes.data() + offset, static_cast<int>(bytes.size() - offset), decoded))
        {
            return std::nullopt;
        }
        return decoded;
    }

    int HexDigit(wchar_t value)
    {
        if (value >= L'0' && value <= L'9') return value - L'0';
        if (value >= L'a' && value <= L'f') return value - L'a' + 10;
        if (value >= L'A' && value <= L'F') return value - L'A' + 10;
        return -1;
    }

    std::optional<std::wstring> DecodeJsonString(std::wstring_view json, size_t start)
    {
        std::wstring decoded;
        for (size_t index = start; index < json.size(); ++index)
        {
            const wchar_t character = json[index];
            if (character == L'"')
            {
                return decoded;
            }
            if (character < 0x20)
            {
                return std::nullopt;
            }
            if (character != L'\\')
            {
                decoded.push_back(character);
                continue;
            }

            if (++index >= json.size())
            {
                return std::nullopt;
            }
            switch (json[index])
            {
                case L'"': decoded.push_back(L'"'); break;
                case L'\\': decoded.push_back(L'\\'); break;
                case L'/': decoded.push_back(L'/'); break;
                case L'b': decoded.push_back(L'\b'); break;
                case L'f': decoded.push_back(L'\f'); break;
                case L'n': decoded.push_back(L'\n'); break;
                case L'r': decoded.push_back(L'\r'); break;
                case L't': decoded.push_back(L'\t'); break;
                case L'u':
                {
                    if (index + 4 >= json.size())
                    {
                        return std::nullopt;
                    }
                    unsigned int codeUnit = 0;
                    for (size_t digitIndex = 1; digitIndex <= 4; ++digitIndex)
                    {
                        const int digit = HexDigit(json[index + digitIndex]);
                        if (digit < 0)
                        {
                            return std::nullopt;
                        }
                        codeUnit = (codeUnit * 16U) + static_cast<unsigned int>(digit);
                    }
                    decoded.push_back(static_cast<wchar_t>(codeUnit));
                    index += 4;
                    break;
                }
                default:
                    return std::nullopt;
            }
        }

        return std::nullopt;
    }

    std::optional<std::wstring> ExtractJsonStringValue(
        std::wstring_view json,
        std::wstring_view propertyName)
    {
        std::wstring marker = L"\"";
        marker.append(propertyName);
        marker.push_back(L'"');
        const size_t property = json.find(marker);
        if (property == std::wstring_view::npos)
        {
            return std::nullopt;
        }
        const size_t colon = json.find(L':', property + marker.size());
        if (colon == std::wstring_view::npos)
        {
            return std::nullopt;
        }
        const size_t quote = json.find(L'"', colon + 1);
        if (quote == std::wstring_view::npos)
        {
            return std::nullopt;
        }
        return DecodeJsonString(json, quote + 1);
    }

    std::optional<std::wstring> LoadJsonStringValue(
        InstallerSession& session,
        const std::wstring& path,
        std::wstring_view propertyName)
    {
        const auto json = ReadTextFile(path);
        if (!json)
        {
            session.Log(L"could not read file=" + path + L" property=" + std::wstring(propertyName));
            return std::nullopt;
        }
        const auto value = ExtractJsonStringValue(*json, propertyName);
        if (!value)
        {
            session.Log(L"could not parse property=" + std::wstring(propertyName) + L" file=" + path);
        }
        return value;
    }

    bool EncodeUtf8(const std::wstring& value, std::vector<char>& output)
    {
        if (value.empty())
        {
            output.clear();
            return true;
        }
        const int required = WideCharToMultiByte(
            CP_UTF8,
            WC_ERR_INVALID_CHARS,
            value.data(),
            static_cast<int>(value.size()),
            nullptr,
            0,
            nullptr,
            nullptr);
        if (required <= 0)
        {
            return false;
        }
        output.resize(static_cast<size_t>(required));
        return WideCharToMultiByte(
            CP_UTF8,
            WC_ERR_INVALID_CHARS,
            value.data(),
            static_cast<int>(value.size()),
            output.data(),
            required,
            nullptr,
            nullptr) == required;
    }

    std::wstring EscapeJsonString(const std::wstring& value)
    {
        std::wstring escaped;
        escaped.reserve(value.size() + 8);
        for (const wchar_t character : value)
        {
            switch (character)
            {
                case L'"': escaped.append(L"\\\""); break;
                case L'\\': escaped.append(L"\\\\"); break;
                case L'\b': escaped.append(L"\\b"); break;
                case L'\f': escaped.append(L"\\f"); break;
                case L'\n': escaped.append(L"\\n"); break;
                case L'\r': escaped.append(L"\\r"); break;
                case L'\t': escaped.append(L"\\t"); break;
                default: escaped.push_back(character); break;
            }
        }
        return escaped;
    }

    bool EnsureDirectory(const std::wstring& path)
    {
        if (FolderExists(path))
        {
            return true;
        }
        const std::wstring parent = ParentPath(path);
        if (!parent.empty() && !EqualsIgnoreCase(parent, path) && !EnsureDirectory(parent))
        {
            return false;
        }
        return CreateDirectoryW(path.c_str(), nullptr)
            || GetLastError() == ERROR_ALREADY_EXISTS;
    }

    bool WriteBytes(const std::wstring& path, const std::vector<char>& bytes)
    {
        const HANDLE file = CreateFileW(
            path.c_str(),
            GENERIC_WRITE,
            0,
            nullptr,
            CREATE_ALWAYS,
            FILE_ATTRIBUTE_NORMAL,
            nullptr);
        if (file == INVALID_HANDLE_VALUE)
        {
            return false;
        }
        DWORD written = 0;
        const bool result = bytes.empty()
            || (WriteFile(file, bytes.data(), static_cast<DWORD>(bytes.size()), &written, nullptr)
                && written == bytes.size());
        const bool flushed = result && FlushFileBuffers(file);
        CloseHandle(file);
        return flushed;
    }

    std::wstring CurrentSelectionPath()
    {
        return JoinPath(JoinPath(GetEnvironmentPath(L"LOCALAPPDATA"), SetupDirectoryName), UserSelectionFileName);
    }

    std::wstring LegacySelectionPath()
    {
        return JoinPath(
            JoinPath(GetEnvironmentPath(L"LOCALAPPDATA"), LegacySetupDirectoryName),
            LegacyUserSelectionFileName);
    }

    std::wstring LegacyDefaultStorageRoot()
    {
        return JoinPath(GetEnvironmentPath(L"LOCALAPPDATA"), LegacyStorageDirectoryName);
    }

    std::wstring CurrentCachePath()
    {
        return JoinPath(
            JoinPath(GetEnvironmentPath(L"LOCALAPPDATA"), StorageDirectoryName),
            CacheDirectoryName);
    }

    std::wstring InstallConfigPath(InstallerSession& session)
    {
        return JoinPath(session.GetProperty(L"INSTALLFOLDER"), InstallConfigFileName);
    }

    bool InstallUsesPerUserChoice(InstallerSession& session)
    {
        const auto mode = LoadJsonStringValue(session, InstallConfigPath(session), L"storageMode");
        const bool result = mode && EqualsIgnoreCase(*mode, L"PerUserChoice");
        session.Log(L"InstallUsesPerUserChoice=" + std::wstring(result ? L"True" : L"False"));
        return result;
    }

    std::wstring EffectiveSelectionPath()
    {
        const std::wstring current = CurrentSelectionPath();
        if (FileExists(current))
        {
            return current;
        }
        const std::wstring legacy = LegacySelectionPath();
        return FileExists(legacy) ? legacy : current;
    }

    std::wstring ResolvePerUserStorageRoot(InstallerSession& session)
    {
        const std::wstring current = CurrentSelectionPath();
        if (FileExists(current))
        {
            const auto value = LoadJsonStringValue(session, current, L"storageRootPath");
            return value ? TrimTrailingSlash(*value) : std::wstring{};
        }
        const std::wstring legacy = LegacySelectionPath();
        if (FileExists(legacy))
        {
            const auto value = LoadJsonStringValue(session, legacy, L"storageRootPath");
            return value ? TrimTrailingSlash(*value) : std::wstring{};
        }
        const std::wstring fallback = LegacyDefaultStorageRoot();
        return FolderExists(fallback) ? fallback : std::wstring{};
    }

    std::wstring ResolveCleanupStorageRoot(InstallerSession& session)
    {
        std::wstring raw;
        if (InstallUsesPerUserChoice(session))
        {
            raw = ResolvePerUserStorageRoot(session);
        }
        else
        {
            const auto value = LoadJsonStringValue(session, InstallConfigPath(session), L"storageRootPath");
            raw = value ? TrimTrailingSlash(*value) : std::wstring{};
        }

        const auto normalized = NormalizeCleanupPath(raw);
        if (!normalized || IsProtectedPath(*normalized) || IsUnsafeBroadPath(*normalized))
        {
            session.Log(L"rejected cleanup storage root=" + raw);
            return {};
        }
        session.Log(L"ResolveCleanupStorageRoot=" + *normalized);
        return *normalized;
    }

    std::wstring ResolveLegacyInstallConfig(InstallerSession& session)
    {
        std::wstring products = session.GetProperty(L"WIX_UPGRADE_DETECTED");
        size_t start = 0;
        while (start <= products.size())
        {
            const size_t end = products.find(L';', start);
            std::wstring product = products.substr(
                start,
                end == std::wstring::npos ? products.size() - start : end - start);
            if (!product.empty())
            {
                const std::wstring componentPath = session.GetComponentPath(
                    product,
                    LegacyMainExecutableComponentCode);
                if (!componentPath.empty())
                {
                    const std::wstring candidate = JoinPath(
                        ParentPath(componentPath),
                        LegacyInstallConfigFileName);
                    if (FileExists(candidate))
                    {
                        return candidate;
                    }
                }
            }
            if (end == std::wstring::npos)
            {
                break;
            }
            start = end + 1;
        }
        return {};
    }

    void ResolveLegacyStorageCandidate(
        InstallerSession& session,
        std::wstring& selectionSource,
        std::wstring& storageRoot)
    {
        selectionSource.clear();
        storageRoot.clear();
        const std::wstring config = ResolveLegacyInstallConfig(session);
        const std::wstring legacySelection = LegacySelectionPath();
        if (!config.empty())
        {
            const auto mode = LoadJsonStringValue(session, config, L"storageMode");
            if (mode && EqualsIgnoreCase(*mode, L"Absolute"))
            {
                const auto root = LoadJsonStringValue(session, config, L"storageRootPath");
                storageRoot = root ? TrimTrailingSlash(*root) : std::wstring{};
            }
            else if (mode && EqualsIgnoreCase(*mode, L"PerUserChoice"))
            {
                if (FileExists(legacySelection))
                {
                    selectionSource = legacySelection;
                }
                else if (FolderExists(LegacyDefaultStorageRoot()))
                {
                    storageRoot = LegacyDefaultStorageRoot();
                }
            }
            else
            {
                session.Log(L"ignored unsupported legacy storage mode");
            }
            return;
        }

        if (FileExists(legacySelection))
        {
            selectionSource = legacySelection;
            return;
        }
        if (FolderExists(LegacyDefaultStorageRoot()))
        {
            storageRoot = LegacyDefaultStorageRoot();
        }
    }

    bool DeleteTree(const std::wstring& path)
    {
        const DWORD attributes = GetFileAttributesW(path.c_str());
        if (attributes == INVALID_FILE_ATTRIBUTES)
        {
            return GetLastError() == ERROR_FILE_NOT_FOUND || GetLastError() == ERROR_PATH_NOT_FOUND;
        }
        if ((attributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0)
        {
            return false;
        }
        if ((attributes & FILE_ATTRIBUTE_DIRECTORY) == 0)
        {
            SetFileAttributesW(path.c_str(), FILE_ATTRIBUTE_NORMAL);
            return DeleteFileW(path.c_str()) != FALSE;
        }

        WIN32_FIND_DATAW item{};
        const HANDLE find = FindFirstFileW(JoinPath(path, L"*").c_str(), &item);
        if (find != INVALID_HANDLE_VALUE)
        {
            bool succeeded = true;
            do
            {
                if (wcscmp(item.cFileName, L".") == 0 || wcscmp(item.cFileName, L"..") == 0)
                {
                    continue;
                }
                const std::wstring child = JoinPath(path, item.cFileName);
                if ((item.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0
                    || !DeleteTree(child))
                {
                    succeeded = false;
                    break;
                }
            } while (FindNextFileW(find, &item));
            FindClose(find);
            if (!succeeded)
            {
                return false;
            }
        }
        else if (GetLastError() != ERROR_FILE_NOT_FOUND)
        {
            return false;
        }

        SetFileAttributesW(path.c_str(), FILE_ATTRIBUTE_NORMAL);
        return RemoveDirectoryW(path.c_str()) != FALSE;
    }

    bool IsFixtureMutationAllowed(InstallerSession& session, const std::wstring& target)
    {
        const auto fixture = session.FixtureRoot();
        return !fixture || WeezTail::Setup::IsAllowedFixtureMutation(*fixture, target);
    }

    bool DeleteApprovedSelection(
        InstallerSession& session,
        const std::wstring& path,
        const std::wstring& expectedPath)
    {
        const auto normalized = NormalizeCleanupPath(path);
        if (!normalized
            || !EqualsIgnoreCase(*normalized, expectedPath)
            || !IsFixtureMutationAllowed(session, *normalized)
            || !IsSafeTree(*normalized))
        {
            session.Log(L"DeleteFile refused an ineligible selection file.");
            return false;
        }
        if (!FileExists(*normalized))
        {
            return true;
        }
        SetFileAttributesW(normalized->c_str(), FILE_ATTRIBUTE_NORMAL);
        if (!DeleteFileW(normalized->c_str()))
        {
            session.Log(L"DeleteFile failed path=" + *normalized);
            return false;
        }
        return true;
    }

    struct MigrationPlan
    {
        std::wstring Token;
        std::wstring SelectionPath;
        std::wstring Payload;
    };

    std::optional<std::wstring> CreateTransactionToken()
    {
        GUID value{};
        if (FAILED(CoCreateGuid(&value)))
        {
            return std::nullopt;
        }
        std::array<wchar_t, 40> buffer{};
        const int written = StringFromGUID2(value, buffer.data(), static_cast<int>(buffer.size()));
        if (written <= 3)
        {
            return std::nullopt;
        }
        return std::wstring(buffer.data() + 1, static_cast<size_t>(written) - 3);
    }

    std::wstring EncodeMigrationPlan(const MigrationPlan& plan)
    {
        std::wstring encoded = L"WTR1";
        for (const auto* field : {&plan.Token, &plan.SelectionPath, &plan.Payload})
        {
            encoded.push_back(L'|');
            encoded.append(std::to_wstring(field->size()));
            encoded.push_back(L':');
            encoded.append(*field);
        }
        return encoded;
    }

    std::optional<MigrationPlan> DecodeMigrationPlan(const std::wstring& encoded)
    {
        if (!encoded.starts_with(L"WTR1"))
        {
            return std::nullopt;
        }
        size_t position = 4;
        std::array<std::wstring, 3> fields;
        for (auto& field : fields)
        {
            if (position >= encoded.size() || encoded[position++] != L'|')
            {
                return std::nullopt;
            }
            const size_t colon = encoded.find(L':', position);
            if (colon == std::wstring::npos || colon == position)
            {
                return std::nullopt;
            }
            size_t length = 0;
            for (size_t index = position; index < colon; ++index)
            {
                if (encoded[index] < L'0' || encoded[index] > L'9')
                {
                    return std::nullopt;
                }
                length = (length * 10) + static_cast<size_t>(encoded[index] - L'0');
                if (length > 30000)
                {
                    return std::nullopt;
                }
            }
            position = colon + 1;
            if (length > encoded.size() - position)
            {
                return std::nullopt;
            }
            field = encoded.substr(position, length);
            position += length;
        }
        if (position != encoded.size())
        {
            return std::nullopt;
        }
        return MigrationPlan{
            std::move(fields[0]),
            std::move(fields[1]),
            std::move(fields[2])
        };
    }

    std::wstring MigrationMarkerPath(const MigrationPlan& plan)
    {
        return plan.SelectionPath + L".migration-" + plan.Token + L".pending";
    }

    std::wstring MigrationTemporaryPath(const MigrationPlan& plan)
    {
        return plan.SelectionPath + L".migration-" + plan.Token + L".tmp";
    }

    bool HasExpectedSelectionSuffix(const std::wstring& selectionPath)
    {
        const std::wstring suffix = L"\\WeezTailSetup\\WeezTail.msi-user.json";
        return selectionPath.size() > suffix.size()
            && EqualsIgnoreCase(
                std::wstring_view(selectionPath).substr(selectionPath.size() - suffix.size()),
                suffix);
    }

    bool ValidateMigrationPlan(InstallerSession& session, const MigrationPlan& plan)
    {
        const auto selection = NormalizeCleanupPath(plan.SelectionPath);
        const auto rootValue = ExtractJsonStringValue(plan.Payload, L"storageRootPath");
        const auto root = rootValue ? NormalizeCleanupPath(*rootValue) : std::nullopt;
        if (plan.Token.empty()
            || plan.Token.size() > 64
            || !selection
            || !HasExpectedSelectionSuffix(*selection)
            || !root
            || IsProtectedPath(*root)
            || IsUnsafeBroadPath(*root)
            || !IsSafeTree(*selection)
            || !IsFixtureMutationAllowed(session, *selection))
        {
            session.Log(L"rejected invalid migration action data.");
            return false;
        }
        const std::wstring expectedPayload = L"{\"storageRootPath\":\""
            + EscapeJsonString(*root)
            + L"\"}";
        if (plan.Payload != expectedPayload)
        {
            session.Log(L"rejected non-canonical migration payload.");
            return false;
        }
        return true;
    }

    bool RemoveTransactionFile(
        InstallerSession& session,
        const std::wstring& path,
        const wchar_t* description)
    {
        if (!Exists(path))
        {
            return true;
        }
        if (!IsSafeTree(path) || !IsFixtureMutationAllowed(session, path))
        {
            session.Log(std::wstring(L"preserved unsafe ") + description + L"=" + path);
            return false;
        }
        SetFileAttributesW(path.c_str(), FILE_ATTRIBUTE_NORMAL);
        if (!DeleteFileW(path.c_str()))
        {
            session.Log(std::wstring(L"could not remove ") + description + L"=" + path);
            return false;
        }
        return true;
    }

    bool WriteTransactionFile(
        InstallerSession& session,
        const std::wstring& path,
        const std::wstring& value)
    {
        if (!IsSafeTree(path) || !IsFixtureMutationAllowed(session, path))
        {
            session.Log(L"refused unsafe transaction file=" + path);
            return false;
        }
        if (!EnsureDirectory(ParentPath(path)))
        {
            session.Log(L"could not create transaction directory=" + ParentPath(path));
            return false;
        }
        std::vector<char> bytes;
        return EncodeUtf8(value, bytes) && WriteBytes(path, bytes);
    }

    std::optional<MigrationPlan> ReadMigrationActionData(InstallerSession& session)
    {
        const std::wstring data = session.GetProperty(L"CustomActionData");
        if (data.empty())
        {
            return std::nullopt;
        }
        const auto plan = DecodeMigrationPlan(data);
        if (!plan || !ValidateMigrationPlan(session, *plan))
        {
            return std::nullopt;
        }
        return plan;
    }

    enum class CleanupTargetKind : wchar_t
    {
        Data = L'D',
        Cache = L'C',
        Selection = L'S'
    };

    struct CleanupTarget
    {
        CleanupTargetKind Kind;
        std::wstring OriginalPath;
    };

    struct CleanupPlan
    {
        std::wstring Token;
        std::vector<CleanupTarget> Targets;
    };

    struct CleanupManifest
    {
        std::wstring Token;
        CleanupTargetKind Kind;
        std::wstring OriginalPath;
        std::wstring StagedPath;
    };

    void AppendLengthField(std::wstring& encoded, const std::wstring& field)
    {
        encoded.push_back(L'|');
        encoded.append(std::to_wstring(field.size()));
        encoded.push_back(L':');
        encoded.append(field);
    }

    std::optional<std::wstring> ReadLengthField(
        const std::wstring& encoded,
        size_t& position)
    {
        if (position >= encoded.size() || encoded[position++] != L'|')
        {
            return std::nullopt;
        }
        const size_t colon = encoded.find(L':', position);
        if (colon == std::wstring::npos || colon == position)
        {
            return std::nullopt;
        }
        size_t length = 0;
        for (size_t index = position; index < colon; ++index)
        {
            if (encoded[index] < L'0' || encoded[index] > L'9')
            {
                return std::nullopt;
            }
            const size_t digit = static_cast<size_t>(encoded[index] - L'0');
            if (length > (30000 - digit) / 10)
            {
                return std::nullopt;
            }
            length = (length * 10) + digit;
        }
        position = colon + 1;
        if (length > encoded.size() - position)
        {
            return std::nullopt;
        }
        std::wstring field = encoded.substr(position, length);
        position += length;
        return field;
    }

    bool IsValidTransactionToken(const std::wstring& token)
    {
        if (token.empty() || token.size() > 64)
        {
            return false;
        }
        return std::all_of(token.begin(), token.end(), [](wchar_t character)
        {
            return (character >= L'0' && character <= L'9')
                || (character >= L'a' && character <= L'f')
                || (character >= L'A' && character <= L'F')
                || character == L'-';
        });
    }

    std::wstring EncodeCleanupPlan(const CleanupPlan& plan)
    {
        std::wstring encoded = L"WTC1";
        AppendLengthField(encoded, plan.Token);
        AppendLengthField(encoded, std::to_wstring(plan.Targets.size()));
        for (const auto& target : plan.Targets)
        {
            AppendLengthField(encoded, std::wstring(1, static_cast<wchar_t>(target.Kind)));
            AppendLengthField(encoded, target.OriginalPath);
        }
        return encoded;
    }

    std::optional<CleanupTargetKind> DecodeCleanupKind(const std::wstring& field)
    {
        if (field.size() != 1)
        {
            return std::nullopt;
        }
        switch (field[0])
        {
            case static_cast<wchar_t>(CleanupTargetKind::Data): return CleanupTargetKind::Data;
            case static_cast<wchar_t>(CleanupTargetKind::Cache): return CleanupTargetKind::Cache;
            case static_cast<wchar_t>(CleanupTargetKind::Selection): return CleanupTargetKind::Selection;
            default: return std::nullopt;
        }
    }

    std::optional<CleanupPlan> DecodeCleanupPlan(const std::wstring& encoded)
    {
        if (!encoded.starts_with(L"WTC1") || encoded.size() > 30000)
        {
            return std::nullopt;
        }
        size_t position = 4;
        const auto token = ReadLengthField(encoded, position);
        const auto countField = ReadLengthField(encoded, position);
        if (!token || !countField || countField->empty() || countField->size() > 1)
        {
            return std::nullopt;
        }
        const int count = _wtoi(countField->c_str());
        if (count < 1 || count > 3 || *countField != std::to_wstring(count))
        {
            return std::nullopt;
        }
        CleanupPlan plan{*token, {}};
        plan.Targets.reserve(static_cast<size_t>(count));
        for (int index = 0; index < count; ++index)
        {
            const auto kindField = ReadLengthField(encoded, position);
            const auto path = ReadLengthField(encoded, position);
            const auto kind = kindField ? DecodeCleanupKind(*kindField) : std::nullopt;
            if (!kind || !path)
            {
                return std::nullopt;
            }
            plan.Targets.push_back(CleanupTarget{*kind, *path});
        }
        return position == encoded.size() ? std::optional<CleanupPlan>(std::move(plan)) : std::nullopt;
    }

    std::wstring StagedCleanupPath(const CleanupTarget& target, const std::wstring& token)
    {
        return target.OriginalPath + L".weeztail-cleanup-" + token;
    }

    std::wstring CleanupManifestPath(const CleanupTarget& target, const std::wstring& token)
    {
        return StagedCleanupPath(target, token) + L".manifest";
    }

    std::wstring EncodeCleanupManifest(const CleanupManifest& manifest)
    {
        std::wstring encoded = L"WTCM1";
        AppendLengthField(encoded, manifest.Token);
        AppendLengthField(encoded, std::wstring(1, static_cast<wchar_t>(manifest.Kind)));
        AppendLengthField(encoded, manifest.OriginalPath);
        AppendLengthField(encoded, manifest.StagedPath);
        return encoded;
    }

    std::optional<CleanupManifest> DecodeCleanupManifest(const std::wstring& encoded)
    {
        if (!encoded.starts_with(L"WTCM1") || encoded.size() > 30000)
        {
            return std::nullopt;
        }
        size_t position = 5;
        const auto token = ReadLengthField(encoded, position);
        const auto kindField = ReadLengthField(encoded, position);
        const auto original = ReadLengthField(encoded, position);
        const auto staged = ReadLengthField(encoded, position);
        const auto kind = kindField ? DecodeCleanupKind(*kindField) : std::nullopt;
        if (!token || !kind || !original || !staged || position != encoded.size())
        {
            return std::nullopt;
        }
        return CleanupManifest{*token, *kind, *original, *staged};
    }

    bool HasPathLeaf(const std::wstring& path, std::wstring_view expected)
    {
        const size_t separator = path.find_last_of(L'\\');
        return separator != std::wstring::npos
            && EqualsIgnoreCase(std::wstring_view(path).substr(separator + 1), expected);
    }

    bool IsSameVolume(const std::wstring& left, const std::wstring& right)
    {
        return left.size() >= 3 && right.size() >= 3
            && std::towupper(left[0]) == std::towupper(right[0])
            && left[1] == L':' && right[1] == L':';
    }

    bool ValidateCleanupTarget(InstallerSession& session, const CleanupTarget& target)
    {
        const auto normalized = NormalizeCleanupPath(target.OriginalPath);
        if (!normalized
            || !EqualsIgnoreCase(*normalized, target.OriginalPath)
            || !IsSafeTree(*normalized)
            || !IsFixtureMutationAllowed(session, *normalized))
        {
            return false;
        }
        switch (target.Kind)
        {
            case CleanupTargetKind::Data:
                return HasPathLeaf(*normalized, DataDirectoryName);
            case CleanupTargetKind::Cache:
            {
                const auto cache = NormalizeCleanupPath(CurrentCachePath());
                return cache && EqualsIgnoreCase(*normalized, *cache);
            }
            case CleanupTargetKind::Selection:
            {
                const auto current = NormalizeCleanupPath(CurrentSelectionPath());
                const auto legacy = NormalizeCleanupPath(LegacySelectionPath());
                return (current && EqualsIgnoreCase(*normalized, *current))
                    || (legacy && EqualsIgnoreCase(*normalized, *legacy));
            }
        }
        return false;
    }

    bool ValidateCleanupPlan(InstallerSession& session, const CleanupPlan& plan)
    {
        if (!IsValidTransactionToken(plan.Token)
            || plan.Targets.empty()
            || plan.Targets.size() > 3)
        {
            session.Log(L"rejected malformed cleanup action data.");
            return false;
        }
        bool hasData = false;
        bool hasCache = false;
        bool hasSelection = false;
        for (size_t index = 0; index < plan.Targets.size(); ++index)
        {
            const auto& target = plan.Targets[index];
            if (!ValidateCleanupTarget(session, target)
                || !IsSameVolume(target.OriginalPath, StagedCleanupPath(target, plan.Token)))
            {
                session.Log(L"rejected unsafe cleanup target.");
                return false;
            }
            bool* seen = target.Kind == CleanupTargetKind::Data ? &hasData
                : target.Kind == CleanupTargetKind::Cache ? &hasCache : &hasSelection;
            if (*seen)
            {
                return false;
            }
            *seen = true;
            for (size_t other = 0; other < index; ++other)
            {
                const auto& otherPath = plan.Targets[other].OriginalPath;
                if (IsSameOrDescendant(target.OriginalPath, otherPath)
                    || IsSameOrDescendant(otherPath, target.OriginalPath))
                {
                    session.Log(L"rejected overlapping cleanup targets.");
                    return false;
                }
            }
        }
        return hasData && hasCache;
    }

    std::optional<CleanupPlan> ReadCleanupActionData(InstallerSession& session)
    {
        const auto plan = DecodeCleanupPlan(session.GetProperty(L"CustomActionData"));
        if (!plan || !ValidateCleanupPlan(session, *plan))
        {
            return std::nullopt;
        }
        return plan;
    }

    std::optional<bool> IsEffectiveLocalSystem()
    {
        HANDLE token = nullptr;
        if (!OpenThreadToken(GetCurrentThread(), TOKEN_QUERY, TRUE, &token)
            && !OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token))
        {
            return std::nullopt;
        }
        DWORD required = 0;
        GetTokenInformation(token, TokenUser, nullptr, 0, &required);
        std::vector<unsigned char> information(required);
        const bool obtained = required > 0
            && GetTokenInformation(token, TokenUser, information.data(), required, &required) != FALSE;
        std::array<unsigned char, SECURITY_MAX_SID_SIZE> systemSid{};
        DWORD systemSidSize = static_cast<DWORD>(systemSid.size());
        const bool created = CreateWellKnownSid(
            WinLocalSystemSid,
            nullptr,
            systemSid.data(),
            &systemSidSize) != FALSE;
        std::optional<bool> isSystem;
        if (obtained && created)
        {
            const auto* user = reinterpret_cast<const TOKEN_USER*>(information.data());
            isSystem = EqualSid(user->User.Sid, systemSid.data()) != FALSE;
        }
        CloseHandle(token);
        return isSystem;
    }

    std::optional<std::wstring> PathFromHandle(HANDLE handle)
    {
        const DWORD required = GetFinalPathNameByHandleW(handle, nullptr, 0, FILE_NAME_NORMALIZED);
        if (required == 0 || required > 32767)
        {
            return std::nullopt;
        }
        std::vector<wchar_t> buffer(static_cast<size_t>(required) + 1);
        const DWORD written = GetFinalPathNameByHandleW(
            handle,
            buffer.data(),
            static_cast<DWORD>(buffer.size()),
            FILE_NAME_NORMALIZED);
        if (written == 0 || written >= buffer.size())
        {
            return std::nullopt;
        }
        std::wstring path(buffer.data(), written);
        constexpr std::wstring_view devicePrefix = L"\\\\?\\";
        if (path.starts_with(devicePrefix))
        {
            path.erase(0, devicePrefix.size());
        }
        return NormalizeCleanupPath(path);
    }

    bool RenameValidatedPath(
        InstallerSession& session,
        const std::wstring& source,
        const std::wstring& destination)
    {
        if (!IsSameVolume(source, destination)
            || Exists(destination)
            || !IsSafeTree(source)
            || !IsSafeTree(destination)
            || !IsFixtureMutationAllowed(session, source)
            || !IsFixtureMutationAllowed(session, destination))
        {
            return false;
        }
        const HANDLE handle = CreateFileW(
            source.c_str(),
            DELETE | FILE_READ_ATTRIBUTES | SYNCHRONIZE,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            nullptr,
            OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT,
            nullptr);
        if (handle == INVALID_HANDLE_VALUE)
        {
            return false;
        }
        FILE_ATTRIBUTE_TAG_INFO tag{};
        const auto actualPath = PathFromHandle(handle);
        const bool safeHandle = GetFileInformationByHandleEx(
            handle,
            FileAttributeTagInfo,
            &tag,
            sizeof(tag)) != FALSE
            && (tag.FileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) == 0
            && actualPath
            && EqualsIgnoreCase(*actualPath, source);
        bool renamed = false;
        if (safeHandle)
        {
            const size_t nameBytes = destination.size() * sizeof(wchar_t);
            if (nameBytes <= static_cast<size_t>(std::numeric_limits<DWORD>::max()))
            {
                // Windows documents FileNameLength as authoritative, but some supported
                // versions still read the trailing WCHAR while processing FileRenameInfo.
                // Keep the structure's built-in WCHAR plus a zero-filled terminator.
                std::vector<unsigned char> bytes(sizeof(FILE_RENAME_INFO) + nameBytes);
                auto* information = reinterpret_cast<FILE_RENAME_INFO*>(bytes.data());
                information->ReplaceIfExists = FALSE;
                information->RootDirectory = nullptr;
                information->FileNameLength = static_cast<DWORD>(nameBytes);
                std::memcpy(information->FileName, destination.data(), nameBytes);
                renamed = SetFileInformationByHandle(
                    handle,
                    FileRenameInfo,
                    information,
                    static_cast<DWORD>(bytes.size())) != FALSE;
            }
        }
        CloseHandle(handle);
        return renamed;
    }

    bool ValidateManifest(
        InstallerSession& session,
        const CleanupTarget& target,
        const CleanupManifest& manifest,
        const std::wstring& manifestPath)
    {
        return manifest.Kind == target.Kind
            && IsValidTransactionToken(manifest.Token)
            && EqualsIgnoreCase(manifest.OriginalPath, target.OriginalPath)
            && EqualsIgnoreCase(manifest.StagedPath, StagedCleanupPath(target, manifest.Token))
            && EqualsIgnoreCase(manifestPath, manifest.StagedPath + L".manifest")
            && ValidateCleanupTarget(session, target)
            && IsSameVolume(manifest.OriginalPath, manifest.StagedPath)
            && IsFixtureMutationAllowed(session, manifestPath);
    }

    bool RemoveManifest(InstallerSession& session, const std::wstring& manifestPath)
    {
        return RemoveTransactionFile(session, manifestPath, L"cleanup manifest");
    }

    bool RecoverManifest(
        InstallerSession& session,
        const CleanupTarget& target,
        const std::wstring& manifestPath)
    {
        const auto encoded = ReadTextFile(manifestPath);
        const auto manifest = encoded ? DecodeCleanupManifest(*encoded) : std::nullopt;
        if (!manifest || !ValidateManifest(session, target, *manifest, manifestPath))
        {
            session.Log(L"preserved unrecognized cleanup recovery metadata=" + manifestPath);
            return false;
        }
        const bool originalExists = Exists(manifest->OriginalPath);
        const bool stagedExists = Exists(manifest->StagedPath);
        if (originalExists && stagedExists)
        {
            session.Log(L"preserved cleanup conflict original=" + manifest->OriginalPath
                + L" staged=" + manifest->StagedPath);
            return false;
        }
        if (stagedExists
            && !RenameValidatedPath(session, manifest->StagedPath, manifest->OriginalPath))
        {
            session.Log(L"could not restore staged cleanup data=" + manifest->StagedPath);
            return false;
        }
        return RemoveManifest(session, manifestPath);
    }

    bool RecoverInterruptedTarget(InstallerSession& session, const CleanupTarget& target)
    {
        WIN32_FIND_DATAW item{};
        const std::wstring pattern = target.OriginalPath + L".weeztail-cleanup-*.manifest";
        const HANDLE find = FindFirstFileW(pattern.c_str(), &item);
        if (find == INVALID_HANDLE_VALUE)
        {
            return GetLastError() == ERROR_FILE_NOT_FOUND
                || GetLastError() == ERROR_PATH_NOT_FOUND;
        }
        bool recovered = true;
        do
        {
            if ((item.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0)
            {
                recovered = false;
                continue;
            }
            const std::wstring manifestPath = JoinPath(ParentPath(target.OriginalPath), item.cFileName);
            recovered = RecoverManifest(session, target, manifestPath) && recovered;
        } while (FindNextFileW(find, &item));
        FindClose(find);
        return recovered;
    }

    bool StageCleanupTarget(
        InstallerSession& session,
        const CleanupTarget& target,
        const std::wstring& token)
    {
        if (!Exists(target.OriginalPath))
        {
            return true;
        }
        const std::wstring staged = StagedCleanupPath(target, token);
        const std::wstring manifestPath = CleanupManifestPath(target, token);
        const CleanupManifest manifest{token, target.Kind, target.OriginalPath, staged};
        if (!WriteTransactionFile(session, manifestPath, EncodeCleanupManifest(manifest)))
        {
            return false;
        }
        if (!RenameValidatedPath(session, target.OriginalPath, staged))
        {
            RemoveManifest(session, manifestPath);
            return false;
        }
        return true;
    }

    bool RollbackCleanupTarget(
        InstallerSession& session,
        const CleanupTarget& target,
        const std::wstring& token)
    {
        const std::wstring manifestPath = CleanupManifestPath(target, token);
        if (!FileExists(manifestPath))
        {
            return true;
        }
        return RecoverManifest(session, target, manifestPath);
    }

    bool CommitCleanupTarget(
        InstallerSession& session,
        const CleanupTarget& target,
        const std::wstring& token)
    {
        const std::wstring staged = StagedCleanupPath(target, token);
        const std::wstring manifestPath = CleanupManifestPath(target, token);
        const auto encoded = ReadTextFile(manifestPath);
        const auto manifest = encoded ? DecodeCleanupManifest(*encoded) : std::nullopt;
        if (!manifest || !ValidateManifest(session, target, *manifest, manifestPath))
        {
            if (Exists(staged) || Exists(manifestPath))
            {
                session.Log(L"preserved unrecognized staged cleanup data=" + staged);
                return false;
            }
            return true;
        }
        if (Exists(staged))
        {
            const DWORD attributes = GetFileAttributesW(staged.c_str());
            const bool deleted = attributes != INVALID_FILE_ATTRIBUTES
                && (attributes & FILE_ATTRIBUTE_DIRECTORY) != 0
                ? DeleteTree(staged)
                : DeleteApprovedSelection(session, staged, staged);
            if (!deleted)
            {
                session.Log(L"could not finalize staged cleanup data; recovery metadata remains=" + staged);
                return false;
            }
        }
        return RemoveManifest(session, manifestPath);
    }
}

namespace WeezTail::Setup
{
    ActionResult CaptureLegacyStorageSelection(InstallerSession& session)
    {
        constexpr std::array<const wchar_t*, 3> actionDataProperties = {
            L"RollbackLegacyStorageSelection",
            L"ApplyLegacyStorageSelection",
            L"CommitLegacyStorageSelection"
        };
        if (!session.SetProperty(L"LOGREADERMIGRATIONPLANNED", L"0"))
        {
            return ActionResult::Failure;
        }
        for (const auto* property : actionDataProperties)
        {
            if (!session.SetProperty(property, L""))
            {
                return ActionResult::Failure;
            }
        }

        const std::wstring currentSelection = CurrentSelectionPath();
        if (FileExists(currentSelection))
        {
            session.Log(L"kept the existing WeezTail selection.");
            return ActionResult::Success;
        }

        std::wstring selectionSource;
        std::wstring storageRoot;
        ResolveLegacyStorageCandidate(session, selectionSource, storageRoot);
        if (!selectionSource.empty())
        {
            const auto selectedRoot = LoadJsonStringValue(
                session,
                selectionSource,
                L"storageRootPath");
            storageRoot = selectedRoot ? *selectedRoot : std::wstring{};
        }
        if (storageRoot.empty())
        {
            session.Log(L"found no valid legacy storage metadata to capture.");
            return ActionResult::Success;
        }

        const auto normalized = NormalizeCleanupPath(storageRoot);
        const auto normalizedSelection = NormalizeCleanupPath(currentSelection);
        if (!normalized
            || IsProtectedPath(*normalized)
            || IsUnsafeBroadPath(*normalized)
            || !normalizedSelection
            || !HasExpectedSelectionSuffix(*normalizedSelection)
            || !IsSafeTree(*normalizedSelection)
            || !IsFixtureMutationAllowed(session, *normalizedSelection))
        {
            session.Log(L"rejected legacy storage root=" + storageRoot);
            return ActionResult::Success;
        }

        const auto token = CreateTransactionToken();
        if (!token)
        {
            return ActionResult::Failure;
        }
        const MigrationPlan plan{
            *token,
            *normalizedSelection,
            L"{\"storageRootPath\":\"" + EscapeJsonString(*normalized) + L"\"}"
        };
        const std::wstring encoded = EncodeMigrationPlan(plan);
        if (encoded.size() > 30000)
        {
            session.Log(L"legacy migration metadata exceeds the action-data limit.");
            return ActionResult::Failure;
        }
        for (const auto* property : actionDataProperties)
        {
            if (!session.SetProperty(property, encoded))
            {
                return ActionResult::Failure;
            }
        }
        if (!session.SetProperty(L"LOGREADERMIGRATIONPLANNED", L"1"))
        {
            return ActionResult::Failure;
        }
        session.Log(L"captured validated legacy storage metadata for transactional migration.");
        return ActionResult::Success;
    }

    ActionResult ApplyLegacyStorageSelection(InstallerSession& session)
    {
        const auto plan = ReadMigrationActionData(session);
        if (!plan)
        {
            session.Log(L"missing or invalid migration action data.");
            return ActionResult::Failure;
        }
        if (FileExists(plan->SelectionPath))
        {
            session.Log(L"preserved a selection created after migration planning.");
            return ActionResult::Success;
        }

        const std::wstring marker = MigrationMarkerPath(*plan);
        const std::wstring temporary = MigrationTemporaryPath(*plan);
        if (!WriteTransactionFile(session, marker, plan->Token)
            || !WriteTransactionFile(session, temporary, plan->Payload))
        {
            return ActionResult::Failure;
        }
        if (FileExists(plan->SelectionPath))
        {
            RemoveTransactionFile(session, temporary, L"migration temporary file");
            RemoveTransactionFile(session, marker, L"migration marker");
            session.Log(L"preserved a selection created during migration staging.");
            return ActionResult::Success;
        }
        if (!MoveFileExW(
            temporary.c_str(),
            plan->SelectionPath.c_str(),
            MOVEFILE_WRITE_THROUGH))
        {
            if (GetLastError() == ERROR_ALREADY_EXISTS || FileExists(plan->SelectionPath))
            {
                RemoveTransactionFile(session, temporary, L"migration temporary file");
                RemoveTransactionFile(session, marker, L"migration marker");
                session.Log(L"preserved a selection that won the migration race.");
                return ActionResult::Success;
            }
            session.Log(L"could not commit transactional migration selection.");
            return ActionResult::Failure;
        }
        session.Log(L"applied transactional legacy storage selection.");
        return ActionResult::Success;
    }

    ActionResult RollbackLegacyStorageSelection(InstallerSession& session)
    {
        const auto plan = ReadMigrationActionData(session);
        if (!plan)
        {
            return ActionResult::Success;
        }
        const std::wstring marker = MigrationMarkerPath(*plan);
        const auto markerValue = ReadTextFile(marker);
        if (!markerValue || *markerValue != plan->Token)
        {
            return ActionResult::Success;
        }

        const auto selectionValue = ReadTextFile(plan->SelectionPath);
        if (selectionValue && *selectionValue == plan->Payload)
        {
            RemoveTransactionFile(session, plan->SelectionPath, L"rolled-back migration selection");
        }
        else if (selectionValue)
        {
            session.Log(L"preserved changed migration selection during rollback.");
        }
        RemoveTransactionFile(session, MigrationTemporaryPath(*plan), L"migration temporary file");
        RemoveTransactionFile(session, marker, L"migration marker");
        return ActionResult::Success;
    }

    ActionResult CommitLegacyStorageSelection(InstallerSession& session)
    {
        const auto plan = ReadMigrationActionData(session);
        if (!plan)
        {
            return ActionResult::Success;
        }
        const std::wstring marker = MigrationMarkerPath(*plan);
        const auto markerValue = ReadTextFile(marker);
        if (markerValue && *markerValue == plan->Token)
        {
            RemoveTransactionFile(
                session,
                MigrationTemporaryPath(*plan),
                L"migration temporary file");
            RemoveTransactionFile(session, marker, L"migration marker");
        }
        else if (markerValue)
        {
            session.Log(L"preserved changed migration marker during commit.");
        }
        return ActionResult::Success;
    }

    ActionResult PromptRemoveData(InstallerSession& session)
    {
        if (!session.SetProperty(L"REMOVELOGREADERDATA", L"0")
            || !session.SetProperty(L"LOGREADERDATAROOT", L"")
            || !session.SetProperty(L"LOGREADERUSERSELECTIONPATH", L""))
        {
            return ActionResult::Failure;
        }

        const std::wstring uiLevel = session.GetProperty(L"UILevel");
        if (uiLevel.empty() || _wtoi(uiLevel.c_str()) < 5)
        {
            return ActionResult::Success;
        }
        const std::wstring storageRoot = ResolveCleanupStorageRoot(session);
        if (storageRoot.empty())
        {
            session.Log(L"prompt skipped because no cleanup storage root was found.");
            return ActionResult::Success;
        }

        const std::wstring message = L"Remove WeezTail data and cache for the current Windows user?\r\nData: "
            + JoinPath(storageRoot, DataDirectoryName)
            + L"\r\nCache: "
            + CurrentCachePath();
        if (!session.PromptYesNo(message, L"WeezTail Setup"))
        {
            return ActionResult::Success;
        }

        if (!session.SetProperty(L"REMOVELOGREADERDATA", L"1")
            || !session.SetProperty(L"LOGREADERDATAROOT", storageRoot))
        {
            return ActionResult::Failure;
        }
        if (InstallUsesPerUserChoice(session)
            && !session.SetProperty(L"LOGREADERUSERSELECTIONPATH", EffectiveSelectionPath()))
        {
            return ActionResult::Failure;
        }
        return ActionResult::Success;
    }

    ActionResult PlanDataCleanup(InstallerSession& session)
    {
        constexpr std::array<const wchar_t*, 4> actionDataProperties = {
            L"RecoverInterruptedDataCleanup",
            L"RollbackDataCleanup",
            L"StageDataCleanup",
            L"CommitDataCleanup"
        };
        for (const auto* property : actionDataProperties)
        {
            if (!session.SetProperty(property, L""))
            {
                return ActionResult::Failure;
            }
        }
        if (!session.SetProperty(L"LOGREADERCLEANUPPLANNED", L"0"))
        {
            return ActionResult::Failure;
        }
        if (session.GetProperty(L"REMOVELOGREADERDATA") != L"1"
            || session.GetProperty(L"REMOVE") != L"ALL"
            || !session.GetProperty(L"UPGRADINGPRODUCTCODE").empty())
        {
            return ActionResult::Success;
        }
        const auto isLocalSystem = session.FixtureRoot()
            ? std::optional<bool>(false)
            : IsEffectiveLocalSystem();
        if (!isLocalSystem || *isLocalSystem)
        {
            session.Log(L"retained current-user data because setup could not establish a non-system user identity.");
            return ActionResult::Success;
        }

        const std::wstring storageRoot = ResolveCleanupStorageRoot(session);
        const std::wstring requestedRoot = session.GetProperty(L"LOGREADERDATAROOT");
        if (storageRoot.empty())
        {
            session.Log(L"cleanup skipped because no storage root was found.");
            return ActionResult::Success;
        }
        if (!requestedRoot.empty())
        {
            const auto normalizedRequest = NormalizeCleanupPath(requestedRoot);
            if (!normalizedRequest || !EqualsIgnoreCase(*normalizedRequest, storageRoot))
            {
                session.Log(L"retained data because the supplied root does not match storage configuration.");
                return ActionResult::Success;
            }
        }

        const auto dataPath = NormalizeCleanupPath(JoinPath(storageRoot, DataDirectoryName));
        const auto cachePath = NormalizeCleanupPath(CurrentCachePath());
        if (!dataPath || !cachePath)
        {
            session.Log(L"retained data because a cleanup target was invalid.");
            return ActionResult::Success;
        }
        CleanupPlan plan;
        const auto token = CreateTransactionToken();
        if (!token)
        {
            return ActionResult::Failure;
        }
        plan.Token = *token;
        plan.Targets = {
            CleanupTarget{CleanupTargetKind::Data, *dataPath},
            CleanupTarget{CleanupTargetKind::Cache, *cachePath}
        };

        const std::wstring selectionProperty = session.GetProperty(L"LOGREADERUSERSELECTIONPATH");
        if (!selectionProperty.empty())
        {
            const bool perUserChoice = InstallUsesPerUserChoice(session);
            const auto selection = NormalizeCleanupPath(selectionProperty);
            const auto effectiveSelection = NormalizeCleanupPath(EffectiveSelectionPath());
            if (!perUserChoice
                || !selection
                || !effectiveSelection
                || !EqualsIgnoreCase(*selection, *effectiveSelection))
            {
                session.Log(L"retained data because the supplied selection file is not eligible.");
                return ActionResult::Success;
            }
            plan.Targets.push_back(CleanupTarget{CleanupTargetKind::Selection, *selection});
        }

        const std::wstring historicalCache = JoinPath(storageRoot, CacheDirectoryName);
        if (!EqualsIgnoreCase(historicalCache, *cachePath) && FolderExists(historicalCache))
        {
            session.Log(L"preserved the historical storage-root Cache folder because its ownership is uncertain.");
        }
        if (!ValidateCleanupPlan(session, plan))
        {
            session.Log(L"retained data because the cleanup plan was unsafe.");
            return ActionResult::Success;
        }

        const std::wstring encoded = EncodeCleanupPlan(plan);
        for (const auto* property : actionDataProperties)
        {
            if (!session.SetProperty(property, encoded))
            {
                return ActionResult::Failure;
            }
        }
        if (!session.SetProperty(L"LOGREADERCLEANUPPLANNED", L"1"))
        {
            return ActionResult::Failure;
        }
        session.Log(L"planned transactional cleanup for "
            + std::to_wstring(plan.Targets.size()) + L" current-user targets.");
        return ActionResult::Success;
    }

    ActionResult RecoverInterruptedDataCleanup(InstallerSession& session)
    {
        const auto plan = ReadCleanupActionData(session);
        if (!plan)
        {
            return ActionResult::Failure;
        }
        for (const auto& target : plan->Targets)
        {
            if (!RecoverInterruptedTarget(session, target))
            {
                session.Log(L"cleanup recovery left conflicting or unrecognized data in place.");
            }
        }
        return ActionResult::Success;
    }

    ActionResult StageDataCleanup(InstallerSession& session)
    {
        const auto plan = ReadCleanupActionData(session);
        if (!plan)
        {
            return ActionResult::Failure;
        }
        for (const auto& target : plan->Targets)
        {
            if (!RecoverInterruptedTarget(session, target))
            {
                session.Log(L"retained all cleanup targets because prior recovery needs attention.");
                return ActionResult::Success;
            }
        }

        size_t stagedCount = 0;
        for (const auto& target : plan->Targets)
        {
            const bool existed = Exists(target.OriginalPath);
            if (!StageCleanupTarget(session, target, plan->Token))
            {
                session.Log(L"could not stage cleanup target=" + target.OriginalPath);
                return stagedCount == 0 ? ActionResult::Success : ActionResult::Failure;
            }
            if (existed)
            {
                ++stagedCount;
            }
            if (session.FixtureRoot()
                && session.GetProperty(L"HARNESSFAILAFTERSTAGES") == std::to_wstring(stagedCount))
            {
                session.Log(L"injected cleanup staging failure.");
                return ActionResult::Failure;
            }
        }
        return ActionResult::Success;
    }

    ActionResult RollbackDataCleanup(InstallerSession& session)
    {
        const auto plan = ReadCleanupActionData(session);
        if (!plan)
        {
            return ActionResult::Failure;
        }
        bool restored = true;
        for (auto target = plan->Targets.rbegin(); target != plan->Targets.rend(); ++target)
        {
            restored = RollbackCleanupTarget(session, *target, plan->Token) && restored;
        }
        return restored ? ActionResult::Success : ActionResult::Failure;
    }

    ActionResult CommitDataCleanup(InstallerSession& session)
    {
        const auto plan = ReadCleanupActionData(session);
        if (!plan)
        {
            return ActionResult::Failure;
        }
        for (const auto& target : plan->Targets)
        {
            if (!CommitCleanupTarget(session, target, plan->Token))
            {
                session.Log(L"cleanup commit retained recoverable staged data for a later run.");
            }
        }
        return ActionResult::Success;
    }

    bool RunJsonSelfTest(std::wstring& failure)
    {
        struct Case
        {
            const wchar_t* Json;
            const wchar_t* Expected;
            const wchar_t* Name;
        };
        const std::array<Case, 4> cases = {{
            {LR"({"storageRootPath":"C:\\Logs\\\u6E2C\u8A66-\u0394"})", L"C:\\Logs\\\x6E2C\x8A66-\x0394", L"Unicode escape decoding"},
            {LR"({"storageRootPath":"C:\\Literal\\u6E2C"})", L"C:\\Literal\\u6E2C", L"Escaped literal Unicode sequence"},
            {LR"({"storageRootPath":"C:\\Emoji\\\uD83D\uDE80"})", L"C:\\Emoji\\\xD83D\xDE80", L"Surrogate-pair decoding"},
            {LR"({"storageRootPath":"C:\\Invalid\\\u12G4"})", nullptr, L"Malformed Unicode escape rejection"}
        }};

        for (const auto& item : cases)
        {
            const auto actual = ExtractJsonStringValue(item.Json, L"storageRootPath");
            const bool passed = item.Expected == nullptr ? !actual : actual && *actual == item.Expected;
            if (!passed)
            {
                failure = item.Name;
                return false;
            }
        }
        return true;
    }

    bool IsAllowedFixtureMutation(
        const std::wstring& fixtureRoot,
        const std::wstring& targetPath)
    {
        const auto root = NormalizeCleanupPath(fixtureRoot);
        const auto target = NormalizeCleanupPath(targetPath);
        return root && target && !EqualsIgnoreCase(*root, *target)
            && IsSameOrDescendant(*target, *root)
            && IsSafeTree(*target);
    }
}

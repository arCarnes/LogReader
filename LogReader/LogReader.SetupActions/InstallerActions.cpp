#include "InstallerActions.h"

#include <windows.h>
#include <msi.h>
#include <msiquery.h>

#include <optional>
#include <string>
#include <vector>

#pragma comment(lib, "Msi.lib")
#pragma comment(lib, "User32.lib")

namespace
{
    class MsiSession final : public WeezTail::Setup::InstallerSession
    {
    public:
        explicit MsiSession(MSIHANDLE handle) noexcept : handle_(handle)
        {
        }

        std::wstring GetProperty(const wchar_t* name) override
        {
            DWORD length = 0;
            wchar_t empty = L'\0';
            const UINT first = MsiGetPropertyW(handle_, name, &empty, &length);
            if (first != ERROR_SUCCESS && first != ERROR_MORE_DATA)
            {
                return {};
            }
            std::vector<wchar_t> buffer(static_cast<size_t>(length) + 1);
            DWORD capacity = static_cast<DWORD>(buffer.size());
            if (MsiGetPropertyW(handle_, name, buffer.data(), &capacity) != ERROR_SUCCESS)
            {
                return {};
            }
            return std::wstring(buffer.data(), capacity);
        }

        bool SetProperty(const wchar_t* name, const std::wstring& value) override
        {
            return MsiSetPropertyW(handle_, name, value.c_str()) == ERROR_SUCCESS;
        }

        void Log(const std::wstring& message) override
        {
            PMSIHANDLE record = MsiCreateRecord(0);
            if (record != 0)
            {
                const std::wstring formatted = L"WeezTail Setup: " + message;
                MsiRecordSetStringW(record, 0, formatted.c_str());
                MsiProcessMessage(handle_, INSTALLMESSAGE_INFO, record);
            }
        }

        bool PromptYesNo(const std::wstring& message, const std::wstring& title) override
        {
            return MessageBoxW(
                nullptr,
                message.c_str(),
                title.c_str(),
                MB_YESNO | MB_ICONQUESTION | MB_SETFOREGROUND) == IDYES;
        }

        void ShowWarning(const std::wstring& message, const std::wstring& title) override
        {
            MessageBoxW(
                nullptr,
                message.c_str(),
                title.c_str(),
                MB_OK | MB_ICONWARNING | MB_SETFOREGROUND);
        }

        std::wstring GetComponentPath(
            const std::wstring& productCode,
            const std::wstring& componentCode) override
        {
            DWORD length = 0;
            wchar_t empty = L'\0';
            MsiGetComponentPathW(productCode.c_str(), componentCode.c_str(), &empty, &length);
            if (length == 0)
            {
                return {};
            }
            std::vector<wchar_t> buffer(static_cast<size_t>(length) + 1);
            DWORD capacity = static_cast<DWORD>(buffer.size());
            const INSTALLSTATE state = MsiGetComponentPathW(
                productCode.c_str(),
                componentCode.c_str(),
                buffer.data(),
                &capacity);
            if (state == INSTALLSTATE_UNKNOWN || state == INSTALLSTATE_INVALIDARG)
            {
                return {};
            }
            return std::wstring(buffer.data(), capacity);
        }

        std::optional<std::wstring> FixtureRoot() const override
        {
            return std::nullopt;
        }

    private:
        MSIHANDLE handle_;
    };

    template<typename Action>
    UINT Invoke(MSIHANDLE handle, Action&& action) noexcept
    {
        try
        {
            MsiSession session(handle);
            return action(session) == WeezTail::Setup::ActionResult::Success
                ? ERROR_SUCCESS
                : ERROR_INSTALL_FAILURE;
        }
        catch (...)
        {
            MsiSession session(handle);
            session.Log(L"compiled custom action failed with an unexpected exception.");
            return ERROR_INSTALL_FAILURE;
        }
    }
}

extern "C" __declspec(dllexport) UINT __stdcall MigrateLegacyStorageSelection(MSIHANDLE handle) noexcept
{
    return Invoke(handle, WeezTail::Setup::MigrateLegacyStorageSelection);
}

extern "C" __declspec(dllexport) UINT __stdcall PromptRemoveData(MSIHANDLE handle) noexcept
{
    return Invoke(handle, WeezTail::Setup::PromptRemoveData);
}

extern "C" __declspec(dllexport) UINT __stdcall RemoveDataFolders(MSIHANDLE handle) noexcept
{
    return Invoke(handle, WeezTail::Setup::RemoveDataFolders);
}

BOOL WINAPI DllMain(HINSTANCE, DWORD, LPVOID) noexcept
{
    return TRUE;
}

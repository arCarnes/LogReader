#pragma once

#include <optional>
#include <string>

namespace WeezTail::Setup
{
    enum class ActionResult
    {
        Success,
        Failure
    };

    class InstallerSession
    {
    public:
        virtual ~InstallerSession() = default;

        virtual std::wstring GetProperty(const wchar_t* name) = 0;
        virtual bool SetProperty(const wchar_t* name, const std::wstring& value) = 0;
        virtual void Log(const std::wstring& message) = 0;
        virtual bool PromptYesNo(const std::wstring& message, const std::wstring& title) = 0;
        virtual void ShowWarning(const std::wstring& message, const std::wstring& title) = 0;
        virtual std::wstring GetComponentPath(
            const std::wstring& productCode,
            const std::wstring& componentCode) = 0;
        virtual std::optional<std::wstring> FixtureRoot() const = 0;
    };

    ActionResult CaptureLegacyStorageSelection(InstallerSession& session);
    ActionResult ApplyLegacyStorageSelection(InstallerSession& session);
    ActionResult RollbackLegacyStorageSelection(InstallerSession& session);
    ActionResult CommitLegacyStorageSelection(InstallerSession& session);
    ActionResult PromptRemoveData(InstallerSession& session);
    ActionResult RemoveDataFolders(InstallerSession& session);

    bool RunJsonSelfTest(std::wstring& failure);
    bool IsAllowedFixtureMutation(
        const std::wstring& fixtureRoot,
        const std::wstring& targetPath);
}

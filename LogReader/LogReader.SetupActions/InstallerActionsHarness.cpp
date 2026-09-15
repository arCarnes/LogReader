#include "InstallerActions.h"

#include <windows.h>

#include <iostream>
#include <map>
#include <optional>
#include <string>

namespace
{
    class HarnessSession final : public WeezTail::Setup::InstallerSession
    {
    public:
        HarnessSession(
            std::wstring fixtureRoot,
            bool promptAnswer,
            std::wstring componentPath = {})
            : fixtureRoot_(std::move(fixtureRoot)),
              promptAnswer_(promptAnswer),
              componentPath_(std::move(componentPath))
        {
        }

        std::wstring GetProperty(const wchar_t* name) override
        {
            const auto value = properties_.find(name);
            return value == properties_.end() ? std::wstring{} : value->second;
        }

        bool SetProperty(const wchar_t* name, const std::wstring& value) override
        {
            properties_[name] = value;
            return true;
        }

        void Log(const std::wstring& message) override
        {
            std::wcout << L"WeezTail Setup: " << message << L'\n';
        }

        bool PromptYesNo(const std::wstring&, const std::wstring&) override
        {
            return promptAnswer_;
        }

        void ShowWarning(const std::wstring&, const std::wstring&) override
        {
        }

        std::wstring GetComponentPath(const std::wstring&, const std::wstring&) override
        {
            return componentPath_;
        }

        std::optional<std::wstring> FixtureRoot() const override
        {
            return fixtureRoot_;
        }

    private:
        std::map<std::wstring, std::wstring> properties_;
        std::wstring fixtureRoot_;
        bool promptAnswer_;
        std::wstring componentPath_;
    };
}

int wmain(int argumentCount, wchar_t** arguments)
{
    if (argumentCount == 2 && wcscmp(arguments[1], L"selftest-json") == 0)
    {
        std::wstring failure;
        if (!WeezTail::Setup::RunJsonSelfTest(failure))
        {
            std::wcerr << failure << L" failed.\n";
            return 1;
        }
        std::wcout << L"Installer action JSON parsing validated.\n";
        return 0;
    }

    if (argumentCount >= 4 && wcscmp(arguments[2], L"guard") == 0)
    {
        return WeezTail::Setup::IsAllowedFixtureMutation(arguments[1], arguments[3]) ? 78 : 77;
    }

    if (argumentCount >= 4
        && (wcscmp(arguments[2], L"migrate") == 0
            || wcscmp(arguments[2], L"migrate-rollback") == 0))
    {
        HarnessSession session(arguments[1], false, arguments[3]);
        session.SetProperty(L"WIX_UPGRADE_DETECTED", L"{FIXTURE-OLD-PRODUCT}");
        auto result = WeezTail::Setup::CaptureLegacyStorageSelection(session);
        if (result == WeezTail::Setup::ActionResult::Success
            && session.GetProperty(L"LOGREADERMIGRATIONPLANNED") == L"1")
        {
            session.SetProperty(
                L"CustomActionData",
                session.GetProperty(L"ApplyLegacyStorageSelection"));
            result = WeezTail::Setup::ApplyLegacyStorageSelection(session);
            session.SetProperty(
                L"CustomActionData",
                session.GetProperty(
                    wcscmp(arguments[2], L"migrate-rollback") == 0
                        ? L"RollbackLegacyStorageSelection"
                        : L"CommitLegacyStorageSelection"));
            if (result == WeezTail::Setup::ActionResult::Success)
            {
                result = wcscmp(arguments[2], L"migrate-rollback") == 0
                    ? WeezTail::Setup::RollbackLegacyStorageSelection(session)
                    : WeezTail::Setup::CommitLegacyStorageSelection(session);
            }
        }
        std::wcout << L"ActionResult="
            << (result == WeezTail::Setup::ActionResult::Success ? 1 : 3)
            << L'\n';
        return result == WeezTail::Setup::ActionResult::Success ? 0 : 1;
    }

    if (argumentCount < 7)
    {
        std::wcerr << L"Expected fixture root and action properties; received "
            << argumentCount - 1
            << L" arguments:";
        for (int index = 1; index < argumentCount; ++index)
        {
            std::wcerr << L" [" << arguments[index] << L"]";
        }
        std::wcerr << L'\n';
        return 2;
    }

    const bool prompt = wcscmp(arguments[2], L"prompt") == 0;
    const bool promptAnswer = argumentCount > 9 && wcscmp(arguments[9], L"yes") == 0;
    HarnessSession session(arguments[1], promptAnswer);
    session.SetProperty(L"UILevel", prompt && argumentCount > 8 ? arguments[8] : L"2");
    session.SetProperty(L"REMOVE", argumentCount > 7 ? arguments[7] : L"ALL");
    session.SetProperty(L"REMOVELOGREADERDATA", arguments[2]);
    session.SetProperty(L"LOGREADERDATAROOT", arguments[3]);
    session.SetProperty(L"LOGREADERUSERSELECTIONPATH", arguments[4]);
    session.SetProperty(L"INSTALLFOLDER", arguments[5]);
    session.SetProperty(L"UPGRADINGPRODUCTCODE", arguments[6]);

    if (prompt
        && WeezTail::Setup::PromptRemoveData(session) != WeezTail::Setup::ActionResult::Success)
    {
        return 1;
    }
    if (WeezTail::Setup::RemoveDataFolders(session) != WeezTail::Setup::ActionResult::Success)
    {
        return 1;
    }

    std::wcout << L"ActionResult=1\n";
    return 0;
}

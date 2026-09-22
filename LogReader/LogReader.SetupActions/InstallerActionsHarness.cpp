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
            || wcscmp(arguments[2], L"migrate-rollback") == 0
            || wcscmp(arguments[2], L"migrate-rollback-locked") == 0))
    {
        const bool rollback = wcscmp(arguments[2], L"migrate") != 0;
        const bool lockedRollback = wcscmp(arguments[2], L"migrate-rollback-locked") == 0;
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
                    rollback
                        ? L"RollbackLegacyStorageSelection"
                        : L"CommitLegacyStorageSelection"));
            if (result == WeezTail::Setup::ActionResult::Success)
            {
                HANDLE lockedSelection = INVALID_HANDLE_VALUE;
                if (lockedRollback)
                {
                    const std::wstring selection = std::wstring(arguments[1])
                        + L"\\Local\\WeezTailSetup\\WeezTail.msi-user.json";
                    lockedSelection = CreateFileW(
                        selection.c_str(),
                        GENERIC_READ,
                        FILE_SHARE_READ | FILE_SHARE_WRITE,
                        nullptr,
                        OPEN_EXISTING,
                        FILE_ATTRIBUTE_NORMAL,
                        nullptr);
                    if (lockedSelection == INVALID_HANDLE_VALUE)
                    {
                        result = WeezTail::Setup::ActionResult::Failure;
                    }
                }
                if (!lockedRollback || lockedSelection != INVALID_HANDLE_VALUE)
                {
                    result = rollback
                        ? WeezTail::Setup::RollbackLegacyStorageSelection(session)
                        : WeezTail::Setup::CommitLegacyStorageSelection(session);
                }
                if (lockedSelection != INVALID_HANDLE_VALUE)
                {
                    CloseHandle(lockedSelection);
                }
                if (lockedRollback)
                {
                    WIN32_FIND_DATAW item{};
                    const std::wstring pattern = std::wstring(arguments[1])
                        + L"\\Local\\WeezTailSetup\\WeezTail.msi-user.json.migration-rollback-*.pending";
                    const HANDLE find = FindFirstFileW(pattern.c_str(), &item);
                    const bool retainedRecovery = find != INVALID_HANDLE_VALUE;
                    if (retainedRecovery)
                    {
                        FindClose(find);
                    }
                    result = result == WeezTail::Setup::ActionResult::Failure && retainedRecovery
                        ? WeezTail::Setup::ActionResult::Success
                        : WeezTail::Setup::ActionResult::Failure;
                }
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

    const std::wstring mode = arguments[2];
    const bool prompt = mode == L"prompt";
    const bool transactionalMode = mode.starts_with(L"cleanup-");
    const bool promptAnswer = argumentCount > 9 && wcscmp(arguments[9], L"yes") == 0;
    HarnessSession session(arguments[1], promptAnswer);
    session.SetProperty(L"UILevel", prompt && argumentCount > 8 ? arguments[8] : L"2");
    session.SetProperty(L"REMOVE", argumentCount > 7 ? arguments[7] : L"ALL");
    session.SetProperty(L"REMOVELOGREADERDATA", prompt ? L"0" : (transactionalMode ? L"1" : arguments[2]));
    session.SetProperty(L"LOGREADERDATAROOT", arguments[3]);
    session.SetProperty(L"LOGREADERUSERSELECTIONPATH", arguments[4]);
    session.SetProperty(L"INSTALLFOLDER", arguments[5]);
    session.SetProperty(L"UPGRADINGPRODUCTCODE", arguments[6]);

    if (prompt
        && WeezTail::Setup::PromptRemoveData(session) != WeezTail::Setup::ActionResult::Success)
    {
        return 1;
    }
    auto result = WeezTail::Setup::PlanDataCleanup(session);
    if (result != WeezTail::Setup::ActionResult::Success)
    {
        return 1;
    }
    if (session.GetProperty(L"LOGREADERCLEANUPPLANNED") != L"1")
    {
        std::wcout << L"ActionResult=1\n";
        return 0;
    }

    if (mode == L"cleanup-deferred-environment")
    {
        const std::wstring deferredLocal = std::wstring(arguments[1]) + L"\\DeferredLocal";
        if (!SetEnvironmentVariableW(L"LOCALAPPDATA", deferredLocal.c_str()))
        {
            return 1;
        }
    }

    const auto invoke = [&session](
        const wchar_t* property,
        WeezTail::Setup::ActionResult (*action)(WeezTail::Setup::InstallerSession&))
    {
        session.SetProperty(L"CustomActionData", session.GetProperty(property));
        return action(session);
    };
    result = invoke(
        L"RecoverInterruptedDataCleanup",
        WeezTail::Setup::RecoverInterruptedDataCleanup);
    if (result != WeezTail::Setup::ActionResult::Success || mode == L"cleanup-recover-only")
    {
        return result == WeezTail::Setup::ActionResult::Success ? 0 : 1;
    }

    HANDLE locked = INVALID_HANDLE_VALUE;
    if (mode == L"cleanup-locked")
    {
        const std::wstring dataPath = std::wstring(arguments[3]) + L"\\Data\\sentinel.txt";
        locked = CreateFileW(
            dataPath.c_str(),
            GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            nullptr,
            OPEN_EXISTING,
            FILE_ATTRIBUTE_NORMAL,
            nullptr);
    }
    if (mode == L"cleanup-fail-rollback")
    {
        session.SetProperty(L"HARNESSFAILAFTERSTAGES", L"1");
    }
    result = invoke(L"StageDataCleanup", WeezTail::Setup::StageDataCleanup);
    if (mode == L"cleanup-fail-rollback")
    {
        if (result != WeezTail::Setup::ActionResult::Failure)
        {
            return 1;
        }
        result = invoke(L"RollbackDataCleanup", WeezTail::Setup::RollbackDataCleanup);
    }
    else if (result != WeezTail::Setup::ActionResult::Success)
    {
        if (locked != INVALID_HANDLE_VALUE)
        {
            CloseHandle(locked);
        }
        return 1;
    }
    else if (mode == L"cleanup-stage-only")
    {
        std::wcout << L"ActionResult=1\n";
        return 0;
    }
    else if (mode == L"cleanup-rollback" || mode == L"cleanup-rollback-conflict")
    {
        if (mode == L"cleanup-rollback-conflict")
        {
            const std::wstring dataPath = std::wstring(arguments[3]) + L"\\Data";
            CreateDirectoryW(dataPath.c_str(), nullptr);
            const std::wstring conflict = dataPath + L"\\conflict.txt";
            const HANDLE file = CreateFileW(
                conflict.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
            if (file != INVALID_HANDLE_VALUE)
            {
                CloseHandle(file);
            }
        }
        result = invoke(L"RollbackDataCleanup", WeezTail::Setup::RollbackDataCleanup);
        if (mode == L"cleanup-rollback-conflict")
        {
            result = result == WeezTail::Setup::ActionResult::Failure
                ? WeezTail::Setup::ActionResult::Success
                : WeezTail::Setup::ActionResult::Failure;
        }
    }
    else
    {
        HANDLE commitLock = INVALID_HANDLE_VALUE;
        if (mode == L"cleanup-commit-failure")
        {
            WIN32_FIND_DATAW item{};
            const std::wstring pattern = std::wstring(arguments[3]) + L"\\Data.weeztail-cleanup-*";
            const HANDLE find = FindFirstFileW(pattern.c_str(), &item);
            if (find != INVALID_HANDLE_VALUE)
            {
                std::wstring stagedDirectory;
                do
                {
                    if ((item.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0)
                    {
                        stagedDirectory = item.cFileName;
                        break;
                    }
                } while (FindNextFileW(find, &item));
                FindClose(find);
                if (!stagedDirectory.empty())
                {
                    const std::wstring child = std::wstring(arguments[3])
                        + L"\\" + stagedDirectory + L"\\sentinel.txt";
                    commitLock = CreateFileW(
                        child.c_str(), GENERIC_READ, 0, nullptr, OPEN_EXISTING, 0, nullptr);
                }
            }
        }
        result = invoke(L"CommitDataCleanup", WeezTail::Setup::CommitDataCleanup);
        if (commitLock != INVALID_HANDLE_VALUE)
        {
            CloseHandle(commitLock);
        }
        if (locked != INVALID_HANDLE_VALUE)
        {
            CloseHandle(locked);
        }
    }

    std::wcout << L"ActionResult=1\n";
    return result == WeezTail::Setup::ActionResult::Success ? 0 : 1;
}

Option Explicit

Const msiDoActionStatusSuccess = 1
Const msiDoActionStatusUserExit = 2
Const msiDoActionStatusFailure = 3
Const installConfigFileName = "LogReader.install.json"
Const msiUserStorageSelectionFileName = "LogReader.msi-user.json"
Const storageSetupDirectoryName = "LogReaderSetup"
Const storageDataDirectoryName = "Data"
Const storageCacheDirectoryName = "Cache"

Function PromptRemoveData()
    Dim result
    Dim storageRoot

    Session.Property("REMOVELOGREADERDATA") = "0"
    Session.Property("LOGREADERDATAROOT") = ""
    Session.Property("LOGREADERUSERSELECTIONPATH") = ""

    If Session.Property("UILevel") = "" Then
        PromptRemoveData = msiDoActionStatusSuccess
        Exit Function
    End If

    If CInt(Session.Property("UILevel")) < 5 Then
        PromptRemoveData = msiDoActionStatusSuccess
        Exit Function
    End If

    storageRoot = ResolveCleanupStorageRoot()
    If storageRoot = "" Then
        LogMessage "PromptRemoveData skipped because no cleanup storage root was found."
        PromptRemoveData = msiDoActionStatusSuccess
        Exit Function
    End If

    result = MsgBox( _
        "Remove LogReader data and cache for the current Windows user?" & vbCrLf & _
        storageRoot, _
        vbYesNo + vbQuestion, _
        "LogReader Setup")

    If result = vbYes Then
        Session.Property("REMOVELOGREADERDATA") = "1"
        Session.Property("LOGREADERDATAROOT") = storageRoot

        If InstallUsesPerUserChoice() Then
            Session.Property("LOGREADERUSERSELECTIONPATH") = ResolveCurrentUserSelectionPath()
        End If
    End If

    PromptRemoveData = msiDoActionStatusSuccess
End Function

Function RemoveDataFolders()
    On Error Resume Next

    Dim storageRoot
    Dim dataPath
    Dim cachePath
    Dim fileSystem
    Dim storageFolder
    Dim userSelectionPath

    If Session.Property("REMOVELOGREADERDATA") <> "1" Then
        RemoveDataFolders = msiDoActionStatusSuccess
        Exit Function
    End If

    storageRoot = TrimTrailingSlash(Session.Property("LOGREADERDATAROOT"))
    If storageRoot = "" Then
        storageRoot = ResolveCleanupStorageRoot()
    End If

    If storageRoot = "" Then
        LogMessage "RemoveDataFolders skipped because no cleanup storage root was found."
        RemoveDataFolders = msiDoActionStatusSuccess
        Exit Function
    End If

    dataPath = storageRoot & "\" & storageDataDirectoryName
    cachePath = storageRoot & "\" & storageCacheDirectoryName
    userSelectionPath = Session.Property("LOGREADERUSERSELECTIONPATH")

    LogMessage "RemoveDataFolders storageRoot=" & storageRoot
    Set fileSystem = CreateObject("Scripting.FileSystemObject")

    If fileSystem.FolderExists(dataPath) Then
        fileSystem.DeleteFolder dataPath, True
    End If

    If fileSystem.FolderExists(cachePath) Then
        fileSystem.DeleteFolder cachePath, True
    End If

    If userSelectionPath <> "" And fileSystem.FileExists(userSelectionPath) Then
        fileSystem.DeleteFile userSelectionPath, True
    End If

    If fileSystem.FolderExists(storageRoot) Then
        Set storageFolder = fileSystem.GetFolder(storageRoot)
        If storageFolder.Files.Count = 0 And storageFolder.SubFolders.Count = 0 Then
            fileSystem.DeleteFolder storageRoot, True
        End If
    End If

    If Err.Number <> 0 Then
        Session.Log "LogReader Setup could not fully remove the data directory: " & Err.Description
        Err.Clear
    End If

    RemoveDataFolders = msiDoActionStatusSuccess
End Function

Private Function ResolveCleanupStorageRoot()
    If InstallUsesPerUserChoice() Then
        ResolveCleanupStorageRoot = TrimTrailingSlash(LoadJsonStringValue(ResolveCurrentUserSelectionPath(), "storageRootPath"))
    Else
        ResolveCleanupStorageRoot = TrimTrailingSlash(LoadJsonStringValue(ResolveInstallConfigPath(), "storageRootPath"))
    End If

    LogMessage "ResolveCleanupStorageRoot=" & ResolveCleanupStorageRoot
End Function

Private Function InstallUsesPerUserChoice()
    Dim storageMode

    storageMode = LCase(LoadJsonStringValue(ResolveInstallConfigPath(), "storageMode"))
    InstallUsesPerUserChoice = (storageMode = "peruserchoice")

    LogMessage "InstallUsesPerUserChoice=" & CStr(InstallUsesPerUserChoice)
End Function

Private Function ResolveInstallConfigPath()
    ResolveInstallConfigPath = EnsureTrailingSlash(Session.Property("INSTALLFOLDER")) & installConfigFileName
End Function

Private Function ResolveCurrentUserSelectionPath()
    Dim shell

    Set shell = CreateObject("WScript.Shell")
    ResolveCurrentUserSelectionPath = shell.ExpandEnvironmentStrings("%LOCALAPPDATA%") _
        & "\" & storageSetupDirectoryName _
        & "\" & msiUserStorageSelectionFileName
End Function

Private Function LoadJsonStringValue(filePath, propertyName)
    On Error Resume Next

    Dim fileSystem
    Dim stream
    Dim json
    Dim propertyMarker
    Dim propertyIndex
    Dim colonIndex
    Dim openingQuoteIndex
    Dim closingQuoteIndex
    Dim rawValue

    Set fileSystem = CreateObject("Scripting.FileSystemObject")
    If Not fileSystem.FileExists(filePath) Then
        LogMessage "LoadJsonStringValue missing file=" & filePath & " property=" & propertyName
        LoadJsonStringValue = ""
        Exit Function
    End If

    Set stream = fileSystem.OpenTextFile(filePath, 1, False)
    json = stream.ReadAll
    stream.Close

    If Err.Number <> 0 Then
        LogMessage "LoadJsonStringValue failed file=" & filePath & " property=" & propertyName & " Err.Number=" & Err.Number & " Description=" & Err.Description
        Err.Clear
        LoadJsonStringValue = ""
        Exit Function
    End If

    propertyMarker = """" & propertyName & """"
    propertyIndex = InStr(1, json, propertyMarker, vbTextCompare)
    If propertyIndex = 0 Then
        LoadJsonStringValue = ""
        Exit Function
    End If

    colonIndex = InStr(propertyIndex + Len(propertyMarker), json, ":")
    If colonIndex = 0 Then
        LoadJsonStringValue = ""
        Exit Function
    End If

    openingQuoteIndex = InStr(colonIndex + 1, json, """")
    If openingQuoteIndex = 0 Then
        LoadJsonStringValue = ""
        Exit Function
    End If

    closingQuoteIndex = InStr(openingQuoteIndex + 1, json, """")
    If closingQuoteIndex = 0 Then
        LoadJsonStringValue = ""
        Exit Function
    End If

    rawValue = Mid(json, openingQuoteIndex + 1, closingQuoteIndex - openingQuoteIndex - 1)
    LoadJsonStringValue = Replace(rawValue, "\\", "\")
End Function

Private Function TrimTrailingSlash(path)
    Do While Right(path, 1) = "\" Or Right(path, 1) = "/"
        path = Left(path, Len(path) - 1)
    Loop

    TrimTrailingSlash = path
End Function

Private Function EnsureTrailingSlash(path)
    path = TrimTrailingSlash(path)
    If path = "" Then
        EnsureTrailingSlash = ""
    Else
        EnsureTrailingSlash = path & "\"
    End If
End Function

Private Sub LogMessage(message)
    Session.Log "LogReader Setup: " & message
End Sub

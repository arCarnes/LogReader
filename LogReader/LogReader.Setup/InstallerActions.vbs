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
    Dim storageRoot
    Dim dataPath
    Dim cachePath
    Dim fileSystem
    Dim userSelectionPath
    Dim cleanupFailed

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

    cleanupFailed = False
    cleanupFailed = Not DeleteFolderIfExists(fileSystem, dataPath) Or cleanupFailed
    cleanupFailed = Not DeleteFolderIfExists(fileSystem, cachePath) Or cleanupFailed

    If userSelectionPath <> "" Then
        cleanupFailed = Not DeleteFileIfExists(fileSystem, userSelectionPath) Or cleanupFailed
    End If

    If cleanupFailed Then
        LogMessage "RemoveDataFolders completed with cleanup failures. Some LogReader data may remain."
        If Session.Property("UILevel") <> "" And CInt(Session.Property("UILevel")) >= 5 Then
            MsgBox "LogReader Setup could not remove all selected data. Some files may remain under:" & vbCrLf & storageRoot, _
                vbOKOnly + vbExclamation, _
                "LogReader Setup"
        End If
    End If

    RemoveDataFolders = msiDoActionStatusSuccess
End Function

Private Function ResolveCleanupStorageRoot()
    Dim rawStorageRoot
    Dim normalizedStorageRoot

    If InstallUsesPerUserChoice() Then
        rawStorageRoot = TrimTrailingSlash(LoadJsonStringValue(ResolveCurrentUserSelectionPath(), "storageRootPath"))
    Else
        rawStorageRoot = TrimTrailingSlash(LoadJsonStringValue(ResolveInstallConfigPath(), "storageRootPath"))
    End If

    If rawStorageRoot = "" Then
        LogMessage "ResolveCleanupStorageRoot missing storage root."
        ResolveCleanupStorageRoot = ""
        Exit Function
    End If

    normalizedStorageRoot = NormalizeCleanupPath(rawStorageRoot)
    If normalizedStorageRoot = "" Then
        LogMessage "ResolveCleanupStorageRoot rejected invalid storage root=" & rawStorageRoot
        ResolveCleanupStorageRoot = ""
        Exit Function
    End If

    If IsProtectedCleanupPath(normalizedStorageRoot) Then
        LogMessage "ResolveCleanupStorageRoot rejected protected storage root=" & normalizedStorageRoot
        ResolveCleanupStorageRoot = ""
        Exit Function
    End If

    If IsUnsafeBroadCleanupPath(normalizedStorageRoot) Then
        LogMessage "ResolveCleanupStorageRoot rejected broad storage root=" & normalizedStorageRoot
        ResolveCleanupStorageRoot = ""
        Exit Function
    End If

    ResolveCleanupStorageRoot = normalizedStorageRoot
    LogMessage "ResolveCleanupStorageRoot=" & ResolveCleanupStorageRoot
End Function

Private Function DeleteFolderIfExists(fileSystem, folderPath)
    On Error Resume Next

    DeleteFolderIfExists = True
    If Not fileSystem.FolderExists(folderPath) Then
        On Error GoTo 0
        Exit Function
    End If

    fileSystem.DeleteFolder folderPath, True
    If Err.Number <> 0 Then
        LogMessage "DeleteFolder failed path=" & folderPath & " Err.Number=" & Err.Number & " Description=" & Err.Description
        Err.Clear
        DeleteFolderIfExists = False
    End If

    On Error GoTo 0
End Function

Private Function DeleteFileIfExists(fileSystem, filePath)
    On Error Resume Next

    DeleteFileIfExists = True
    If Not fileSystem.FileExists(filePath) Then
        On Error GoTo 0
        Exit Function
    End If

    fileSystem.DeleteFile filePath, True
    If Err.Number <> 0 Then
        LogMessage "DeleteFile failed path=" & filePath & " Err.Number=" & Err.Number & " Description=" & Err.Description
        Err.Clear
        DeleteFileIfExists = False
    End If

    On Error GoTo 0
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

Private Function NormalizeCleanupPath(path)
    On Error Resume Next

    Dim fileSystem
    Dim normalizedPath

    path = TrimTrailingSlash(path)
    If path = "" Then
        NormalizeCleanupPath = ""
        Exit Function
    End If

    Set fileSystem = CreateObject("Scripting.FileSystemObject")
    normalizedPath = fileSystem.GetAbsolutePathName(path)
    If Err.Number <> 0 Then
        LogMessage "NormalizeCleanupPath failed path=" & path & " Err.Number=" & Err.Number & " Description=" & Err.Description
        Err.Clear
        NormalizeCleanupPath = ""
        Exit Function
    End If

    NormalizeCleanupPath = TrimTrailingSlash(normalizedPath)
End Function

Private Function IsProtectedCleanupPath(path)
    Dim normalizedPath
    Dim protectedRoots
    Dim root
    Dim normalizedRoot

    normalizedPath = EnsureTrailingSlash(path)
    protectedRoots = Array( _
        ResolveEnvironmentPath("%ProgramFiles%"), _
        ResolveEnvironmentPath("%ProgramFiles(x86)%"), _
        ResolveEnvironmentPath("%WINDIR%"))

    For Each root In protectedRoots
        normalizedRoot = EnsureTrailingSlash(TrimTrailingSlash(root))
        If normalizedRoot <> "" Then
            If StrComp(Left(normalizedPath, Len(normalizedRoot)), normalizedRoot, vbTextCompare) = 0 Then
                IsProtectedCleanupPath = True
                Exit Function
            End If
        End If
    Next

    IsProtectedCleanupPath = False
End Function

Private Function IsUnsafeBroadCleanupPath(path)
    Dim fileSystem
    Dim driveRoot
    Dim normalizedPath
    Dim broadRoots
    Dim root
    Dim normalizedRoot

    Set fileSystem = CreateObject("Scripting.FileSystemObject")
    normalizedPath = TrimTrailingSlash(path)
    driveRoot = TrimTrailingSlash(fileSystem.GetDriveName(normalizedPath))

    If driveRoot <> "" Then
        If StrComp(normalizedPath, driveRoot, vbTextCompare) = 0 Then
            IsUnsafeBroadCleanupPath = True
            Exit Function
        End If
    End If

    broadRoots = Array( _
        ResolveEnvironmentPath("%USERPROFILE%"), _
        ResolveEnvironmentPath("%LOCALAPPDATA%"), _
        ResolveEnvironmentPath("%APPDATA%"), _
        ResolveEnvironmentPath("%TEMP%"))

    For Each root In broadRoots
        normalizedRoot = TrimTrailingSlash(root)
        If normalizedRoot <> "" Then
            If IsSamePathOrDescendant(normalizedPath, normalizedRoot) Then
                IsUnsafeBroadCleanupPath = Not HasLogReaderSpecificSegment(normalizedPath, normalizedRoot)
                Exit Function
            End If
        End If
    Next

    IsUnsafeBroadCleanupPath = False
End Function

Private Function IsSamePathOrDescendant(path, root)
    If StrComp(path, root, vbTextCompare) = 0 Then
        IsSamePathOrDescendant = True
        Exit Function
    End If

    root = EnsureTrailingSlash(root)
    IsSamePathOrDescendant = (StrComp(Left(path, Len(root)), root, vbTextCompare) = 0)
End Function

Private Function HasLogReaderSpecificSegment(path, root)
    Dim relativePath
    Dim segments
    Dim segment

    HasLogReaderSpecificSegment = False
    If StrComp(path, root, vbTextCompare) = 0 Then
        Exit Function
    End If

    relativePath = Mid(path, Len(EnsureTrailingSlash(root)) + 1)
    segments = Split(relativePath, "\")
    For Each segment In segments
        If InStr(1, segment, "LogReader", vbTextCompare) > 0 Then
            HasLogReaderSpecificSegment = True
            Exit Function
        End If
    Next
End Function

Private Function ResolveEnvironmentPath(variableName)
    Dim shell

    Set shell = CreateObject("WScript.Shell")
    ResolveEnvironmentPath = TrimTrailingSlash(shell.ExpandEnvironmentStrings(variableName))
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

Option Explicit

Const msiDoActionStatusSuccess = 1
Const msiDoActionStatusUserExit = 2
Const msiDoActionStatusFailure = 3
Const installConfigFileName = "WeezTail.install.json"
Const msiUserStorageSelectionFileName = "WeezTail.msi-user.json"
Const storageSetupDirectoryName = "WeezTailSetup"
Const storageRootDirectoryName = "WeezTail"
Const legacyInstallConfigFileName = "LogReader.install.json"
Const legacyMsiUserStorageSelectionFileName = "LogReader.msi-user.json"
Const legacyStorageSetupDirectoryName = "LogReaderSetup"
Const legacyStorageRootDirectoryName = "LogReader"
' Stable through the LogReader MSI lineage; used to find a custom legacy install directory before upgrade removal.
Const legacyMainExecutableComponentCode = "{FE07166C-F174-4245-A4A8-BD7EED904D05}"
Const storageDataDirectoryName = "Data"
Const storageCacheDirectoryName = "Cache"

Function MigrateLegacyStorageSelection()
    On Error Resume Next

    Dim currentSelectionPath
    Dim fileSystem
    Dim legacySelectionSourcePath
    Dim normalizedStorageRoot
    Dim storageRoot

    currentSelectionPath = ResolveCurrentUserSelectionPath()
    Set fileSystem = CreateObject("Scripting.FileSystemObject")
    If Err.Number <> 0 Then
        LogMessage "MigrateLegacyStorageSelection could not create the file-system helper. Err.Number=" _
            & Err.Number & " Description=" & Err.Description
        Err.Clear
        MigrateLegacyStorageSelection = msiDoActionStatusFailure
        Exit Function
    End If

    If fileSystem.FileExists(currentSelectionPath) Then
        LogMessage "MigrateLegacyStorageSelection kept the existing WeezTail selection."
        MigrateLegacyStorageSelection = msiDoActionStatusSuccess
        Exit Function
    End If

    ResolveLegacyStorageCandidate fileSystem, legacySelectionSourcePath, storageRoot
    If legacySelectionSourcePath <> "" Then
        If Not CopyUserStorageSelection( _
            fileSystem, _
            legacySelectionSourcePath, _
            currentSelectionPath) Then
            MigrateLegacyStorageSelection = msiDoActionStatusFailure
            Exit Function
        End If

        LogMessage "MigrateLegacyStorageSelection copied legacy selection=" & legacySelectionSourcePath
        MigrateLegacyStorageSelection = msiDoActionStatusSuccess
        Exit Function
    End If

    If storageRoot = "" Then
        LogMessage "MigrateLegacyStorageSelection found no legacy storage metadata to migrate."
        MigrateLegacyStorageSelection = msiDoActionStatusSuccess
        Exit Function
    End If

    normalizedStorageRoot = NormalizeCleanupPath(storageRoot)
    If normalizedStorageRoot = "" Then
        LogMessage "MigrateLegacyStorageSelection rejected invalid storage root=" & storageRoot
        MigrateLegacyStorageSelection = msiDoActionStatusSuccess
        Exit Function
    End If

    If IsProtectedCleanupPath(normalizedStorageRoot) Then
        LogMessage "MigrateLegacyStorageSelection rejected protected storage root=" & storageRoot
        MigrateLegacyStorageSelection = msiDoActionStatusSuccess
        Exit Function
    End If

    If IsUnsafeBroadCleanupPath(normalizedStorageRoot) Then
        LogMessage "MigrateLegacyStorageSelection rejected storage root=" & storageRoot
        MigrateLegacyStorageSelection = msiDoActionStatusSuccess
        Exit Function
    End If

    If Not SaveUserStorageSelection(fileSystem, currentSelectionPath, normalizedStorageRoot) Then
        MigrateLegacyStorageSelection = msiDoActionStatusFailure
        Exit Function
    End If

    LogMessage "MigrateLegacyStorageSelection adopted storage root=" & normalizedStorageRoot
    MigrateLegacyStorageSelection = msiDoActionStatusSuccess
End Function

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
        "Remove WeezTail data and cache for the current Windows user?" & vbCrLf & _
        storageRoot, _
        vbYesNo + vbQuestion, _
        "WeezTail Setup")

    If result = vbYes Then
        Session.Property("REMOVELOGREADERDATA") = "1"
        Session.Property("LOGREADERDATAROOT") = storageRoot

        If InstallUsesPerUserChoice() Then
            Session.Property("LOGREADERUSERSELECTIONPATH") = ResolveEffectiveUserSelectionPath()
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
        LogMessage "RemoveDataFolders completed with cleanup failures. Some WeezTail data may remain."
        If Session.Property("UILevel") <> "" And CInt(Session.Property("UILevel")) >= 5 Then
            MsgBox "WeezTail Setup could not remove all selected data. Some files may remain under:" & vbCrLf & storageRoot, _
                vbOKOnly + vbExclamation, _
                "WeezTail Setup"
        End If
    End If

    RemoveDataFolders = msiDoActionStatusSuccess
End Function

Private Function ResolveCleanupStorageRoot()
    Dim rawStorageRoot
    Dim normalizedStorageRoot

    If InstallUsesPerUserChoice() Then
        rawStorageRoot = ResolvePerUserCleanupStorageRoot()
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

Private Sub ResolveLegacyStorageCandidate(fileSystem, ByRef selectionSourcePath, ByRef storageRoot)
    Dim legacyConfigPath
    Dim legacySelectionPath
    Dim storageMode

    selectionSourcePath = ""
    storageRoot = ""
    legacyConfigPath = ResolveLegacyInstallConfigPath(fileSystem)
    legacySelectionPath = ResolveLegacyUserSelectionPath()
    If legacyConfigPath <> "" Then
        storageMode = LCase(Trim(LoadJsonStringValue(legacyConfigPath, "storageMode")))
        Select Case storageMode
            Case "absolute"
                storageRoot = TrimTrailingSlash( _
                    LoadJsonStringValue(legacyConfigPath, "storageRootPath"))
            Case "peruserchoice"
                If fileSystem.FileExists(legacySelectionPath) Then
                    selectionSourcePath = legacySelectionPath
                ElseIf fileSystem.FolderExists(ResolveLegacyDefaultStorageRoot()) Then
                    storageRoot = ResolveLegacyDefaultStorageRoot()
                End If
            Case Else
                LogMessage "ResolveLegacyStorageCandidate ignored unsupported storage mode=" & storageMode
        End Select

        Exit Sub
    End If

    If fileSystem.FileExists(legacySelectionPath) Then
        selectionSourcePath = legacySelectionPath
        Exit Sub
    End If

    storageRoot = ResolveLegacyDefaultStorageRoot()
    If Not fileSystem.FolderExists(storageRoot) Then
        storageRoot = ""
    End If
End Sub

Private Function ResolveLegacyInstallConfigPath(fileSystem)
    On Error Resume Next

    Dim candidatePath
    Dim componentPath
    Dim productCode
    Dim relatedProducts

    ResolveLegacyInstallConfigPath = ""
    relatedProducts = Session.Property("WIX_UPGRADE_DETECTED")
    If relatedProducts = "" Then
        Exit Function
    End If

    For Each productCode In Split(relatedProducts, ";")
        productCode = Trim(productCode)
        If productCode <> "" Then
            Err.Clear
            componentPath = Session.Installer.ComponentPath( _
                productCode, _
                legacyMainExecutableComponentCode)
            If Err.Number <> 0 Then
                LogMessage "ResolveLegacyInstallConfigPath could not locate product=" & productCode _
                    & " Err.Number=" & Err.Number & " Description=" & Err.Description
                Err.Clear
            ElseIf componentPath <> "" Then
                candidatePath = fileSystem.BuildPath( _
                    fileSystem.GetParentFolderName(componentPath), _
                    legacyInstallConfigFileName)
                If fileSystem.FileExists(candidatePath) Then
                    ResolveLegacyInstallConfigPath = candidatePath
                    Exit Function
                End If
            End If
        End If
    Next
End Function

Private Function ResolvePerUserCleanupStorageRoot()
    Dim currentSelectionPath
    Dim fileSystem
    Dim legacySelectionPath

    Set fileSystem = CreateObject("Scripting.FileSystemObject")
    currentSelectionPath = ResolveCurrentUserSelectionPath()
    If fileSystem.FileExists(currentSelectionPath) Then
        ResolvePerUserCleanupStorageRoot = TrimTrailingSlash( _
            LoadUtf8JsonStringValue(currentSelectionPath, "storageRootPath"))
        Exit Function
    End If

    legacySelectionPath = ResolveLegacyUserSelectionPath()
    If fileSystem.FileExists(legacySelectionPath) Then
        ResolvePerUserCleanupStorageRoot = TrimTrailingSlash( _
            LoadUtf8JsonStringValue(legacySelectionPath, "storageRootPath"))
        Exit Function
    End If

    If fileSystem.FolderExists(ResolveLegacyDefaultStorageRoot()) Then
        ResolvePerUserCleanupStorageRoot = ResolveLegacyDefaultStorageRoot()
    Else
        ResolvePerUserCleanupStorageRoot = ""
    End If
End Function

Private Function CopyUserStorageSelection(fileSystem, sourcePath, selectionPath)
    On Error Resume Next

    Dim temporaryPath

    CopyUserStorageSelection = False
    If fileSystem.FileExists(selectionPath) Then
        CopyUserStorageSelection = True
        Exit Function
    End If

    If Not PrepareTemporarySelection(fileSystem, selectionPath, temporaryPath) Then
        Exit Function
    End If

    fileSystem.CopyFile sourcePath, temporaryPath, True
    If Err.Number <> 0 Then
        LogMessage "CopyUserStorageSelection could not copy source=" & sourcePath _
            & " Err.Number=" & Err.Number & " Description=" & Err.Description
        Err.Clear
        DeleteTemporarySelection fileSystem, temporaryPath
        Exit Function
    End If

    CopyUserStorageSelection = CommitTemporarySelection( _
        fileSystem, _
        temporaryPath, _
        selectionPath)
End Function

Private Function SaveUserStorageSelection(fileSystem, selectionPath, storageRoot)
    On Error Resume Next

    Dim stream
    Dim temporaryPath

    SaveUserStorageSelection = False
    If fileSystem.FileExists(selectionPath) Then
        SaveUserStorageSelection = True
        Exit Function
    End If

    If Not PrepareTemporarySelection(fileSystem, selectionPath, temporaryPath) Then
        Exit Function
    End If

    Set stream = CreateObject("ADODB.Stream")
    If Err.Number <> 0 Then
        LogMessage "SaveUserStorageSelection could not create the UTF-8 stream." _
            & " Err.Number=" & Err.Number & " Description=" & Err.Description
        Err.Clear
        DeleteTemporarySelection fileSystem, temporaryPath
        Exit Function
    End If

    stream.Type = 2
    stream.Charset = "utf-8"
    stream.Open
    stream.WriteText "{""storageRootPath"":""" & Replace(storageRoot, "\", "\\") & """}"
    stream.SaveToFile temporaryPath, 2
    stream.Close
    If Err.Number <> 0 Then
        LogMessage "SaveUserStorageSelection could not write temporary file=" & temporaryPath _
            & " Err.Number=" & Err.Number & " Description=" & Err.Description
        Err.Clear
        DeleteTemporarySelection fileSystem, temporaryPath
        Exit Function
    End If

    SaveUserStorageSelection = CommitTemporarySelection( _
        fileSystem, _
        temporaryPath, _
        selectionPath)
End Function

Private Function PrepareTemporarySelection(fileSystem, selectionPath, ByRef temporaryPath)
    On Error Resume Next

    Dim selectionDirectory

    PrepareTemporarySelection = False
    selectionDirectory = fileSystem.GetParentFolderName(selectionPath)
    If Not fileSystem.FolderExists(selectionDirectory) Then
        fileSystem.CreateFolder selectionDirectory
        If Err.Number <> 0 Then
            LogMessage "PrepareTemporarySelection could not create directory=" & selectionDirectory _
                & " Err.Number=" & Err.Number & " Description=" & Err.Description
            Err.Clear
            Exit Function
        End If
    End If

    temporaryPath = selectionPath & ".tmp"
    If fileSystem.FileExists(temporaryPath) Then
        fileSystem.DeleteFile temporaryPath, True
        If Err.Number <> 0 Then
            LogMessage "PrepareTemporarySelection could not clear file=" & temporaryPath _
                & " Err.Number=" & Err.Number & " Description=" & Err.Description
            Err.Clear
            Exit Function
        End If
    End If

    PrepareTemporarySelection = True
End Function

Private Function CommitTemporarySelection(fileSystem, temporaryPath, selectionPath)
    On Error Resume Next

    CommitTemporarySelection = False
    If fileSystem.FileExists(selectionPath) Then
        DeleteTemporarySelection fileSystem, temporaryPath
        CommitTemporarySelection = True
        Exit Function
    End If

    fileSystem.MoveFile temporaryPath, selectionPath
    If Err.Number <> 0 Then
        LogMessage "CommitTemporarySelection could not move file=" & temporaryPath _
            & " Err.Number=" & Err.Number & " Description=" & Err.Description
        Err.Clear
        DeleteTemporarySelection fileSystem, temporaryPath
        Exit Function
    End If

    CommitTemporarySelection = True
End Function

Private Sub DeleteTemporarySelection(fileSystem, temporaryPath)
    On Error Resume Next

    If fileSystem.FileExists(temporaryPath) Then
        fileSystem.DeleteFile temporaryPath, True
        If Err.Number <> 0 Then
            LogMessage "DeleteTemporarySelection could not remove file=" & temporaryPath _
                & " Err.Number=" & Err.Number & " Description=" & Err.Description
            Err.Clear
        End If
    End If
End Sub

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

Private Function ResolveEffectiveUserSelectionPath()
    Dim currentSelectionPath
    Dim fileSystem
    Dim legacySelectionPath

    Set fileSystem = CreateObject("Scripting.FileSystemObject")
    currentSelectionPath = ResolveCurrentUserSelectionPath()
    If fileSystem.FileExists(currentSelectionPath) Then
        ResolveEffectiveUserSelectionPath = currentSelectionPath
        Exit Function
    End If

    legacySelectionPath = ResolveLegacyUserSelectionPath()
    If fileSystem.FileExists(legacySelectionPath) Then
        ResolveEffectiveUserSelectionPath = legacySelectionPath
    Else
        ResolveEffectiveUserSelectionPath = currentSelectionPath
    End If
End Function

Private Function ResolveLegacyUserSelectionPath()
    Dim shell

    Set shell = CreateObject("WScript.Shell")
    ResolveLegacyUserSelectionPath = shell.ExpandEnvironmentStrings("%LOCALAPPDATA%") _
        & "\" & legacyStorageSetupDirectoryName _
        & "\" & legacyMsiUserStorageSelectionFileName
End Function

Private Function ResolveLegacyDefaultStorageRoot()
    Dim shell

    Set shell = CreateObject("WScript.Shell")
    ResolveLegacyDefaultStorageRoot = shell.ExpandEnvironmentStrings("%LOCALAPPDATA%") _
        & "\" & legacyStorageRootDirectoryName
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
        If InStr(1, segment, storageRootDirectoryName, vbTextCompare) > 0 _
            Or InStr(1, segment, legacyStorageRootDirectoryName, vbTextCompare) > 0 Then
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
    Dim json
    Dim stream

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

    LoadJsonStringValue = ExtractJsonStringValue(json, propertyName)
End Function

Private Function LoadUtf8JsonStringValue(filePath, propertyName)
    On Error Resume Next

    Dim fileSystem
    Dim json
    Dim stream

    Set fileSystem = CreateObject("Scripting.FileSystemObject")
    If Not fileSystem.FileExists(filePath) Then
        LogMessage "LoadUtf8JsonStringValue missing file=" & filePath & " property=" & propertyName
        LoadUtf8JsonStringValue = ""
        Exit Function
    End If

    Set stream = CreateObject("ADODB.Stream")
    If Err.Number <> 0 Then
        LogMessage "LoadUtf8JsonStringValue could not create the UTF-8 stream." _
            & " Err.Number=" & Err.Number & " Description=" & Err.Description
        Err.Clear
        LoadUtf8JsonStringValue = ""
        Exit Function
    End If

    stream.Type = 2
    stream.Charset = "utf-8"
    stream.Open
    stream.LoadFromFile filePath
    json = stream.ReadText
    stream.Close

    If Err.Number <> 0 Then
        LogMessage "LoadUtf8JsonStringValue failed file=" & filePath & " property=" & propertyName _
            & " Err.Number=" & Err.Number & " Description=" & Err.Description
        Err.Clear
        LoadUtf8JsonStringValue = ""
        Exit Function
    End If

    LoadUtf8JsonStringValue = ExtractJsonStringValue(json, propertyName)
End Function

Private Function ExtractJsonStringValue(json, propertyName)
    Dim colonIndex
    Dim openingQuoteIndex
    Dim propertyIndex
    Dim propertyMarker
    Dim succeeded

    propertyMarker = """" & propertyName & """"
    propertyIndex = InStr(1, json, propertyMarker, vbTextCompare)
    If propertyIndex = 0 Then
        ExtractJsonStringValue = ""
        Exit Function
    End If

    colonIndex = InStr(propertyIndex + Len(propertyMarker), json, ":")
    If colonIndex = 0 Then
        ExtractJsonStringValue = ""
        Exit Function
    End If

    openingQuoteIndex = InStr(colonIndex + 1, json, """")
    If openingQuoteIndex = 0 Then
        ExtractJsonStringValue = ""
        Exit Function
    End If

    ExtractJsonStringValue = DecodeJsonString(json, openingQuoteIndex + 1, succeeded)
    If Not succeeded Then
        ExtractJsonStringValue = ""
    End If
End Function

Private Function DecodeJsonString(json, valueStartIndex, ByRef succeeded)
    Dim character
    Dim codeUnit
    Dim decodedValue
    Dim escapedCharacter
    Dim hexSequence
    Dim index

    succeeded = False
    decodedValue = ""
    index = valueStartIndex

    Do While index <= Len(json)
        character = Mid(json, index, 1)
        If character = """" Then
            succeeded = True
            DecodeJsonString = decodedValue
            Exit Function
        End If

        If character <> "\" Then
            decodedValue = decodedValue & character
        Else
            index = index + 1
            If index > Len(json) Then
                DecodeJsonString = ""
                Exit Function
            End If

            escapedCharacter = Mid(json, index, 1)
            Select Case escapedCharacter
                Case """"
                    decodedValue = decodedValue & """"
                Case "\"
                    decodedValue = decodedValue & "\"
                Case "/"
                    decodedValue = decodedValue & "/"
                Case "b"
                    decodedValue = decodedValue & ChrW(8)
                Case "f"
                    decodedValue = decodedValue & ChrW(12)
                Case "n"
                    decodedValue = decodedValue & vbLf
                Case "r"
                    decodedValue = decodedValue & vbCr
                Case "t"
                    decodedValue = decodedValue & vbTab
                Case "u"
                    If index + 4 > Len(json) Then
                        DecodeJsonString = ""
                        Exit Function
                    End If

                    hexSequence = Mid(json, index + 1, 4)
                    If Not TryParseJsonHexCodeUnit(hexSequence, codeUnit) Then
                        DecodeJsonString = ""
                        Exit Function
                    End If

                    If codeUnit > 32767 Then
                        decodedValue = decodedValue & ChrW(codeUnit - 65536)
                    Else
                        decodedValue = decodedValue & ChrW(codeUnit)
                    End If
                    index = index + 4
                Case Else
                    DecodeJsonString = ""
                    Exit Function
            End Select
        End If

        index = index + 1
    Loop

    DecodeJsonString = ""
End Function

Private Function TryParseJsonHexCodeUnit(hexSequence, ByRef codeUnit)
    Dim character
    Dim characterCode
    Dim digit
    Dim index

    TryParseJsonHexCodeUnit = False
    codeUnit = 0
    If Len(hexSequence) <> 4 Then
        Exit Function
    End If

    For index = 1 To Len(hexSequence)
        character = Mid(hexSequence, index, 1)
        characterCode = AscW(character)
        If characterCode >= 48 And characterCode <= 57 Then
            digit = characterCode - 48
        ElseIf characterCode >= 65 And characterCode <= 70 Then
            digit = characterCode - 65 + 10
        ElseIf characterCode >= 97 And characterCode <= 102 Then
            digit = characterCode - 97 + 10
        Else
            Exit Function
        End If

        codeUnit = (codeUnit * 16) + digit
    Next

    TryParseJsonHexCodeUnit = True
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
    Session.Log "WeezTail Setup: " & message
End Sub

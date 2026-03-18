Option Explicit

Const msiDoActionStatusSuccess = 1
Const msiDoActionStatusUserExit = 2
Const msiDoActionStatusFailure = 3

Function PromptForDataParent()
    Dim defaultParent
    Dim promptResult
    Dim selectedParent

    defaultParent = TrimTrailingSlash(Session.Property("DATAPARENTDIR"))
    If defaultParent = "" Then
        defaultParent = TrimTrailingSlash(Session.Property("LocalAppDataFolder"))
    End If

    promptResult = MsgBox( _
        "LogReader stores app data and cache under:" & vbCrLf & _
        BuildStorageRoot(defaultParent) & vbCrLf & vbCrLf & _
        "Click Yes to use this location or No to choose another folder.", _
        vbYesNoCancel + vbQuestion, _
        "LogReader Setup")

    If promptResult = vbCancel Then
        PromptForDataParent = msiDoActionStatusUserExit
        Exit Function
    End If

    selectedParent = defaultParent
    If promptResult = vbNo Then
        selectedParent = BrowseForFolder(defaultParent)
        If selectedParent = "" Then
            PromptForDataParent = msiDoActionStatusUserExit
            Exit Function
        End If
    End If

    Session.Property("DATAPARENTDIR") = selectedParent
    PromptForDataParent = ValidateDataParent()
End Function

Function ValidateDataParent()
    Dim dataParent
    Dim storageRoot
    Dim validationMessage

    dataParent = TrimTrailingSlash(Session.Property("DATAPARENTDIR"))
    If dataParent = "" Then
        ShowSetupMessage "Choose a data folder before continuing."
        ValidateDataParent = msiDoActionStatusFailure
        Exit Function
    End If

    storageRoot = BuildStorageRoot(dataParent)
    validationMessage = GetStorageValidationMessage(storageRoot)
    If validationMessage <> "" Then
        ShowSetupMessage validationMessage
        ValidateDataParent = msiDoActionStatusFailure
        Exit Function
    End If

    Session.Property("LOGREADERDATAROOT") = storageRoot
    ValidateDataParent = msiDoActionStatusSuccess
End Function

Function WriteInstallConfig()
    On Error Resume Next

    Dim installFolder
    Dim storageRoot
    Dim configPath
    Dim fileSystem
    Dim stream

    installFolder = EnsureTrailingSlash(Session.Property("INSTALLFOLDER"))
    storageRoot = ResolveStorageRoot()
    configPath = installFolder & "LogReader.install.json"

    EnsureFolder storageRoot
    EnsureFolder storageRoot & "\Data"
    EnsureFolder storageRoot & "\Cache"

    Set fileSystem = CreateObject("Scripting.FileSystemObject")
    Set stream = fileSystem.CreateTextFile(configPath, True, False)
    stream.Write "{""installMode"":""Msi"",""storageMode"":""Absolute"",""storageRootPath"":""" & EscapeJson(storageRoot) & """}"
    stream.Close

    If Err.Number <> 0 Then
        ShowSetupMessage "LogReader Setup could not write the install configuration file." & vbCrLf & vbCrLf & Err.Description
        WriteInstallConfig = msiDoActionStatusFailure
        Exit Function
    End If

    WriteInstallConfig = msiDoActionStatusSuccess
End Function

Function PromptRemoveData()
    Dim result

    Session.Property("REMOVELOGREADERDATA") = "0"

    If Session.Property("UILevel") = "" Then
        PromptRemoveData = msiDoActionStatusSuccess
        Exit Function
    End If

    If CInt(Session.Property("UILevel")) < 5 Then
        PromptRemoveData = msiDoActionStatusSuccess
        Exit Function
    End If

    result = MsgBox( _
        "Remove LogReader data and cache?" & vbCrLf & _
        ResolveStorageRoot(), _
        vbYesNo + vbQuestion, _
        "LogReader Setup")

    If result = vbYes Then
        Session.Property("REMOVELOGREADERDATA") = "1"
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

    If Session.Property("REMOVELOGREADERDATA") <> "1" Then
        RemoveDataFolders = msiDoActionStatusSuccess
        Exit Function
    End If

    storageRoot = ResolveStorageRoot()
    dataPath = storageRoot & "\Data"
    cachePath = storageRoot & "\Cache"
    Set fileSystem = CreateObject("Scripting.FileSystemObject")

    If fileSystem.FolderExists(dataPath) Then
        fileSystem.DeleteFolder dataPath, True
    End If

    If fileSystem.FolderExists(cachePath) Then
        fileSystem.DeleteFolder cachePath, True
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

Private Function BrowseForFolder(initialPath)
    On Error Resume Next

    Dim shellApp
    Dim folder

    Set shellApp = CreateObject("Shell.Application")
    Set folder = shellApp.BrowseForFolder(0, "Select the parent folder for LogReader data and cache.", 0, initialPath)

    If Err.Number <> 0 Or folder Is Nothing Then
        BrowseForFolder = ""
    Else
        BrowseForFolder = TrimTrailingSlash(folder.Self.Path)
    End If

    Err.Clear
End Function

Private Function BuildStorageRoot(dataParent)
    BuildStorageRoot = EnsureTrailingSlash(TrimTrailingSlash(dataParent)) & "LogReader"
End Function

Private Function ResolveStorageRoot()
    Dim storageRoot

    storageRoot = TrimTrailingSlash(Session.Property("LOGREADERDATAROOT"))
    If storageRoot = "" Then
        storageRoot = BuildStorageRoot(Session.Property("DATAPARENTDIR"))
    End If

    ResolveStorageRoot = storageRoot
End Function

Private Function GetStorageValidationMessage(storageRoot)
    If IsProtectedPath(storageRoot) Then
        GetStorageValidationMessage = _
            "LogReader cannot store data under protected locations such as Program Files or Windows." & vbCrLf & vbCrLf & _
            "Choose a different data folder parent."
        Exit Function
    End If

    If Not CanCreateAndWrite(storageRoot) Then
        GetStorageValidationMessage = _
            "LogReader Setup could not create or write to this storage location:" & vbCrLf & vbCrLf & _
            storageRoot & vbCrLf & vbCrLf & _
            "Choose a different folder."
        Exit Function
    End If

    GetStorageValidationMessage = ""
End Function

Private Function CanCreateAndWrite(path)
    On Error Resume Next

    Dim fileSystem
    Dim probePath
    Dim stream

    Set fileSystem = CreateObject("Scripting.FileSystemObject")
    EnsureFolder path

    probePath = EnsureTrailingSlash(path) & ".logreader-write-test.tmp"
    Set stream = fileSystem.CreateTextFile(probePath, True, False)
    stream.Write "probe"
    stream.Close

    If fileSystem.FileExists(probePath) Then
        fileSystem.DeleteFile probePath, True
    End If

    CanCreateAndWrite = (Err.Number = 0)
    Err.Clear
End Function

Private Sub EnsureFolder(path)
    On Error Resume Next

    Dim fileSystem
    Dim parentPath

    Set fileSystem = CreateObject("Scripting.FileSystemObject")
    If fileSystem.FolderExists(path) Then
        Exit Sub
    End If

    parentPath = fileSystem.GetParentFolderName(path)
    If parentPath <> "" And Not fileSystem.FolderExists(parentPath) Then
        EnsureFolder parentPath
    End If

    If Not fileSystem.FolderExists(path) Then
        fileSystem.CreateFolder path
    End If
End Sub

Private Function IsProtectedPath(path)
    Dim candidate

    candidate = NormalizePath(path)
    IsProtectedPath = _
        StartsWithPath(candidate, Session.Property("ProgramFiles64Folder")) Or _
        StartsWithPath(candidate, Session.Property("ProgramFilesFolder")) Or _
        StartsWithPath(candidate, Session.Property("WindowsFolder"))
End Function

Private Function StartsWithPath(candidatePath, protectedRoot)
    If Trim(protectedRoot) = "" Then
        StartsWithPath = False
        Exit Function
    End If

    StartsWithPath = (LCase(candidatePath) Like LCase(NormalizePath(protectedRoot)) & "*")
End Function

Private Function NormalizePath(path)
    Dim fileSystem

    Set fileSystem = CreateObject("Scripting.FileSystemObject")
    NormalizePath = LCase(EnsureTrailingSlash(fileSystem.GetAbsolutePathName(path)))
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

Private Function EscapeJson(value)
    value = Replace(value, "\", "\\")
    value = Replace(value, """", "\""")
    EscapeJson = value
End Function

Private Sub ShowSetupMessage(message)
    MsgBox message, vbOKOnly + vbExclamation, "LogReader Setup"
End Sub

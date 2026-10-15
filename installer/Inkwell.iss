; Inkwell installer (Inno Setup 6)
; Built by build.ps1, which publishes each edition to ..\publish\<edition> first.
;   /DEdition=x64     Windows 10/11, 64-bit Intel/AMD (also runs on ARM64 via emulation)
;   /DEdition=arm64   Windows 10/11 on ARM (Snapdragon, Surface Pro X...)
;   /DEdition=x86     Windows 10/11, 32-bit
;   /DEdition=legacy  Windows 7 SP1 / 8.1 (32- or 64-bit), needs .NET Framework 4.8
; This file is UTF-8 with a BOM.

#define AppName "Inkwell"
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef Edition
  #define Edition "x64"
#endif
#define AppExe "Inkwell.exe"
#define DocxProgId "Inkwell.Document"
#define MdProgId "Inkwell.Markdown"
#define PublishDir "..\publish\" + Edition

[Setup]
; Same AppId for every edition: installing another edition simply replaces the current one.
AppId={{B1102FC4-9C59-4C92-B3B7-4D1F6648BA1B}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppName}
AppPublisherURL=https://www.nomanshafiq.com/inkwell/
AppComments=Write beautifully. Without fighting your document.
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=..\dist
#if Edition == "legacy"
OutputBaseFilename=Inkwell-Setup-{#AppVersion}-win7-8
#else
OutputBaseFilename=Inkwell-Setup-{#AppVersion}-{#Edition}
#endif
SetupIconFile=..\src\Inkwell\Assets\Inkwell.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
LZMANumBlockThreads=4
#if Edition == "x64"
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
#elif Edition == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
MinVersion=10.0
#elif Edition == "x86"
MinVersion=10.0
#else
; 32- or 64-bit Intel/AMD Windows 7 SP1 / 8.1 (ARM PCs run Windows 10/11 and use the arm64 edition).
ArchitecturesAllowed=x86os or x64os
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=6.1sp1
#endif
; "Install for all users" (Program Files) by default; "only for me" needs no admin rights.
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog commandline
ChangesAssociations=yes
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "docxopenwith"; Description: "Add {#AppName} to ""Open with"" for Word documents (.docx)"; GroupDescription: "Windows integration:"
Name: "mdopenwith"; Description: "Add {#AppName} to ""Open with"" for Markdown files (.md)"; GroupDescription: "Windows integration:"

[InstallDelete]
; Updating: remove the previous version's program files first, so nothing obsolete is left behind.
; Only in Inkwell's own folder. Settings (%APPDATA%\Inkwell) and the uninstaller (unins000.*) are never touched.
Type: filesandordirs; Name: "{app}\Fonts"; Check: IsOwnFolder
Type: files; Name: "{app}\*.dll"; Check: IsOwnFolder
Type: files; Name: "{app}\*.json"; Check: IsOwnFolder
Type: files; Name: "{app}\*.config"; Check: IsOwnFolder
Type: files; Name: "{app}\*.pdb"; Check: IsOwnFolder
Type: files; Name: "{app}\*.ico"; Check: IsOwnFolder
Type: files; Name: "{app}\THIRD-PARTY-NOTICES.txt"; Check: IsOwnFolder
Type: files; Name: "{app}\createdump.exe"; Check: IsOwnFolder
Type: files; Name: "{app}\{#AppExe}"; Check: IsOwnFolder

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; File types: "Open with" only, never the default app.
; Inkwell adds itself to the "Open with" list of .docx and .md files (OpenWithProgids) and to Default Apps
; (Capabilities), so people can choose it. It never writes the default value of Software\Classes\.docx or
; Software\Classes\.md and never sets itself as the default handler: Word, or whatever app the person chose,
; keeps opening these files on double-click. Making Inkwell the default stays the person's own choice in Windows Settings.

; ---- .docx: an extra "Open with" entry
Root: HKA; Subkey: "Software\Classes\.docx\OpenWithProgids"; ValueType: string; ValueName: "{#DocxProgId}"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: docxopenwith
Root: HKA; Subkey: "Software\Classes\{#DocxProgId}"; ValueType: string; ValueName: ""; ValueData: "Word document (Inkwell)"; Flags: uninsdeletekey; Tasks: docxopenwith
Root: HKA; Subkey: "Software\Classes\{#DocxProgId}"; ValueType: string; ValueName: "FriendlyTypeName"; ValueData: "Word document"; Tasks: docxopenwith
Root: HKA; Subkey: "Software\Classes\{#DocxProgId}\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\InkwellDoc.ico"""; Tasks: docxopenwith
Root: HKA; Subkey: "Software\Classes\{#DocxProgId}\shell\open"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "{#AppName}"; Tasks: docxopenwith
Root: HKA; Subkey: "Software\Classes\{#DocxProgId}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: docxopenwith

; ---- .md: an extra "Open with" entry
Root: HKA; Subkey: "Software\Classes\.md\OpenWithProgids"; ValueType: string; ValueName: "{#MdProgId}"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: mdopenwith
Root: HKA; Subkey: "Software\Classes\{#MdProgId}"; ValueType: string; ValueName: ""; ValueData: "Markdown document (Inkwell)"; Flags: uninsdeletekey; Tasks: mdopenwith
Root: HKA; Subkey: "Software\Classes\{#MdProgId}"; ValueType: string; ValueName: "FriendlyTypeName"; ValueData: "Markdown document"; Tasks: mdopenwith
Root: HKA; Subkey: "Software\Classes\{#MdProgId}\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\InkwellDoc.ico"""; Tasks: mdopenwith
Root: HKA; Subkey: "Software\Classes\{#MdProgId}\shell\open"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "{#AppName}"; Tasks: mdopenwith
Root: HKA; Subkey: "Software\Classes\{#MdProgId}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: mdopenwith

; ---- Application registration (shows up nicely in "Open with" / "Choose another app")
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "{#AppName}"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"",0"
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".docx"; ValueData: ""; Tasks: docxopenwith
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".docm"; ValueData: ""; Tasks: docxopenwith
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".dotx"; ValueData: ""; Tasks: docxopenwith
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".md"; ValueData: ""; Tasks: mdopenwith
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".markdown"; ValueData: ""; Tasks: mdopenwith
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".txt"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".rtf"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".odt"; ValueData: ""

; ---- Default Apps (Settings > Apps > Default apps > Inkwell): lists Inkwell as a choice, doesn't make it the default
Root: HKA; Subkey: "Software\{#AppName}"; Flags: uninsdeletekeyifempty
Root: HKA; Subkey: "Software\{#AppName}\Capabilities"; ValueType: string; ValueName: "ApplicationName"; ValueData: "{#AppName}"; Flags: uninsdeletekey; Tasks: docxopenwith or mdopenwith
Root: HKA; Subkey: "Software\{#AppName}\Capabilities"; ValueType: string; ValueName: "ApplicationDescription"; ValueData: "Write beautifully. Without fighting your document."; Tasks: docxopenwith or mdopenwith
Root: HKA; Subkey: "Software\{#AppName}\Capabilities"; ValueType: string; ValueName: "ApplicationIcon"; ValueData: """{app}\{#AppExe}"",0"; Tasks: docxopenwith or mdopenwith
Root: HKA; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".docx"; ValueData: "{#DocxProgId}"; Tasks: docxopenwith
Root: HKA; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".md"; ValueData: "{#MdProgId}"; Tasks: mdopenwith
Root: HKA; Subkey: "Software\RegisteredApplications"; ValueType: string; ValueName: "{#AppName}"; ValueData: "Software\{#AppName}\Capabilities"; Flags: uninsdeletevalue; Tasks: docxopenwith or mdopenwith

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
; Inkwell's updater runs setup with /VERYSILENT ... /RELAUNCH=1: start Inkwell again afterwards
; (as the signed-in user, not elevated, even when setup asked for administrator rights).
Filename: "{app}\{#AppExe}"; Parameters: "--updated"; Flags: nowait runasoriginaluser; Check: RelaunchRequested

[Code]
// /RELAUNCH=1 on the command line (passed by Inkwell's updater).
function RelaunchRequested(): Boolean;
begin
  Result := ExpandConstant('{param:RELAUNCH|0}') = '1';
end;

// The install folder is Inkwell's own (named Inkwell and holding Inkwell.exe), so old program files can go.
function IsOwnFolder(): Boolean;
begin
  Result := (CompareText(ExtractFileName(RemoveBackslashUnlessRoot(ExpandConstant('{app}'))), '{#AppName}') = 0) and
            FileExists(ExpandConstant('{app}\{#AppExe}'));
end;

#if Edition == "legacy"
function HasDotNet48(): Boolean;
var
  Release: Cardinal;
begin
  Result := RegQueryDWordValue(HKLM, 'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full', 'Release', Release) and (Release >= 528040);
end;

function InitializeSetup(): Boolean;
var
  ErrorCode: Integer;
begin
  Result := True;
  if not HasDotNet48() then
  begin
    if SuppressibleMsgBox('Inkwell for Windows 7 / 8.1 needs Microsoft .NET Framework 4.8, which is not installed on this PC.' + #13#10#13#10 +
              'Open Microsoft''s download page now? Install it, restart if asked, then run this setup again.',
              mbConfirmation, MB_YESNO, IDNO) = IDYES then
      ShellExec('open', 'https://dotnet.microsoft.com/download/dotnet-framework/net48', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
    Result := False;
  end;
end;
#endif

#ifndef ReleaseRoot
  #error ReleaseRoot must point to the staged release directory.
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts"
#endif
#ifndef ProductVersion
  #define ProductVersion "1.0.0-beta.3"
#endif
#ifndef ProductFileVersion
  #define ProductFileVersion "1.0.0.0"
#endif

[Setup]
AppId={{B1200CC2-7E4C-46E2-B809-E5CB5BD0BB04}
AppName=EPLAN EDZ Manager
AppVersion={#ProductVersion}
AppVerName=EPLAN EDZ Manager {#ProductVersion}
AppPublisher=EPLAN EDZ Manager Contributors
DefaultDirName={localappdata}\Programs\EplanEdzManager
DefaultGroupName=EPLAN EDZ Manager
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=EplanEdzManager-{#ProductVersion}-x64-Setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\src\EplanEdzManager.Desktop\Resources\EplanEdzManager.ico
UninstallDisplayIcon={app}\Desktop\EplanEdzManager.Desktop.exe
ChangesAssociations=no
DisableProgramGroupPage=yes
VersionInfoVersion={#ProductFileVersion}
VersionInfoDescription=EPLAN EDZ Manager Unsigned Beta Installer
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#ReleaseRoot}\EplanEdzManager\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\EPLAN EDZ Manager"; Filename: "{app}\Desktop\EplanEdzManager.Desktop.exe"
Name: "{autodesktop}\EPLAN EDZ Manager"; Filename: "{app}\Desktop\EplanEdzManager.Desktop.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式："; Flags: unchecked

[Run]
Filename: "{app}\Desktop\EplanEdzManager.Desktop.exe"; Description: "启动 EPLAN EDZ Manager"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: files; Name: "{app}\AddIn\EplanEdzManager.AddIn.json"

[Code]
const
  NetFramework472Release = 461808;

function IsNetFramework472OrLater(): Boolean;
var
  ReleaseValue: Cardinal;
begin
  Result := RegQueryDWordValue(
    HKLM64,
    'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full',
    'Release',
    ReleaseValue) and (ReleaseValue >= NetFramework472Release);
end;

function InitializeSetup(): Boolean;
begin
  Result := True;
  if (not IsNetFramework472OrLater()) and (not WizardSilent()) then
    MsgBox(
      '.NET Framework 4.7.2 or later was not detected.' + #13#10 + #13#10 +
      'Desktop Offline Mode can still be installed and used because .NET 8 is bundled. ' +
      'EPLAN Bridge/Add-In functions remain unavailable until .NET Framework 4.7.2 or later is installed.',
      mbInformation,
      MB_OK);
end;

function EscapeJsonPath(Value: String): String;
begin
  Result := Value;
  StringChangeEx(Result, '\', '\\', True);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ConfigPath: String;
  ConfigLines: TArrayOfString;
begin
  if CurStep = ssPostInstall then
  begin
    ConfigPath := ExpandConstant('{app}\AddIn\EplanEdzManager.AddIn.json');
    SetArrayLength(ConfigLines, 4);
    ConfigLines[0] := '{';
    ConfigLines[1] := '  "desktopExecutablePath": "' + EscapeJsonPath(ExpandConstant('{app}\Desktop\EplanEdzManager.Desktop.exe')) + '",';
    ConfigLines[2] := '  "connectTimeoutMilliseconds": 2000';
    ConfigLines[3] := '}';
    if not SaveStringsToUTF8File(ConfigPath, ConfigLines, False) then
      RaiseException('Unable to write the installed Add-In configuration.');
  end;
end;


; ============================================================
; SpaceCommsKit — SCK Ground Station Installer
; Inno Setup 6.x script
;
; To build:
;   1. Install Inno Setup 6 from https://jrsoftware.org/isinfo.php
;   2. Build the C# app in Release mode:
;      dotnet publish OpenLstGroundStation\OpenLstGroundStation.csproj
;          -c Release -r win-x64 --self-contained true
;          -o "bin\Release\net8.0-windows\publish\win-x64"
;      Output lands in: bin\Release\net8.0-windows\publish\win-x64\
;   3. Copy rtlsdr\ folder next to this .iss file
;   4. Open this .iss file in Inno Setup Compiler
;   5. Press F9 (Build) — installer .exe appears in Output\ folder
; ============================================================

#define AppName      "SCK Ground Station"
#define AppVersion   "1.2.0"
#define AppPublisher "SpaceCommsKit"
#define AppURL       "https://www.spacecommskit.com"
#define AppExeName   "OpenLstGroundStation.exe"
#define AppCopyright "Copyright (C) 2025 SpaceCommsKit"

; ── Path to your published build output ──────────────────────
#define SourceDir    "C:\Users\maxor\source\repos\2022_projects\OpenLST_GroundStation\bin\Release\net8.0-windows\publish"

[Setup]
; ── Application identity ─────────────────────────────────────
AppId={{B4G8F3E2-5C9D-5F0B-C3G7-D2E6F9B4G0C3}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}
AppUpdatesURL={#AppURL}
AppCopyright={#AppCopyright}

; ── Install location ──────────────────────────────────────────
DefaultDirName={localappdata}\SpaceCommsKit\SCK Ground Station
DefaultGroupName=SpaceCommsKit\SCK Ground Station
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=

; ── Output ────────────────────────────────────────────────────
OutputDir=C:\Users\maxor\source\repos\2022_projects\OpenLST_GroundStation\installer\Output
OutputBaseFilename=SCK_Ground_Station_Setup_v{#AppVersion}
SetupIconFile=

; ── Appearance ────────────────────────────────────────────────
WizardStyle=modern
WizardSizePercent=120
DisableWelcomePage=no

; ── Compression ───────────────────────────────────────────────
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes

; ── Windows version requirement ──────────────────────────────
MinVersion=10.0.17763
; .NET 8 requires Windows 10 version 1809 or later

; ── Uninstall ────────────────────────────────────────────────
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}

; ── Restart ──────────────────────────────────────────────────
RestartIfNeededByRun=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
; Desktop shortcut — optional, unchecked by default
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; \
    GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

; Start menu shortcut — always created
Name: "startmenuicon"; Description: "Create Start Menu shortcut"; \
    GroupDescription: "{cm:AdditionalIcons}"; Flags: checkedonce

[Files]
; ── Main application (self-contained .NET 8 publish) ─────────
Source: "{#SourceDir}\*"; DestDir: "{app}"; \
    Flags: ignoreversion recursesubdirs createallsubdirs

; ── Documentation ────────────────────────────────────────────
; Uncomment when PDFs are ready:
; Source: "docs\SCK_Ground_Station_User_Guide.pdf"; \
;     DestDir: "{app}\docs"; Flags: ignoreversion

; ── RTL-SDR Tools (GPLv2 — bundled for RF QA tab) ───────────
; Source: github.com/rtlsdrblog/rtl-sdr-blog (GPLv2)
Source: "rtlsdr\*"; \
    DestDir: "{app}\rtlsdr"; \
    Flags: ignoreversion recursesubdirs

[Icons]
; Start menu
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"; \
    Comment: "SpaceCommsKit SCK Ground Station"

; Desktop (only if task selected)
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; \
    Tasks: desktopicon; Comment: "SpaceCommsKit SCK Ground Station"

; Uninstall entry in Start menu
Name: "{group}\Uninstall {#AppName}"; \
    Filename: "{uninstallexe}"

[Run]
; Offer to launch app after install
Filename: "{app}\{#AppExeName}"; \
    Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; \
    Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Clean up app-generated files on uninstall
Type: filesandordirs; Name: "{app}\Log"
Type: filesandordirs; Name: "{app}\QA_Snapshots"
Type: filesandordirs; Name: "{app}\temp"
Type: files;          Name: "{app}\appsettings.json"
Type: files;          Name: "{app}\customcommands.json"
Type: files;          Name: "{app}\smartrf_path.cfg"

[Code]
function InitializeSetup(): Boolean;
begin
  Result := True;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    // App is self-contained — nothing extra needed
  end;
end;

; ============================================================
; SpaceCommsKit — SCK Ground Station Installer
; Inno Setup 6.x script
;
; To build (recommended): run build_gs_installer.ps1 from the project
; folder. It does a clean self-contained publish, checks that no
; settings/keys are in the publish folder, then compiles this script.
;
; By hand:
;   1. Install Inno Setup 6 from https://jrsoftware.org/isinfo.php
;   2. dotnet publish OpenLstGroundStation.csproj -c Release -r win-x64
;          --self-contained true -o bin\Release\net8.0-windows\publish
;   3. rtlsdr\ next to this .iss, or pass /DRtlSdrDir=<path to rtlsdr>
;   4. Open this .iss in Inno Setup Compiler, press F9.
;      Installer .exe appears in installer\Output\
;
; Keep AppId unchanged: it lets a new version install over the old one.
; ============================================================

#define AppName      "SCK Ground Station"
; Default version; build_gs_installer.ps1 passes /DAppVersion=x.y.z
#ifndef AppVersion
  #define AppVersion "1.3.0"
#endif
#define AppPublisher "SpaceCommsKit"
#define AppURL       "https://www.spacecommskit.com"
#define AppExeName   "OpenLstGroundStation.exe"
#define AppCopyright "Copyright (C) 2025-2026 SpaceCommsKit"

; ── Path to your published build output ──────────────────────
; Relative to this .iss file (keep the .iss in the project folder)
#define SourceDir    SourcePath + "bin\Release\net8.0-windows\publish"

; RTL-SDR tools: the build script passes /DRtlSdrDir=<repo>\installer\rtlsdr
#ifndef RtlSdrDir
  #define RtlSdrDir SourcePath + "rtlsdr"
#endif

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
OutputDir={#SourcePath}installer\Output
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
; Excludes: never ship a developer's settings (COM port, paths and the
; Provision-tab AES keys live in appsettings.json) or logs.
Source: "{#SourceDir}\*"; DestDir: "{app}"; \
    Excludes: "appsettings.json,customcommands.json,smartrf_path.cfg,Log\*,QA_Snapshots\*,temp\*"; \
    Flags: ignoreversion recursesubdirs createallsubdirs

; ── Documentation ────────────────────────────────────────────
; Uncomment when PDFs are ready:
; Source: "docs\SCK_Ground_Station_User_Guide.pdf"; \
;     DestDir: "{app}\docs"; Flags: ignoreversion

; ── RTL-SDR Tools (GPLv2 — bundled for RF QA tab) ───────────
; Source: github.com/rtlsdrblog/rtl-sdr-blog (GPLv2)
Source: "{#RtlSdrDir}\*"; \
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

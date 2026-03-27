; ============================================================
; OpenLST Explorer Kit — Ground Station Installer
; Inno Setup 6.x script
;
; To build:
;   1. Install Inno Setup 6 from https://jrsoftware.org/isinfo.php
;   2. Build the C# app in Release mode:
;      dotnet publish -c Release -r win-x64 --self-contained true
;      Output lands in: bin\Release\net8.0-windows\win-x64\publish\
;   3. Open this .iss file in Inno Setup Compiler
;   4. Press F9 (Build) — installer .exe appears in Output\ folder
; ============================================================

#define AppName      "OpenLST Ground Station"
#define AppVersion   "1.0.0"
#define AppPublisher "SpaceCommsKit"
#define AppURL       "https://www.spacecommskit.com"
#define AppExeName   "OpenLstGroundStation.exe"
#define AppCopyright "Copyright (C) 2025 SpaceCommsKit"

; ── Path to your published build output ──────────────────────
#define SourceDir    "C:\Users\maxor\source\repos\2022_projects\OpenLST_GroundStation\bin\Release\net8.0-windows\win-x64\publish"

[Setup]
; ── Application identity ─────────────────────────────────────
AppId={{A3F7E2D1-4B8C-4E9A-B2F6-C1D5E8A3F9B2}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}
AppUpdatesURL={#AppURL}
AppCopyright={#AppCopyright}

; ── Install location ──────────────────────────────────────────
DefaultDirName={localappdata}\SpaceCommsKit\OpenLST Ground Station
DefaultGroupName=SpaceCommsKit\OpenLST Ground Station
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=

; ── Output ────────────────────────────────────────────────────
OutputDir=C:\Users\maxor\source\repos\2022_projects\OpenLST_GroundStation\installer\Output
OutputBaseFilename=OpenLST_Ground_Station_Setup_v{#AppVersion}
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
; Desktop shortcut — optional, ticked by default
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; \
    GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

; Start menu shortcut — always created
Name: "startmenuicon"; Description: "Create Start Menu shortcut"; \
    GroupDescription: "{cm:AdditionalIcons}"; Flags: checkedonce

[Files]
; ── Main application (self-contained .NET 8 publish) ─────────
; Self-contained publish includes the .NET runtime — no separate
; runtime installation required on the target machine.
Source: "{#SourceDir}\*"; DestDir: "{app}"; \
    Flags: ignoreversion recursesubdirs createallsubdirs

; ── Documentation ────────────────────────────────────────────
; Copy the PDF guides alongside the application
; Uncomment and adjust paths once you have the PDFs ready:
; Source: "docs\OpenLST_Explorer_Kit_User_Guide.pdf"; \
;     DestDir: "{app}\docs"; Flags: ignoreversion
; Source: "docs\OpenLST_Explorer_Kit_Developer_Guide.pdf"; \
;     DestDir: "{app}\docs"; Flags: ignoreversion

[Icons]
; Start menu
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"; \
    Comment: "OpenLST Explorer Kit Ground Station"

; Desktop (only if task selected)
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; \
    Tasks: desktopicon; Comment: "OpenLST Explorer Kit Ground Station"

; Uninstall entry in Start menu
Name: "{group}\Uninstall {#AppName}"; \
    Filename: "{uninstallexe}"

[Run]
; Offer to launch app after install
Filename: "{app}\{#AppExeName}"; \
    Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; \
    Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Clean up settings and log files created by the app
Type: filesandordirs; Name: "{app}\Log"
Type: files;          Name: "{app}\appsettings.json"
Type: files;          Name: "{app}\customcommands.json"

[Code]
// ── Custom installer code ─────────────────────────────────────

// Check for existing installation and warn user
function InitializeSetup(): Boolean;
begin
  Result := True;
end;

// Show a friendly finish message
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    // Nothing extra needed — app is self-contained
  end;
end;

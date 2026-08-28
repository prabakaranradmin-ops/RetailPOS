; RetailPOS installer.
;
; Produces a single setup.exe carrying everything a lane needs. The two executables inside it are
; already self-contained — the .NET runtime is bundled into each — so the target machine needs
; nothing installed beforehand. This exists for the things a copied folder cannot do: a shortcut a
; cashier can find, the till coming back by itself after a reboot, and a clean uninstall.
;
; Built by build-installer.ps1, which publishes first so the payload is never stale.

#define AppName        "RetailPOS"
#define AppPublisher   "MaaranSoft"

; Passed in by build-installer.ps1, which takes it from the git tag. It was written here as a
; literal once, and the literal went stale the moment the next release was cut: the installer
; carried v1.1.0 code while telling Add/Remove Programs it was 1.0.0, so anyone auditing which
; build a lane was running got a confident wrong answer.
;
; The fallback is deliberately not a plausible version number. A build made by running ISCC by hand
; should announce itself as a hand build rather than impersonate a release.
#ifndef AppVersion
  #define AppVersion "0.0.0-handbuilt"
#endif

; Windows will only accept digits and dots in a file version, so a tag like 1.1.0-RC2 needs a
; numeric twin. The display version above is what a person reads; this is what the file carries.
#ifndef AppVersionNumeric
  #define AppVersionNumeric "0.0.0"
#endif
; Which of the two builds this is packaging. "NoTax" issues bills of supply and cannot charge GST;
; anything else is the ordinary GST build.
;
; It changes the setup file's name and what Add/Remove Programs shows, so a shop can tell which one
; it has without opening the till. It deliberately does NOT change AppId: the two are the same
; product, so installing one over the other upgrades in place and the lane keeps its database,
; settings and backups rather than ending up with two copies fighting over one data folder.
#ifndef Variant
  #define Variant "Gst"
#endif

#if Variant == "NoTax"
  #define VariantSuffix "-NoTax"
  #define VariantLabel  " (no tax)"
#else
  #define VariantSuffix "-GST"
  #define VariantLabel  " (GST)"
#endif

#define TillExe        "Pos.App.exe"
#define ToolExe        "pos.exe"
#define Payload        "..\..\artifacts\lane"

[Setup]
AppId={{75A3C612-99E2-4A24-8A82-1A5C7E990FB1}
AppName={#AppName}{#VariantLabel}
AppVersion={#AppVersion}
AppVerName={#AppName}{#VariantLabel} {#AppVersion}
AppPublisher={#AppPublisher}
VersionInfoVersion={#AppVersionNumeric}

; Per-user by default, and deliberately so — see the note in build-installer.ps1. The lane's
; database and settings live under the *running* user's LocalAppData, and an installer elevated as
; somebody else would put them in the wrong profile. Choosing "all users" in the dialog is still
; allowed for anyone who knows they want it.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
AllowNoIcons=yes

OutputDir=..\..\artifacts\installer
OutputBaseFilename={#AppName}{#VariantSuffix}-Setup-{#AppVersion}

; No SetupIconFile: that wants a .ico to embed, and the only icon we have is inside a 174MB
; executable. The installed shortcuts and the uninstall entry take their icon from the executable
; itself, which is where it already lives.
UninstallDisplayIcon={app}\{#TillExe}
UninstallDisplayName={#AppName}{#VariantLabel} {#AppVersion}

; The payload is two 174MB executables that compress well. Solid compression across both of them
; takes the setup from roughly 350MB to something that fits on a memory stick and copies in a
; minute rather than ten.
Compression=lzma2/max
SolidCompression=yes
LZMANumBlockThreads=4

ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
InfoAfterFile=after-install.txt

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Put a shortcut on the desktop"; GroupDescription: "Shortcuts:"
Name: "startupicon"; Description: "Open the till automatically when this machine starts"; GroupDescription: "Shortcuts:"

[Files]
Source: "{#Payload}\{#TillExe}";                 DestDir: "{app}"; Flags: ignoreversion
Source: "{#Payload}\{#ToolExe}";                 DestDir: "{app}"; Flags: ignoreversion
Source: "{#Payload}\settings.pilot-tamil.json";  DestDir: "{app}"; Flags: ignoreversion
Source: "{#Payload}\settings.json";              DestDir: "{app}"; Flags: ignoreversion
Source: "{#Payload}\catalog_template.csv";       DestDir: "{app}"; Flags: ignoreversion
Source: "{#Payload}\SETTINGS.md";                DestDir: "{app}"; Flags: ignoreversion
Source: "{#Payload}\CATALOGUE_FORMAT.md";        DestDir: "{app}"; Flags: ignoreversion
Source: "{#Payload}\HARDWARE_SIGNOFF.md";        DestDir: "{app}"; Flags: ignoreversion
Source: "{#Payload}\PILOT_RUNBOOK.md";           DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName} Till";        Filename: "{app}\{#TillExe}"
Name: "{group}\Operator runbook";       Filename: "{app}\PILOT_RUNBOOK.md"
Name: "{group}\{#AppName} commands";    Filename: "{cmd}"; Parameters: "/K cd /d ""{app}"" && echo Run: pos.exe --help"; IconFilename: "{app}\{#ToolExe}"
Name: "{group}\Uninstall {#AppName}";   Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName} Till";  Filename: "{app}\{#TillExe}"; Tasks: desktopicon
Name: "{userstartup}\{#AppName} Till";  Filename: "{app}\{#TillExe}"; Tasks: startupicon

[Code]
{
  Puts a settings file in place on a lane that has none, so the shop is one edit away from trading
  rather than hunting for a template.

  It never overwrites. Re-installing over a trading lane must not touch the shop's identity, its
  printer name, or anything else somebody got right on the bench — and the same folder holds the
  database, so this code deliberately creates the folder and one file and nothing else.
}
procedure CurStepChanged(CurStep: TSetupStep);
var
  LaneDir, Target, Source: String;
begin
  if CurStep <> ssPostInstall then
    Exit;

  LaneDir := ExpandConstant('{localappdata}\RetailPOS');
  Target := LaneDir + '\settings.json';
  Source := ExpandConstant('{app}\settings.pilot-tamil.json');

  if not DirExists(LaneDir) then
    ForceDirectories(LaneDir);

  if not FileExists(Target) then
    FileCopy(Source, Target, True);
end;

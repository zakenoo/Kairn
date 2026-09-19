; Installateur de Kairn (Inno Setup 6).
; Compiler : ISCC.exe installer\Kairn.iss   (après « dotnet publish » autonome dans release\)

#define AppName "Kairn"
#define AppVersion "1.0.0"
#define AppExe "Kairn.exe"

[Setup]
AppId={{6E3C9F2A-4B7D-4E1A-9C58-2D7F0B1A8E43}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Zak
AppPublisherURL=https://github.com/zakenoo/Kairn
AppSupportURL=https://github.com/zakenoo/Kairn/issues
AppUpdatesURL=https://github.com/zakenoo/Kairn/releases
; Installation pour l'utilisateur, sans droits administrateur.
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
OutputDir=..\release
OutputBaseFilename=Kairn-Setup-{#AppVersion}
SetupIconFile=..\Kairn\Assets\kairn.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
WizardImageFile=wizard.bmp
WizardSmallImageFile=wizard-small.bmp
Compression=lzma2/ultra64
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
; Kairn tourne souvent en arrière-plan : on le ferme proprement avant de le remplacer.
CloseApplications=yes
RestartApplications=no
ShowLanguageDialog=auto

[Languages]
Name: "fr"; MessagesFile: "compiler:Languages\French.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "pt"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "de"; MessagesFile: "compiler:Languages\German.isl"
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "ja"; MessagesFile: "compiler:Languages\Japanese.isl"
Name: "ar"; MessagesFile: "compiler:Languages\Arabic.isl"

[CustomMessages]
fr.DeleteData=Supprimer aussi tes données Kairn (tâches, objectifs, bibliothèque, thèmes) ?%n%nChoisis « Non » pour les garder si tu comptes réinstaller Kairn.
en.DeleteData=Also delete your Kairn data (tasks, goals, library, themes)?%n%nChoose “No” to keep them if you plan to reinstall Kairn.
es.DeleteData=¿Eliminar también tus datos de Kairn (tareas, objetivos, biblioteca, temas)?%n%nElige «No» para conservarlos si piensas reinstalar Kairn.
pt.DeleteData=Excluir também seus dados do Kairn (tarefas, objetivos, biblioteca, temas)?%n%nEscolha “Não” para mantê-los se for reinstalar o Kairn.
de.DeleteData=Auch deine Kairn-Daten löschen (Aufgaben, Ziele, Bibliothek, Themes)?%n%nWähle „Nein“, um sie zu behalten, falls du Kairn neu installieren willst.
ru.DeleteData=Удалить также данные Kairn (задачи, цели, библиотеку, темы)?%n%nВыбери «Нет», чтобы сохранить их, если собираешься переустановить Kairn.
ja.DeleteData=Kairn のデータ（タスク、目標、ライブラリ、テーマ）も削除しますか？%n%n再インストールする予定なら「いいえ」を選んで残してください。
ar.DeleteData=هل تريد حذف بيانات Kairn أيضًا (المهام، الأهداف، المكتبة، السمات)؟%n%nاختر «لا» للاحتفاظ بها إن كنت ستعيد تثبيت Kairn.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\release\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Ferme Kairn s'il tourne encore (icône près de l'horloge) avant de retirer ses fichiers.
Filename: "{sys}\taskkill.exe"; Parameters: "/IM {#AppExe} /F"; Flags: runhidden; RunOnceId: "StopKairn"

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    // Le lancement au démarrage de Windows ne doit pas survivre à la désinstallation.
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', '{#AppName}');
    // Les données restent, sauf si la personne demande à les supprimer.
    if DirExists(ExpandConstant('{userappdata}\{#AppName}')) and
       (SuppressibleMsgBox(CustomMessage('DeleteData'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES) then
      DelTree(ExpandConstant('{userappdata}\{#AppName}'), True, True, True);
  end;
end;

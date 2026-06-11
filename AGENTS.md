# AGENTS.md

Huong dan cho AI/IDE agent khi lam viec voi repo nay.

## Muc tieu du an

RcloneDrive GUI la ung dung Windows WinForms quan ly `rclone.exe`: tao profile, them config, mount/ngat o, duyet file, dong bo local/remote, chay Web UI, cai WinFsp/rclone tu dong va toi uu workflow code tren FTP/SFTP.

Source chinh:

```text
RcloneDriveManager/Program.cs
```

Output chinh:

```text
RcloneDrive.exe
```

## Nguyen tac lam viec

- Doc `README.md`, `CHANGELOG.md`, `docs/OPERATIONS.md`, `docs/TROUBLESHOOTING.md` truoc khi sua logic lon.
- Uu tien sua trong `RcloneDriveManager/Program.cs`; app hien la WinForms single-file.
- Khong dung lenh pha huy nhu `git reset --hard` hoac xoa hang loat file neu khong duoc yeu cau ro.
- Neu build bi loi vi `RcloneDrive.exe` dang bi khoa, chi dong process `RcloneDrive.exe`; khong dung `rclone.exe` vi co the dang mount o.
- Khong quet rong tren o mount FTP/SFTP nhu `Z:\public_html` hoac `\\server\...\public_html` neu khong can. Hay doc file cu the hoac thu muc con nho.
- Khi can lam viec voi OpenCode/Codex history, dung mot dang duong dan co dinh. Voi `Network mode`, uu tien UNC `\\server\<ten-o>\public_html`.

## Build

Build tren Windows bang Roslyn C# compiler:

```powershell
& 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe' /codepage:65001 /target:winexe /platform:x64 /win32icon:'.\RcloneDriveManager\RcloneDrive.ico' /out:'.\RcloneDrive.exe' /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll '.\RcloneDriveManager\Program.cs'
```

Sau build, kiem tra:

```powershell
git status --short
Get-Item .\RcloneDrive.exe
```

## Luong tinh nang quan trong

- Auto download `rclone.exe` neu thieu.
- Auto install WinFsp neu thieu.
- Auto update `RcloneDrive.exe` tu GitHub raw theo commit moi nhat cua `main`.
- Mount preset:
  - `Nhanh/RaiDrive`: cache metadata lau hon, doc project nhanh hon.
  - `Live`: cache ngan hon, thay doi remote hien nhanh hon.
- `Network mode` tao o dang network drive va co duong dan UNC.
- Nut `Mo project` uu tien UNC khi `Network mode` bat de OpenCode/Codex khong tach session.
- Log chi cuon doc, co nut `Loi`, `Copy`, `Xoa log`.

## Can than voi FTP/SFTP

FTP shared hosting rat cham khi IDE/agent quet nhieu file nho. Khong nen mo root lon neu co WordPress core. Uu tien mo thu muc con:

```text
public_html/wp-content/themes/<theme>
public_html/wp-content/plugins/<plugin>
```

Neu bat buoc mo root lon, dung preset `Nhanh/RaiDrive` va tranh chay `rg --files` khong gioi han.

## Git

Commit nen nho va mo ta dung hanh vi. Thuong commit ca:

```text
RcloneDriveManager/Program.cs
RcloneDrive.exe
```

Khong commit `rclone.exe`, file cache, file tam update/build sinh ngau nhien dang `*_RcloneDrive.exe`.

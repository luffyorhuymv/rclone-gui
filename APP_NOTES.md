# RcloneDrive GUI - Note tong hop

File nay gom nhanh cac chuc nang, bug da gap, fix da lam va luu y khi tiep tuc phat trien app.

## Muc tieu app

RcloneDrive GUI la app Windows WinForms quan ly `rclone.exe` theo kieu gan giong RaiDrive:

- Tao va quan ly profile mount.
- Them/sua/xoa config rclone tren UI.
- Kiem tra ket noi truoc khi mount.
- Mount/ngat o rclone bang WinFsp.
- Quet cac o rclone dang mount san.
- Tu dong mount lai o da ket noi thanh cong.
- Chay rclone Web UI.
- Duyet file remote.
- Dong bo local/remote cho workflow code.
- Tu tai `rclone.exe` khi thieu.
- Tu cai WinFsp khi thieu.
- Tu update app tu GitHub.

Source chinh:

```text
RcloneDriveManager/Program.cs
```

Output chinh:

```text
RcloneDrive.exe
```

## Chuc nang hien co

### Quan ly profile

- Tao profile moi.
- Luu profile.
- Xoa profile.
- Cai dat/chinh sua profile dang chon.
- Chon remote tu config rclone.
- Dat ten profile tuy y, khong bat buoc trung remote.
- Cung mot remote co the tao nhieu profile/o mount khac nhau.
- Neu bam `Ket noi` khi dang nhap form hien tai, app co the tao/lua profile tu du lieu dang co.

### Ket noi va mount o

- Nut `Ket noi` mount profile dang chon.
- Nut `Ngat` ngat profile/o dang chon.
- Nut `Mo o` mo Explorer vao o da mount.
- Nut `Lam moi o` remount profile dang chon.
- Nut tren tung dong o thao tac dung theo row duoc bam.
- Mount thanh cong thi luu `RestoreOnStartup = true`.
- Ngat thu cong thi luu `RestoreOnStartup = false`.
- Khi mo app binh thuong, app tu mount lai profile co `RestoreOnStartup`.
- Khi chay `--automount`, app mount profile co `RestoreOnStartup` va profile tick `Tu mount khi mo app`.

### Ky tu o dia

- Co tuy chon chon ky tu o thu cong.
- Co tuy chon tu chon o trong.
- Ham auto chon o trong tranh:
  - O Windows dang dung.
  - O rclone dang mount san.
  - Ky tu o da gan trong profile khac cua app.
- Neu profile dung `AUTO`, mount thanh cong app luu lai ky tu o that de lan sau on dinh project path.

### Network mode va OpenCode/Codex

- `Network mode` mount theo dang network drive.
- Nut `Mo project` uu tien duong dan on dinh de OpenCode/Codex khong tach session.
- Voi o WinFsp/network, tranh mo xen ke nhieu dang duong dan cho cung project.
- Nen mo truc tiep thu muc project, vi du:

```text
X:\public_html
```

Khong nen chi mo root:

```text
X:\
```

### Config rclone

- Them config tren UI.
- Co o nhap user/pass cho cac remote can dang nhap.
- Co nut luu config.
- Co nut check ket noi config.
- Co nut mo rclone Web UI.
- App chuan hoa remote path, tranh nhap nham UNC kieu `\\server\share` vao duong dan remote.

### Cloudflare Access TCP tunnel

- Co checkbox `Mount Cloudflare tunnel`.
- App lay hostname tu rclone config, khong can nhap rieng CF Access hostname.
- Tunnel local port co the de auto.
- App chay `cloudflared access tcp --hostname <host> --url localhost:<port>`.
- Sau khi tunnel chay, app tam thoi cho remote ket noi qua `localhost:<port>`.

### FTP/SFTP toi uu cho code

- Co preset `Nhanh/RaiDrive`.
- Co preset `Live`.
- Co nut `Code IDE`.
- Toi uu VFS/cache cho thao tac code:
  - `vfs-cache-mode full`
  - upload delay ngan
  - metadata cache lau hon
  - transfers/checkers thap de tranh host gioi han ket noi
- Co workflow:
  - `Tai ve may`
  - `Day len host`
  - `Mo local`

### Log

- Log dung RichTextBox.
- To mau theo muc:
  - ERROR
  - WARN
  - INFO
  - RCLONE
  - WEB
- Gioi han so dong log de app khong nang.
- Log chi cuon doc, khong dung cuon ngang.
- Co nut:
  - `Loi`
  - `Copy`
  - `Xoa log`

### Cong cu

- Tu tai `rclone.exe` neu thieu.
- Tu cai WinFsp neu thieu.
- Auto update app tu GitHub.
- Cai/chinh auto startup Windows.
- Tao file BAT mount/ngat.
- Don cache profile hoac tat ca cache.

### Linux

- Co script Linux:

```text
linux/rclone-drive-linux.sh
```

- Linux mount vao thu muc, khong dung ky tu o `X:` nhu Windows.
- Script co ho tro cai rclone/FUSE/git va chuan bi project cho OpenCode.

## Bug da gap va da fix

### UI bi che, nut bi che, text chen nhau

Da fix nhieu lan:

- Tang vung `O da cau hinh`.
- Card o cao hon.
- Tach vung text va vung nut.
- Nut tren card co kich thuoc gon hon.
- Khi thieu ngang, nut khong ve de len text.
- Them scroll doc cho cac tab dai.
- Bo thanh cuon ngang log.
- Sap xep lai nhom nut theo chuc nang.
- Bo cac o trang/khung vuong thua trong card o.
- Bo icon to giay ben trai card.
- Bo khung quanh ky tu o `X:`/`R:` de card gon hon.

### Nut tren tung o bam sai profile

Da fix:

- Nut ket noi/ngat lay dung `item.Tag` cua row.
- Nut cai dat mo dung profile.
- Nut mo o mo dung drive.
- Nut xoa/ngat thao tac dung profile/o ngoai.

### App khong quet o da mount san

Da fix:

- Quet process rclone va source mount.
- Nhan dien o rclone dang mount, khong lay tat ca o Windows.
- Chi hien o lien quan rclone.
- Nhan dien profile `AUTO` bang source/process, khong chi dua vao ky tu o.

### Mount bao thanh cong nhung Explorer khong thay o

Nguyen nhan tung gap:

- WinFsp thieu/chua cai.
- App chay Administrator, Explorer chay quyen thuong.
- Ky tu o da ton tai.
- WinFsp/FUSE loi `Cannot create WinFsp-FUSE file system`.

Da them:

- Check WinFsp.
- Nut cai WinFsp.
- Log canh bao khi process chay nhung Windows chua thay o.

### Loi symlink FTP

Log tung gap:

```text
symlinks not supported without the --links flag
```

Da them tham so phu hop de rclone chap nhan symlink khi remote ho tro/can.

### FTP login/path/connect loi

Loi tung gap:

- `530 Login authentication failed`
- `421 Too many connections`
- `directory not found`
- `wsasend: An existing connection was forcibly closed by the remote host`

Da them:

- Preflight `rclone lsf` truoc khi mount.
- Log loi ro hon.
- Goi y khong nhap `\\server\share` vao remote path.
- Preset giam ket noi cho FTP shared hosting.
- Exclude `.ftpquota` do mot so host cam doc file nay.

### OpenCode/Codex mat session

Nguyen nhan:

- Cung project nhung mo bang cac duong dan khac nhau.
- Mo root o mount thay vi thu muc project.
- Project tren o WinFsp/network chua co Git nen agent/IDE tao ID khong on dinh.

Fix/huong dan da them:

- Nut `Mo project`.
- Script `fix-opencode-session.ps1`.
- Khi can, khoi tao Git trong thu muc project.
- Mo thu muc cu the nhu `X:\public_html`.
- Tranh mo xen ke `X:\...` va `\\server\...` cho cung project.

### Auto update chua on dinh

Da cai thien:

- Dung GitHub commit API.
- Tai `RcloneDrive.exe` tu GitHub raw/release flow.
- Ep TLS 1.2.
- Timeout ro.
- Log SHA hien tai/SHA moi.
- Chi nen co mot cua so app do co mutex.

## Release da tao gan day

- `v1.0.0`: chuan hoa UI layout va auto restore.
- `v1.0.1`: fix nut action tren row.
- `v1.0.2`: toi uu UI/button behavior.
- `v1.0.3`: can giua theo man hinh dang active.
- `v1.0.4`: gom settings vao tabs de gon hon.
- `v1.0.5`: fix xem/xoa config.
- `v1.0.6`: them luu va check ket noi config.
- `v1.0.7`: auto drive tranh ky tu da dat trong profile.
- `v1.0.8`: validate ky tu o bi profile khac giu.
- `v1.0.9`: bo o trang icon trong card.
- `v1.0.10`: bo khung icon card.
- `v1.0.11`: bo hoan toan icon/khung vuong con sot trong card.

Quy uoc release tiep theo:

```text
v1.0.12
v1.0.13
...
```

Khong dung tag theo ngay nua.

## Build

Build tren Windows bang Roslyn C# compiler:

```powershell
& 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe' /codepage:65001 /target:winexe /platform:x64 /win32icon:'.\RcloneDriveManager\RcloneDrive.ico' /out:'.\RcloneDrive.exe' /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll '.\RcloneDriveManager\Program.cs'
```

Neu build bi khoa file:

- Chi dong `RcloneDrive.exe`.
- Khong tat `rclone.exe` neu no dang mount o.

## Can than khi sua tiep

- App dang la WinForms single-file, nen uu tien sua trong `RcloneDriveManager/Program.cs`.
- Khong quet rong tren o FTP/SFTP mount nhu `Z:\public_html`.
- Khong commit `rclone.exe`.
- Nen commit ca:

```text
RcloneDriveManager/Program.cs
RcloneDrive.exe
```

- Neu tao release moi, attach `RcloneDrive.exe`.
- Tranh xoa release cu neu khong co yeu cau ro.

## Viec nen lam tiep

- Tach bot code UI/logic ra nhieu file neu du an lon hon.
- Them test nho cho:
  - chon ky tu o auto
  - parse remote path
  - restore mount startup
  - Cloudflare tunnel config
- Them man hinh About/Version de xem release hien tai.
- Them export/import profiles.
- Them backup profiles truoc khi sua config.
- Them health check ro hon cho WinFsp, rclone, cloudflared, git.

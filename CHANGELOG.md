# CHANGELOG

Tat ca thay doi dang chu y cua RcloneDrive GUI.

## v1.0.57 - 2026-07-10

- Gan prefix profile va ky tu o vao log rclone mount, vi du `[VPS M:]`, de tranh nham log cua o nay sang o khac.
- Sua hanh vi thu nho/dong app: minimize khong tu an xuong tray; bam `X` se hoi an tray hay thoat app.
- Them canh bao khi FTP/SFTP mount root `/`, khuyen dung thu muc site cu the nhu `/www/wwwroot/ten-domain`.
- Bo qua mot so thu muc he thong de giam loi `permission denied` khi lo mount root FTP/SFTP.
- Build lai `RcloneDrive.exe` ban `v1.0.57` de lam asset release GitHub.

## 2026.06.11.3

- Nhan dien profile `AUTO` dang mount bang source rclone/process, khong chi bang ky tu o luu trong profile.
- Khi mount thanh cong voi `AUTO`, app luu lai ky tu o that de lan sau khong doi project path.
- Giam loi tao them o moi khi profile cu da mount nhung app vua restart.

## 2026.06.11.2

- Sua nut `Mo project` uu tien duong dan ky tu o nhu `X:\public_html` de OpenCode Desktop load dung session.
- Cap nhat huong dan OpenCode: khong mo xen ke `X:\...` va `\\server\...` cho cung project.

## 2026.06.11.1

- Doi `Log rclone` sang RichTextBox de hien thi ro hon.
- To mau log theo muc `ERROR/WARN/INFO/RCLONE/WEB`.
- Gioi han log o 2000 dong moi nhat de app khong nang khi rclone spam loi.
- Nho gon nut log va giu thanh cuon doc khong bi che.

## 2026.06.10.2

- Them `Preset mount`: `Nhanh/RaiDrive` va `Live`.
- Them nut `Lam moi o` de remount profile dang chon.
- Them nut `Mo project`; khi bat `Network mode`, app mo theo UNC `\\server\<ten-o>\...` de OpenCode/Codex giu session on dinh.
- Them version app tren header.
- Chan mo nhieu cua so `RcloneDrive.exe` cung luc bang mutex.
- Them nut log: `Loi`, `Copy`, `Xoa log`.

## 2026.06.10.1

- Bo thanh cuon ngang cua `Log rclone`; log dai tu xuong dong va chi cuon doc.
- Tab `O dia` co cuon doc, hien day du tuy chon VFS/cache/transfers.
- Log rclone chuyen xuong duoi cung de vung form rong hon.

## 2026.06.10

- Sua tieng Viet bi loi ma hoa trong UI/log.
- Cai thien auto update: ep TLS 1.2, check tre sau khi mo app, timeout 30 giay, log ro SHA hien tai/SHA moi.
- Toi uu FTP/SFTP theo kieu RaiDrive: cache metadata lau hon, read-ahead cao hon, transfers/checkers thap hon.
- Them local workspace workflow: `Tai ve may`, `Day len host`, `Mo local`.
- Them tu cai WinFsp khi thieu.
- Them tu tai `rclone.exe` khi thieu.

## 2026.06.09

- Them self-update tu GitHub.
- Them duyet file bang `rclone lsjson`, hien loai file, kich thuoc, ngay sua va path.
- Them cache controls: chon cache, cache tat ca, don cache profile/tat ca.
- Sap xep lai nut UI trong header, sidebar, tab `O dia`, tab `Cong cu`.
- Cho phep cung mot remote tao nhieu profile/o mount.
- Tu tao profile moi khi bam `Ket noi` tren profile dang mount.
- Chuan hoa remote path, loai bo host gia `\\server\...` khi can mount.

## 2026.05

- Ban WinForms dau tien: quan ly profile, mount/ngat, them config, Web UI, BAT mount/ngat, quet o rclone dang mount.
- Them icon o rclone va volname theo profile/config.
- Them preflight `rclone lsf` truoc khi mount de bat loi sai login/path/remote.

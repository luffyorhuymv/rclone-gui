# CHANGELOG

Tat ca thay doi dang chu y cua RcloneDrive GUI.

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

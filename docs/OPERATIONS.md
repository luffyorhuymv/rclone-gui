# OPERATIONS

Tai lieu van hanh RcloneDrive GUI.

## Chay app

Mo:

```text
RcloneDrive.exe
```

App ky vong chay cung thu muc voi `rclone.exe`. Neu thieu, app se hoi va tu tai rclone Windows amd64 tu nguon chinh thuc.

Mount rclone tren Windows can WinFsp. Neu thieu, app co the tu tai/cai WinFsp MSI va yeu cau UAC.

## Profile mount

Moi profile gom:

- Ten profile
- Remote
- Duong dan remote
- Ky tu o dia hoac tu chon o trong
- VFS cache mode
- Thu muc cache
- Transfers, buffer, cache age, write-back
- Preset mount
- Read-only, Auto mount, Network mode
- Extra args

## Preset mount

### Nhanh/RaiDrive

Dung khi mo project bang IDE/agent va can doc cay file nhanh hon.

Hanh vi:

- cache metadata lau hon
- read-ahead cao hon
- FTP ep transfers/checkers thap
- tat poll interval voi FTP/SFTP de giam request

Danh doi: neu file tren host bi sua tu noi khac, app co the thay doi cham hon vai phut.

### Live

Dung khi can thay doi remote hien nhanh hon.

Hanh vi:

- cache metadata ngan hon
- read-ahead nho hon

Danh doi: IDE/agent co the doc cham hon, nhat la tren FTP/shared hosting.

## Network mode va duong dan project

Neu bat `Network mode`, Windows co the co duong dan UNC dang:

```text
\\server\<ten-o>\public_html
```

Nen dung duy nhat mot dang duong dan cho OpenCode/Codex:

- OpenCode Desktop tren Windows thuong on dinh hon voi ky tu o dang `Z:\...`.
- Mot so agent khac co the hien thi/normalize thanh UNC, nhung khong nen mo xen ke hai dang cho cung project.

Khong mo xen ke `Z:\public_html` va `\\server\...\public_html` cho cung mot project vi agent/IDE co the coi la hai workspace khac nhau.

## Workflow code tren host

Khuyen nghi cho FTP/SFTP:

1. Mount dung thu muc can code, khong mount root qua rong neu khong can.
2. Chon preset `Nhanh/RaiDrive`.
3. Dung `Mo project` de mo thu muc con nhu:

```text
public_html/wp-content/themes/<theme>
public_html/wp-content/plugins/<plugin>
```

Neu host yeu hoac hay disconnect, dung workflow local:

1. `Tai ve may`
2. Code trong thu muc local
3. `Day len host`

## Backup/import config

Nut `Dong bo config len` upload:

- `rclone.conf`
- `profiles.json`
- manifest dong bo

Dich can co dang:

```text
remote:/thu-muc-backup
```

Vi du:

```text
api:/RcloneDriveManagerBackup
```

Profile local nam tai:

```text
%APPDATA%\RcloneDriveManager\profiles.json
```

## Web UI rclone

Nut `Web UI` chay:

```text
rclone rcd --rc-web-gui --rc-addr 127.0.0.1:5572 --rc-no-auth
```

Dung de them/sua config bang giao dien web cua rclone.

## Auto update

App tu check update sau khi mo khoang 6 giay. Nut `Cap nhat` trong tab `Cong cu` check thu cong.

Nguon update hien tai:

```text
https://raw.githubusercontent.com/luffyorhuymv/rclone-gui/<commit-sha>/RcloneDrive.exe
```

App so SHA256 file dang chay voi file tai ve. Neu khac, app hoi xac nhan, thay exe va mo lai.

## Startup

Nut `Startup ON` them app vao:

```text
HKCU\Software\Microsoft\Windows\CurrentVersion\Run
```

voi tham so:

```text
--automount
```

## Schedule

App hien co `Auto mount khi mo app` va `Startup ON/OFF`, chua co scheduler rieng trong UI.

Neu can chay theo lich, dung Windows Task Scheduler de mo:

```text
RcloneDrive.exe --automount
```

Khuyen nghi:

- Chi schedule mount profile da test on dinh.
- Khong schedule nhieu job cung mount mot remote FTP yeu.
- Neu can backup config dinh ky, dung Task Scheduler goi file BAT rieng hoac mo app roi bam `Dong bo config len`.

## YouTube

App khong co logic rieng cho YouTube. Neu remote/dataset co lien quan YouTube, hay luu tai lieu van hanh rieng trong repo hoac trong thu muc project duoc mount.

Quy uoc cho agent:

- Khong tu tai/upload file YouTube neu khong co yeu cau ro.
- Khong commit token/API key YouTube vao repo.
- Neu can luu link/tai nguyen YouTube cho project, them vao README cua project do, khong them vao app rclone nay.

## Telegram

App khong co tich hop Telegram bot/API.

Quy uoc cho agent:

- Khong dua bot token/chat id vao source.
- Neu can thong bao Telegram, tao script rieng va doc bien moi truong tu file `.env` ngoai repo.
- Neu muon them tinh nang Telegram vao app, can thiet ke rieng: UI cau hinh token, test gui tin, luu token an toan, va log khong lo secret.

## Log

Log co cac nut:

- `Loi`: copy cac dong `ERROR`, `WARN`, `CRITICAL`.
- `Copy`: copy toan bo log.
- `Xoa log`: xoa log hien tai.

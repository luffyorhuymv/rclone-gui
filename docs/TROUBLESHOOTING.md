# TROUBLESHOOTING

Cac loi thuong gap va cach xu ly.

## Khong thay o sau khi mount

Kiem tra:

- WinFsp da cai chua.
- App co dang chay bang Administrator khong. O tao boi tien trinh Administrator co the khong hien trong Explorer quyen thuong.
- Ky tu o dia da bi Windows dung san chua.
- Log co `Cannot create WinFsp-FUSE file system` hoac `cgofuse: cannot find winfsp` khong.

Cach xu ly:

1. Bam `Cai WinFsp` trong tab `Cong cu`.
2. Mo lai app khong dung Administrator neu Explorer dang chay quyen thuong.
3. Chon `Tu chon o trong`.
4. Bam `Quet o` hoac `Lam moi`.

## OpenCode/Codex khong thay lich su chat

Nguyen nhan thuong gap: mo cung project bang hai duong dan khac nhau.

Vi du:

```text
Z:\public_html
\\server\phukientudiensite Z\public_html
```

OpenCode/Codex co the coi day la hai workspace khac nhau.

Cach xu ly:

- Neu bat `Network mode`, luon mo project bang UNC.
- Dung nut `Mo project` trong app; app se uu tien UNC.
- Khong mo xen ke `Z:\...` va `\\server\...` cho cung project.

## Codex/OpenCode doc file rat cham

Nguyen nhan:

- FTP/shared hosting cham voi nhieu file nho.
- IDE/agent quet `.git`, `node_modules`, `vendor`, WordPress core.
- Mo root qua rong nhu `public_html`.

Cach xu ly:

1. Chon preset `Nhanh/RaiDrive`.
2. Mo thu muc con can code, vi du theme/plugin.
3. Tranh chay `rg --files` tren root FTP lon.
4. Neu van cham, dung workflow local: `Tai ve may` -> code local -> `Day len host`.

## FTP 421 Too many connections

Thong bao:

```text
421 Too many connections from this IP
```

Nguyen nhan: host gioi han so ket noi FTP.

Cach xu ly:

- Ngat cac o/phan mem FTP cu.
- Doi vai phut.
- Dung preset `Nhanh/RaiDrive`.
- Giu transfers/checkers thap.
- Tranh mo nhieu IDE/agent cung luc tren cung remote.

## FTP 530 Login authentication failed

Thong bao:

```text
530 Login authentication failed
```

Nguyen nhan: sai user/pass hoac tai khoan FTP khong du quyen.

Cach xu ly:

1. Mo `Them config` hoac `Web UI`.
2. Kiem tra lai host, user, password, port.
3. Bam `Kiem tra ket noi` truoc khi luu/mount.

## Directory not found

Nguyen nhan: path remote sai.

Dung dang rclone:

```text
public_html
/public_html
```

Khong nhap UNC vao remote path:

```text
\\server\share
```

Voi SFTP, can phan biet:

- `remote:public_html`: duong dan tu home user.
- `remote:/public_html`: duong dan tu root server.

## File `.ftpquota` gay loi

Mot so host cam doc `.ftpquota`, co the log:

```text
553 "Prohibited file name: /public_html/.ftpquota"
```

App da tu exclude:

```text
.ftpquota
**/.ftpquota
```

Neu van gap loi, them vao `Tham so rclone them`:

```text
--exclude .ftpquota --exclude **/.ftpquota
```

## Update khong chay

Kiem tra:

- May co vao duoc GitHub khong.
- Log co `Tu kiem tra cap nhat app...` khong.
- Nut `Cap nhat` trong tab `Cong cu` co bao loi khong.

App update tu:

```text
https://api.github.com/repos/luffyorhuymv/rclone-gui/commits/main
https://raw.githubusercontent.com/luffyorhuymv/rclone-gui/<sha>/RcloneDrive.exe
```

Neu exe dang bi khoa, dong app roi chay lai. App co mutex nen chi nen co mot cua so `RcloneDrive.exe`.

## Build bi loi file exe dang bi dung

Loi:

```text
Cannot open RcloneDrive.exe for writing
```

Cach xu ly:

1. Dong process `RcloneDrive.exe`.
2. Khong dong `rclone.exe` neu no dang mount o.
3. Build lai.

Lenh kiem tra:

```powershell
Get-Process RcloneDrive -ErrorAction SilentlyContinue
Get-Process rclone -ErrorAction SilentlyContinue
```

## Nut bi che hoac UI tran

Cach xu ly nguoi dung:

- Keo cua so lon hon.
- Dung scroll doc trong tab `O dia` hoac `Cong cu`.

Neu sua code:

- Tranh row height co dinh qua thap.
- Uu tien `AutoScroll = true`.
- Giu nut ngan va co wrap.

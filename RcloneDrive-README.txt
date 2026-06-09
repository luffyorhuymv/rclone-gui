RcloneDrive.exe
================

Ung dung Windows WinForms dung rclone.exe lam backend, theo workflow giong RaiDrive:

- UI moi: tieng Viet co dau, header action bar, sidebar o da cau hinh, nut co khung vien ro, log panel nen toi, vung giua da sua layout tranh tran/che control
- Layout moi: cot danh sach rong hon, cot log gon hon, nut tren header va sidebar da chinh kich thuoc de tranh che chu/tran hang
- Quan ly drive profiles
- Nut nhanh tren danh sach profile: Ket noi, Ngat, Cai dat o
- Load remote tu rclone listremotes
- Mount remote thanh o dia Windows
- Unmount, open drive, tao file mount .bat
- Tao file unmount .bat bang nut NgatBAT va co san file unmount-rclone-drive.bat de ngat theo ky tu o
- Auto mount khi mo app neu profile bat AutoMount
- Bat/tat khoi dong cung Windows bang nut Startup ON/OFF
- Browser remote bang rclone lsf
- Tao folder remote bang rclone mkdir
- Xoa file remote bang rclone deletefile
- Transfer copy/sync/move/check voi dry-run
- Tools: about, size, cleanup, version, rclone config
- Log lenh va output rclone o panel ben phai
- Add Config: tao remote moi ngay tren UI bang rclone config create
- Check connection truoc khi add config: kiem tra rclone.exe, ten remote, remote trung, type va ping internet/endpoint
- Dong bo config len remote: upload rclone.conf, profiles.json va sync-manifest.txt len thu muc remote do ban nhap
- Chay UI web: mo Rclone Web GUI tai http://127.0.0.1:5572 de them/sua config bang giao dien web
- Mount config chua ket noi: tu dong mount cac profile da cau hinh nhung dang o trang thai chua ket noi
- Ky tu o dia co tuy chon Tu chon o trong: app tu tim ky tu chua dung tu Z ve C khi mount
- Mount them mac dinh --links de tranh loi FTP/symlink khien process chay nhung o dia khong hien
- Co cau hinh VFS cho viec code: Giu cache toi da mac dinh 72h va Upload sau khi sua mac dinh 5s
- Co nut Cache o sidebar de Browse chon thu muc cache va ap dung cho tat ca profile; trong tab o dia co nut Chon cache cho profile dang chon
- Sau khi mount app doi Windows nhan o dia; neu chua thay se hien canh bao trong log
- Volname khi mount duoc them hau to ky tu o dia de tranh trung WinFsp share/service, vi du "Drive api U"
- Ten o mount/volname uu tien theo ten config remote, vi du config new1: se hien new1 U thay vi ten profile
- Khi mount thanh cong app tu refresh Explorer va mo truc tiep o dia vua mount, vi This PC doi luc cap nhat cham
- App chi quet cac o dang mount san tu rclone/WinFsp, khong hien o RaiDrive/CBFS
- Neu o rclone da mount truoc khi mo app va trung ky tu voi profile, profile se hien trang thai Ket noi thay vi bi an khoi danh sach quet
- Co nut Quet o de quet lai ngay cac o rclone/WinFsp dang mount san; nut Lam moi cung quet o truoc khi load lai remote
- Lenh Ngat moi se tim va kill dung process rclone mount theo ky tu o/share name, sau do chay net use /delete va kiem tra o da bien mat
- Co the Mo o dia hoac Ngat cac o dang bat san tu danh sach
- Ban on dinh moi: truoc khi mount app chay `rclone lsf remote:/ --max-depth 1` de bat loi sai user/pass, remote hong hoac mat ket noi truoc khi tao o dia
- Neu ky tu o dia da bi Windows dung san, app se bao loi ngay va khong goi rclone mount de tranh loi WinFsp Status=80070050
- Khi app chay bang Administrator, log se canh bao vi o mount co the khong hien trong Explorer chay quyen thuong

Cach chay:
1. Dat RcloneDrive.exe cung thu muc voi rclone.exe.
2. Double-click RcloneDrive.exe.
3. Bam Refresh de load remote.
4. Tao/sua profile, chon remote, path, drive letter.
5. Bam Mount.

Nut nhanh:
- Ket noi: mount profile dang chon.
- Ngat: unmount profile dang chon.
- Cai dat o: mo hop thoai chinh remote, path, ky tu o dia, VFS cache, cache dir, read-only, auto-mount, network mode, transfers, buffer va extra args.

Cau hinh tot cho code:
- Che do VFS cache: full.
- Giu cache toi da: 72h. Gia tri nay giu file da mo/build lai trong cache du lau de IDE khong doc lai tu remote lien tuc.
- Upload sau khi sua: 5s. Gia tri nay dong bo nhanh nhung van tranh upload qua nhieu lan khi IDE ghi file tam/lien tuc.
- Neu can dong bo gan nhu ngay lap tuc, co the giam Upload sau khi sua xuong 2s. Neu remote yeu/cham, tang len 10s.

Dong bo config len:
1. Mo tab Cong cu.
2. Bam Dong bo config len.
3. Nhap dich dang remote:/thu-muc, vi du api:/RcloneDriveManagerBackup.
4. App se upload rclone.conf, profiles.json va sync-manifest.txt.

Chay UI web:
1. Bam UI web tren thanh tren hoac Chay UI web trong tab Cong cu.
2. App chay `rclone rcd --rc-web-gui --rc-no-auth` tai 127.0.0.1:5572.
3. Trinh duyet se mo Rclone Web GUI de ban them/sua config.

Mount config chua ket noi:
1. Mo tab Cong cu.
2. Bam Mount config chua ket noi.
3. App se mount tat ca profile co remote va ky tu o dia nhung chua ket noi.

Them config tren UI:
1. Mo tab Add Config.
2. Nhap Remote name, chon Storage type.
3. Neu remote can dang nhap, dien User va Password nhu Web UI.
4. Password mac dinh se duoc ma hoa bang `rclone obscure` truoc khi ghi config va khong hien plaintext trong log.
5. Dien tham so nang cao dang key=value, moi dong mot tham so neu can.
6. Bam Check connection.
7. Khi bao Connection OK, bam Add config.

Vi du SFTP:
host=1.2.3.4
user=root
pass=<mat-khau-da-rclone-obscure>

Vi du S3:
provider=AWS
access_key_id=...
secret_access_key=...
region=...

Luu y:
- Mount rclone tren Windows can WinFsp da cai san.
- Theo tai lieu rclone mount tren Windows, hay dung ky tu o dia chua su dung va giu `--volname` khong trung khi mount nhieu o network-mode.
- Neu o da mount nhung khong thay trong This PC, kiem tra app co dang chay bang Administrator khong. O tao boi tien trinh Administrator co the khong hien trong Explorer quyen thuong.
- Google Drive/OneDrive can OAuth nen nut Open wizard van la cach on dinh nhat neu chua co token/client params.
- Profile duoc luu tai:
  %APPDATA%\RcloneDriveManager\profiles.json
- Nut Startup ON them app vao HKCU Run voi tham so --automount.

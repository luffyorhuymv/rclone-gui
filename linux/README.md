# RcloneDrive Linux

Ban Linux dung Bash, khong dung WinForms/WinFsp. Linux mount rclone vao thu muc thay vi ky tu o nhu `X:` hoac `Y:`.

## Cai dat

```bash
chmod +x ./linux/rclone-drive-linux.sh
./linux/rclone-drive-linux.sh install
```

Lenh `install` se kiem tra va tu cai:

- `git`
- FUSE
- `rclone`

## Mount remote

```bash
./linux/rclone-drive-linux.sh mount phukientudien:/ ~/mnt/phukientudien
```

## Mo OpenCode

Khong mo truc tiep thu muc mount root. Hay mo thu muc project cu the:

```bash
./linux/rclone-drive-linux.sh opencode ~/mnt/phukientudien public_html
```

Neu project chua co Git, script se tu chay:

```bash
git init -b main
git config user.name "RcloneDrive"
git config user.email "rclonedrive@local"
git commit --allow-empty -m "Initialize repository"
```

## Unmount

```bash
./linux/rclone-drive-linux.sh unmount ~/mnt/phukientudien
```

## Web UI

```bash
./linux/rclone-drive-linux.sh webui
```

Mac dinh Web UI chay tai:

```text
http://127.0.0.1:5572
user: admin
pass: admin
```

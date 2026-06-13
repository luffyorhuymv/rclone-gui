# RcloneDrive GUI

Ứng dụng Windows WinForms để quản lý rclone remotes, mount ổ đĩa, duyệt file, thêm config và tối ưu workflow code bằng IDE.

## Tính năng chính

- Quản lý profile mount rclone.
- Kết nối/ngắt ổ rclone bằng WinFsp.
- Tự chọn ký tự ổ trống.
- Quét ổ rclone đang mount sẵn.
- Thêm config trên UI hoặc mở rclone Web GUI.
- Preset `Code IDE` với VFS cache `full`, upload sau khi sửa `5s`, giữ cache `72h`.
- Đặt icon riêng cho ổ rclone trong Explorer.
- Tạo file BAT mount/ngắt.

## Chạy app

Mở `RcloneDrive.exe`.

Nếu chưa có `rclone.exe` cạnh app, chương trình sẽ hỏi và tự tải `rclone-current-windows-amd64.zip` từ trang chính thức của rclone, giải nén rồi đặt `rclone.exe` cạnh app.

`rclone.exe` không được đưa vào repo này để repo gọn hơn.

## Bản Linux

Bản Linux nằm trong:

```text
linux/rclone-drive-linux.sh
```

Chạy trên Linux:

```bash
chmod +x ./linux/rclone-drive-linux.sh
./linux/rclone-drive-linux.sh install
./linux/rclone-drive-linux.sh mount phukientudien:/ ~/mnt/phukientudien
./linux/rclone-drive-linux.sh opencode ~/mnt/phukientudien public_html
```

Linux không dùng ký tự ổ như `X:`/`Y:`. Script mount vào thư mục, tự kiểm tra/cài `git`, FUSE, `rclone`, và tự `git init` project nếu OpenCode cần.

## Cloudflare Access TCP tunnel

Với SFTP/SSH nằm sau Cloudflare Access, bật `Cloudflare tunnel` trong profile rồi nhập:

```text
CF Access hostname: lapp.apphay.io.vn
Tunnel local port: 2221
```

App sẽ tự chạy:

```text
cloudflared access tcp --hostname lapp.apphay.io.vn --url localhost:2221
```

Sau đó app tự chỉnh remote rclone dùng `host=localhost` và `port=2221` trước khi test/mount.

## Source

Source chính nằm tại:

```text
RcloneDriveManager/Program.cs
```

## Tài liệu cho agent/IDE

- `AGENTS.md`: quy tắc làm việc cho AI/IDE agent khi đọc hoặc sửa repo.
- `CHANGELOG.md`: lịch sử tính năng/fix theo phiên bản.
- `docs/OPERATIONS.md`: cách vận hành app, mount, update, backup/import, workflow code.
- `docs/TROUBLESHOOTING.md`: lỗi thường gặp và cách xử lý.

Build bằng Roslyn C# compiler trên Windows:

```powershell
& 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe' /codepage:65001 /target:winexe /platform:x64 /win32icon:'.\RcloneDriveManager\RcloneDrive.ico' /out:'.\RcloneDrive.exe' /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll '.\RcloneDriveManager\Program.cs'
```

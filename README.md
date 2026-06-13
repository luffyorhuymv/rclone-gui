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

Với SFTP/SSH nằm sau Cloudflare Access, hãy để rclone remote giữ `host` là hostname thật lúc cấu hình, ví dụ:

```text
host = lapp.apphay.io.vn
```

Bật `Mount Cloudflare tunnel` trong profile. `Tunnel local port` để `0` thì app tự chọn port trống từ `2221` trở lên.

App tự lấy hostname từ rclone config và chạy:

```text
cloudflared access tcp --hostname <host trong rclone config> --url localhost:<port auto>
```

Sau đó app tự chỉnh remote rclone dùng `host=localhost` và `port=<port auto>` trước khi test/mount. Host gốc được lưu trong profile để lần sau vẫn tự bật tunnel đúng hostname.

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

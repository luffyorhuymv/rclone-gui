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

Đặt `RcloneDrive.exe` cùng thư mục với `rclone.exe`, sau đó mở `RcloneDrive.exe`.

`rclone.exe` không được đưa vào repo này. Tải rclone tại trang chính thức rồi đặt cạnh app.

## Source

Source chính nằm tại:

```text
RcloneDriveManager/Program.cs
```

Build bằng Roslyn C# compiler trên Windows:

```powershell
& 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe' /codepage:65001 /target:winexe /platform:x64 /win32icon:'.\RcloneDriveManager\RcloneDrive.ico' /out:'.\RcloneDrive.exe' /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll '.\RcloneDriveManager\Program.cs'
```

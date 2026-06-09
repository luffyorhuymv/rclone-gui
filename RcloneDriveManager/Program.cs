using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace RcloneDriveManager
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(args));
        }
    }

    public sealed class DriveProfile
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Remote { get; set; }
        public string RemotePath { get; set; }
        public string DriveLetter { get; set; }
        public string CacheMode { get; set; }
        public string CacheDir { get; set; }
        public string VfsCacheMaxAge { get; set; }
        public string VfsWriteBack { get; set; }
        public bool ReadOnly { get; set; }
        public bool AutoMount { get; set; }
        public bool NetworkMode { get; set; }
        public int Transfers { get; set; }
        public int BufferSizeMb { get; set; }
        public string ExtraArgs { get; set; }

        public DriveProfile()
        {
            Id = Guid.NewGuid().ToString("N");
            Name = "New Drive";
            Remote = "";
            RemotePath = "/";
            DriveLetter = "Z:";
            CacheMode = "full";
            CacheDir = "%USERPROFILE%\\.cache\\rclone";
            VfsCacheMaxAge = "72h";
            VfsWriteBack = "5s";
            ReadOnly = false;
            AutoMount = false;
            NetworkMode = true;
            Transfers = 4;
            BufferSizeMb = 32;
            ExtraArgs = "";
        }

        public string Source
        {
            get
            {
                return BuildSource(Remote, RemotePath);
            }
        }

        public static string BuildSource(string remote, string remotePath)
        {
            return (remote ?? "").Trim() + NormalizeRemotePath(remotePath, remote);
        }

        public static string NormalizeRemotePath(string value, string remote)
        {
            var path = string.IsNullOrWhiteSpace(value) ? "/" : value.Trim();
            path = path.Replace('\\', '/');

            var remoteName = (remote ?? "").Trim().TrimEnd(':');
            if (remoteName.Length > 0 && path.StartsWith(remoteName + ":", StringComparison.OrdinalIgnoreCase))
                path = path.Substring(remoteName.Length + 1).Trim();

            if (Regex.IsMatch(path, @"^[A-Za-z]:/"))
                path = path.Substring(2);

            while (path.StartsWith("//", StringComparison.Ordinal))
                path = path.Substring(1);

            while (path.Contains("//"))
                path = path.Replace("//", "/");

            if (path.Length == 0) path = "/";
            if (!path.StartsWith("/", StringComparison.Ordinal)) path = "/" + path;
            return path;
        }
    }

    public sealed class MountedDriveInfo
    {
        public string Name { get; set; }
        public string DriveLetter { get; set; }
        public string DisplayRoot { get; set; }
        public string Provider { get; set; }
    }

    public sealed class RcloneResult
    {
        public int ExitCode { get; set; }
        public string Output { get; set; }
        public bool TimedOut { get; set; }
    }

    public sealed class RcloneMountProcessInfo
    {
        public int ProcessId { get; set; }
        public string CommandLine { get; set; }
        public string DriveLetter { get; set; }
        public string Source { get; set; }
        public string VolumeName { get; set; }
    }

    public sealed class MainForm : Form
    {
        private readonly string[] _args;
        private readonly string _appDir;
        private readonly string _rcloneExe;
        private readonly string _dataDir;
        private readonly string _profilesFile;
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer();
        private readonly List<DriveProfile> _profiles = new List<DriveProfile>();
        private readonly Dictionary<string, Process> _mounts = new Dictionary<string, Process>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<DriveProfile, string> _activeDrives = new Dictionary<DriveProfile, string>();
        private readonly List<string> _remotes = new List<string>();
        private readonly List<MountedDriveInfo> _mountedExternalDrives = new List<MountedDriveInfo>();
        private readonly Dictionary<string, MountedDriveInfo> _detectedRcloneDrives = new Dictionary<string, MountedDriveInfo>(StringComparer.OrdinalIgnoreCase);
        private Process _webUiProcess;
        private readonly Color _bg = Color.FromArgb(241, 245, 249);
        private readonly Color _surface = Color.White;
        private readonly Color _line = Color.FromArgb(226, 232, 240);
        private readonly Color _text = Color.FromArgb(17, 24, 39);
        private readonly Color _muted = Color.FromArgb(100, 116, 139);
        private readonly Color _primary = Color.FromArgb(29, 78, 216);
        private readonly Color _danger = Color.FromArgb(185, 28, 28);
        private readonly Color _success = Color.FromArgb(22, 163, 74);

        private ListView profileList;
        private TabControl mainTabs;
        private ComboBox remoteCombo;
        private ComboBox driveCombo;
        private ComboBox cacheModeCombo;
        private TextBox nameBox;
        private TextBox pathBox;
        private TextBox cacheDirBox;
        private TextBox cacheMaxAgeBox;
        private TextBox writeBackBox;
        private TextBox extraArgsBox;
        private NumericUpDown transfersBox;
        private NumericUpDown bufferBox;
        private CheckBox readOnlyBox;
        private CheckBox autoMountBox;
        private CheckBox networkModeBox;
        private TextBox logBox;
        private ComboBox browseRemoteCombo;
        private TextBox browsePathBox;
        private ListView browserList;
        private ComboBox transferModeCombo;
        private ComboBox transferSourceRemoteCombo;
        private ComboBox transferDestRemoteCombo;
        private TextBox transferSourcePathBox;
        private TextBox transferDestPathBox;
        private CheckBox dryRunBox;
        private Label statusLabel;
        private TextBox configNameBox;
        private ComboBox configTypeCombo;
        private TextBox configParamsBox;
        private TextBox configTestPathBox;
        private TextBox configUserBox;
        private TextBox configPassBox;
        private CheckBox configObscurePassBox;
        private CheckBox configRequireInternetBox;
        private Label configCheckLabel;
        private bool _loadingProfileFields;
        private bool _profileNameEditedByUser;
        private bool _changingProfileNameAutomatically;

        public MainForm(string[] args)
        {
            _args = args ?? new string[0];
            _appDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            _rcloneExe = Path.Combine(_appDir, "rclone.exe");
            _dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RcloneDriveManager");
            _profilesFile = Path.Combine(_dataDir, "profiles.json");

            Text = "Trình quản lý ổ Rclone";
            Width = 1420;
            Height = 840;
            MinimumSize = new Size(1280, 760);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9F);
            var iconPath = Path.Combine(_appDir, "RcloneDriveManager", "RcloneDrive.ico");
            if (File.Exists(iconPath))
                Icon = new Icon(iconPath);

            BuildUi();
            Load += async (s, e) =>
            {
                LoadProfiles();
                RefreshDriveLetters();
                await EnsureRcloneAvailableAsync();
                RunStartupDiagnostics();
                if (File.Exists(_rcloneExe))
                    await RefreshRemotesAsync();
                SelectFirstProfile();
                if (_args.Any(a => string.Equals(a, "--automount", StringComparison.OrdinalIgnoreCase)))
                    await MountAutoProfilesAsync();
            };
            FormClosing += (s, e) => SaveProfiles();
        }

        private void BuildUi()
        {
            BackColor = _bg;
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2, BackColor = _bg };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 348));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 380));
            Controls.Add(root);

            var header = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = _surface, Padding = new Padding(18, 12, 18, 10), ColumnCount = 2, RowCount = 1 };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 760));
            root.Controls.Add(header, 0, 0);
            root.SetColumnSpan(header, 3);

            var titleBlock = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, BackColor = _surface };
            titleBlock.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            titleBlock.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            titleBlock.Controls.Add(new Label { Text = "Trình quản lý ổ Rclone", Dock = DockStyle.Fill, Font = new Font("Segoe UI", 15F, FontStyle.Bold), ForeColor = _text, TextAlign = ContentAlignment.BottomLeft }, 0, 0);
            titleBlock.Controls.Add(new Label { Text = "Kết nối, mount, duyệt file và quản lý config rclone", Dock = DockStyle.Fill, Font = new Font("Segoe UI", 9F), ForeColor = _muted, TextAlign = ContentAlignment.TopLeft }, 0, 1);
            header.Controls.Add(titleBlock, 0, 0);

            var headerActions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, BackColor = _surface, Padding = new Padding(0, 8, 0, 0) };
            headerActions.Controls.Add(ActionButton("Thêm config", (s, e) => SelectTab("Thêm config"), _surface, _text, 120));
            headerActions.Controls.Add(ActionButton("Quét ổ", (s, e) => RefreshMountedDriveList(), _surface, _text, 88));
            headerActions.Controls.Add(ActionButton("Làm mới", async (s, e) => await RefreshAllAsync(), _surface, _text, 104));
            headerActions.Controls.Add(ActionButton("Cài đặt ổ", (s, e) => OpenDriveSettingsDialog(), _surface, _text, 112));
            headerActions.Controls.Add(ActionButton("Ngắt", (s, e) => UnmountSelected(), _surface, _danger, 86));
            headerActions.Controls.Add(ActionButton("Kết nối", async (s, e) => await MountSelectedAsync(), _primary, Color.White, 112));
            header.Controls.Add(headerActions, 1, 0);

            var left = new Panel { Dock = DockStyle.Fill, BackColor = _surface, Padding = new Padding(18, 18, 14, 18) };
            root.Controls.Add(left, 0, 1);

            var leftLayout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1 };
            leftLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
            leftLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            leftLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 132));
            left.Controls.Add(leftLayout);
            var driveListTitle = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, BackColor = _surface };
            driveListTitle.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            driveListTitle.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
            driveListTitle.Controls.Add(new Label { Text = "Ổ đã cấu hình", Font = new Font("Segoe UI", 13F, FontStyle.Bold), ForeColor = _text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.BottomLeft }, 0, 0);
            driveListTitle.Controls.Add(new Label { Text = "Profile và ổ rclone đang mount", Font = new Font("Segoe UI", 8.5F), ForeColor = _muted, Dock = DockStyle.Fill, TextAlign = ContentAlignment.TopLeft }, 0, 1);
            leftLayout.Controls.Add(driveListTitle, 0, 0);

            profileList = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, BorderStyle = BorderStyle.FixedSingle, BackColor = Color.FromArgb(248, 250, 252), ForeColor = _text, Font = new Font("Segoe UI", 9F) };
            profileList.Columns.Add("Tên ổ", 142);
            profileList.Columns.Add("Ổ", 52);
            profileList.Columns.Add("Trạng thái", 82);
            profileList.Columns.Add("Nguồn", 116);
            profileList.SelectedIndexChanged += (s, e) => LoadSelectedProfileIntoFields();
            leftLayout.Controls.Add(profileList, 0, 1);

            var leftButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, Padding = new Padding(0, 4, 0, 0) };
            leftButtons.Controls.Add(ActionButton("Mới", (s, e) => NewProfile(), _surface, _text, 66));
            leftButtons.Controls.Add(ActionButton("Lưu", (s, e) => SaveCurrentProfile(), _primary, Color.White, 66));
            leftButtons.Controls.Add(ActionButton("Xóa", (s, e) => DeleteCurrentProfile(), _surface, _danger, 66));
            leftButtons.Controls.Add(ActionButton("Cài đặt", (s, e) => OpenDriveSettingsDialog(), _surface, _text, 92));
            statusLabel = new Label { Text = "Sẵn sàng", AutoSize = false, Width = 310, Height = 28, ForeColor = _muted, TextAlign = ContentAlignment.MiddleLeft };
            leftButtons.Controls.Add(statusLabel);
            leftLayout.Controls.Add(leftButtons, 0, 2);

            mainTabs = new TabControl { Dock = DockStyle.Fill, Font = new Font("Segoe UI", 10F), Padding = new Point(20, 8) };
            root.Controls.Add(mainTabs, 1, 1);
            mainTabs.TabPages.Add(BuildDriveTab());
            mainTabs.TabPages.Add(BuildBrowserTab());
            mainTabs.TabPages.Add(BuildTransferTab());
            mainTabs.TabPages.Add(BuildAddConfigTab());
            mainTabs.TabPages.Add(BuildToolsTab());

            var logPanel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, BackColor = Color.FromArgb(15, 23, 42), Padding = new Padding(12) };
            logPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            logPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            logPanel.Controls.Add(new Label { Text = "Log rclone", Dock = DockStyle.Fill, ForeColor = Color.FromArgb(226, 232, 240), Font = new Font("Segoe UI", 10F, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
            logBox = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                BackColor = Color.FromArgb(15, 23, 42),
                ForeColor = Color.FromArgb(134, 239, 172),
                Font = new Font("Consolas", 9.5F),
                BorderStyle = BorderStyle.None,
                WordWrap = false
            };
            logPanel.Controls.Add(logBox, 0, 1);
            root.Controls.Add(logPanel, 2, 1);
        }

        private TabPage BuildDriveTab()
        {
            var page = new TabPage("Ổ đĩa") { BackColor = _surface, Padding = new Padding(18) };
            var pageLayout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, BackColor = _surface };
            pageLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
            pageLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            pageLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            page.Controls.Add(pageLayout);

            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, Height = 58, Padding = new Padding(0, 4, 0, 8), BackColor = _surface, WrapContents = false };
            actions.Controls.Add(ActionButton("Kết nối", async (s, e) => await MountSelectedAsync(), _primary, Color.White, 124));
            actions.Controls.Add(ActionButton("Ngắt", (s, e) => UnmountSelected(), _surface, _danger, 96));
            actions.Controls.Add(ActionButton("Mở ổ", (s, e) => OpenSelectedDrive(), _surface, _text, 96));
            actions.Controls.Add(ActionButton("Lưu", (s, e) => SaveCurrentProfile(), _surface, _text, 86));
            actions.Controls.Add(ActionButton("Cache", (s, e) => BrowseCacheDirForSelectedProfile(), _surface, _text, 96));
            actions.Controls.Add(ActionButton("Code IDE", (s, e) => ApplyCodeIdePreset(), _surface, _text, 104));
            pageLayout.Controls.Add(actions, 0, 0);

            var checks = new FlowLayoutPanel { Dock = DockStyle.Fill, Height = 46, Padding = new Padding(0, 6, 0, 4), BackColor = _surface };
            readOnlyBox = new CheckBox { Text = "Chỉ đọc", Width = 100 };
            autoMountBox = new CheckBox { Text = "Tự mount khi mở app", Width = 190 };
            networkModeBox = new CheckBox { Text = "Network mode", Width = 140, Checked = true };
            checks.Controls.Add(readOnlyBox);
            checks.Controls.Add(autoMountBox);
            checks.Controls.Add(networkModeBox);
            pageLayout.Controls.Add(checks, 0, 1);

            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 6, CellBorderStyle = TableLayoutPanelCellBorderStyle.None, BackColor = _surface };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));
            panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            pageLayout.Controls.Add(panel, 0, 2);

            nameBox = AddText(panel, "Tên profile", "Ổ mới", 0, 0);
            nameBox.TextChanged += (s, e) =>
            {
                if (!_loadingProfileFields && !_changingProfileNameAutomatically)
                    _profileNameEditedByUser = true;
            };
            remoteCombo = AddCombo(panel, "Remote", 1, 0);
            remoteCombo.SelectedIndexChanged += (s, e) => AutoNameFromSelectedRemote();
            pathBox = AddText(panel, "Đường dẫn remote", "/", 0, 1);
            driveCombo = AddCombo(panel, "Ký tự ổ đĩa", 1, 1);
            cacheModeCombo = AddCombo(panel, "Chế độ VFS cache", 0, 2);
            cacheModeCombo.Items.AddRange(new object[] { "off", "minimal", "writes", "full" });
            cacheModeCombo.SelectedItem = "full";
            cacheDirBox = AddText(panel, "Thư mục cache", "%USERPROFILE%\\.cache\\rclone", 1, 2);
            transfersBox = AddNumber(panel, "Transfers", 4, 1, 64, 0, 3);
            bufferBox = AddNumber(panel, "Bộ đệm MB", 32, 1, 1024, 1, 3);
            cacheMaxAgeBox = AddText(panel, "Giữ cache tối đa", "72h", 0, 4);
            writeBackBox = AddText(panel, "Upload sau khi sửa", "5s", 1, 4);
            extraArgsBox = new TextBox { Text = "", Height = 54, Multiline = true, ScrollBars = ScrollBars.Vertical };
            panel.Controls.Add(Wrap("Tham số rclone thêm", extraArgsBox), 0, 5);
            panel.SetColumnSpan(extraArgsBox.Parent, 2);

            return page;
        }

        private TabPage BuildBrowserTab()
        {
            var page = new TabPage("Duyệt file") { BackColor = _surface, Padding = new Padding(22) };
            var pageLayout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, BackColor = _surface };
            pageLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 88));
            pageLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            page.Controls.Add(pageLayout);

            var top = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 7, RowCount = 1, BackColor = _surface, Padding = new Padding(0, 10, 0, 10) };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 62));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 46));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 126));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 86));
            pageLayout.Controls.Add(top, 0, 0);

            browseRemoteCombo = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(0, 8, 12, 8) };
            browsePathBox = new TextBox { Dock = DockStyle.Fill, Text = "/", Margin = new Padding(0, 8, 12, 8) };
            top.Controls.Add(new Label { Text = "Remote", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
            top.Controls.Add(browseRemoteCombo, 1, 0);
            top.Controls.Add(new Label { Text = "Path", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 2, 0);
            top.Controls.Add(browsePathBox, 3, 0);
            top.Controls.Add(ActionButton("Liệt kê", async (s, e) => await BrowseListAsync(), _primary, Color.White, 92), 4, 0);
            top.Controls.Add(ActionButton("Tạo thư mục", async (s, e) => await BrowseMkdirAsync(), _surface, _text, 116), 5, 0);
            top.Controls.Add(ActionButton("Xóa", async (s, e) => await BrowseDeleteAsync(), _surface, _danger, 78), 6, 0);

            browserList = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, Font = new Font("Consolas", 9.5F), BorderStyle = BorderStyle.FixedSingle, BackColor = Color.FromArgb(248, 250, 252) };
            browserList.Columns.Add("Tên", 420);
            pageLayout.Controls.Add(browserList, 0, 1);
            return page;
        }

        private TabPage BuildTransferTab()
        {
            var page = new TabPage("Truyền dữ liệu") { BackColor = _surface, Padding = new Padding(22) };
            var pageLayout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, BackColor = _surface };
            pageLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 270));
            pageLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            page.Controls.Add(pageLayout);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, BackColor = _surface };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            pageLayout.Controls.Add(layout, 0, 0);

            transferModeCombo = AddCombo(layout, "Chế độ", 0, 0);
            transferModeCombo.Items.AddRange(new object[] { "copy", "sync", "move", "check" });
            transferModeCombo.SelectedIndex = 0;
            dryRunBox = new CheckBox { Text = "Chạy thử trước", Checked = true, Dock = DockStyle.Fill };
            layout.Controls.Add(Wrap("An toàn", dryRunBox), 1, 0);
            transferSourceRemoteCombo = AddCombo(layout, "Remote nguồn", 0, 1);
            transferSourcePathBox = AddText(layout, "Đường dẫn nguồn", "/", 1, 1);
            transferDestRemoteCombo = AddCombo(layout, "Remote đích", 0, 2);
            transferDestPathBox = AddText(layout, "Đường dẫn đích", "/", 1, 2);

            var actions = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 60, Padding = new Padding(0, 12, 0, 0), BackColor = _surface };
            actions.Controls.Add(ActionButton("Chạy truyền", async (s, e) => await RunTransferAsync(), _primary, Color.White, 132));
            pageLayout.Controls.Add(actions, 0, 1);
            return page;
        }

        private TabPage BuildToolsTab()
        {
            var page = new TabPage("Công cụ") { BackColor = _surface, Padding = new Padding(22) };
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 5, ColumnCount = 1, BackColor = _surface };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 86));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 86));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 86));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            page.Controls.Add(layout);

            layout.Controls.Add(new Label { Text = "Công cụ", Dock = DockStyle.Fill, Font = new Font("Segoe UI", 13F, FontStyle.Bold), ForeColor = _text, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);

            var remoteActions = ToolGroup("Remote");
            remoteActions.Controls.Add(ActionButton("Thông tin", async (s, e) => await RunSimpleForSelectedAsync("about"), _surface, _text, 104));
            remoteActions.Controls.Add(ActionButton("Dung lượng", async (s, e) => await RunSimpleForSelectedAsync("size"), _surface, _text, 116));
            remoteActions.Controls.Add(ActionButton("Cleanup", async (s, e) => await RunSimpleForSelectedAsync("cleanup"), _surface, _text, 104));
            remoteActions.Controls.Add(ActionButton("Phiên bản", async (s, e) => await RunCaptureAsync("version"), _surface, _text, 104));
            layout.Controls.Add(remoteActions, 0, 1);

            var configActions = ToolGroup("Config");
            configActions.Controls.Add(ActionButton("Thêm config", (s, e) => SelectTab("Thêm config"), _surface, _text, 124));
            configActions.Controls.Add(ActionButton("Mở wizard", (s, e) => OpenConfig(), _surface, _text, 112));
            configActions.Controls.Add(ActionButton("Đồng bộ config lên", async (s, e) => await SyncConfigUpAsync(), _primary, Color.White, 168));
            configActions.Controls.Add(ActionButton("UI web", (s, e) => StartWebUi(), _surface, _text, 92));
            layout.Controls.Add(configActions, 0, 2);

            var mountActions = ToolGroup("Mount / BAT");
            mountActions.Controls.Add(ActionButton("Mount chưa kết nối", async (s, e) => await MountDisconnectedProfilesAsync(), _surface, _text, 166));
            mountActions.Controls.Add(ActionButton("Tạo BAT mount", (s, e) => CreateBatForSelected(), _surface, _text, 132));
            mountActions.Controls.Add(ActionButton("Tạo BAT ngắt", (s, e) => CreateUnmountBatForSelected(), _surface, _danger, 126));
            mountActions.Controls.Add(ActionButton("Cache tất cả", (s, e) => BrowseCacheDirForAllProfiles(), _surface, _text, 116));
            layout.Controls.Add(mountActions, 0, 3);

            var systemActions = ToolGroup("Hệ thống");
            systemActions.Controls.Add(ActionButton("Quét ổ", (s, e) => RefreshMountedDriveList(), _surface, _text, 88));
            systemActions.Controls.Add(ActionButton("Làm mới", async (s, e) => await RefreshAllAsync(), _surface, _text, 104));
            systemActions.Controls.Add(ActionButton("Startup ON", (s, e) => SetStartup(true), _surface, _text, 112));
            systemActions.Controls.Add(ActionButton("Startup OFF", (s, e) => SetStartup(false), _surface, _danger, 116));
            layout.Controls.Add(systemActions, 0, 4);
            return page;
        }

        private FlowLayoutPanel ToolGroup(string title)
        {
            var panel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = _surface,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                Padding = new Padding(0, 28, 0, 0),
                Margin = new Padding(0, 0, 0, 8)
            };
            panel.Controls.Add(new Label
            {
                Text = title,
                AutoSize = false,
                Width = 118,
                Height = 36,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                ForeColor = _muted,
                Margin = new Padding(0, 3, 10, 3)
            });
            return panel;
        }

        private TabPage BuildAddConfigTab()
        {
            var page = new TabPage("Thêm config") { BackColor = _surface, Padding = new Padding(22) };
            var pageLayout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, BackColor = _surface };
            pageLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 540));
            pageLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
            pageLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            page.Controls.Add(pageLayout);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 5, ColumnCount = 2, BackColor = _surface };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            pageLayout.Controls.Add(layout, 0, 0);

            configNameBox = AddText(layout, "Tên remote", "", 0, 0);
            configTypeCombo = AddCombo(layout, "Loại lưu trữ", 1, 0);
            configTypeCombo.Items.AddRange(new object[]
            {
                "drive", "onedrive", "s3", "ftp", "sftp", "webdav", "alias", "local", "dropbox", "box", "mega"
            });
            configTypeCombo.SelectedIndex = 0;

            configTestPathBox = AddText(layout, "Đường dẫn test sau khi tạo", "/", 0, 1);
            configRequireInternetBox = new CheckBox { Text = "Kiểm tra internet trước khi tạo", Checked = true, Dock = DockStyle.Fill };
            layout.Controls.Add(Wrap("Kiểm tra kết nối", configRequireInternetBox), 1, 1);

            configUserBox = AddText(layout, "User", "", 0, 2);
            configPassBox = AddText(layout, "Password", "", 1, 2);
            configPassBox.UseSystemPasswordChar = true;
            configObscurePassBox = new CheckBox { Text = "Tự mã hóa password bằng rclone obscure", Checked = true, Dock = DockStyle.Fill };
            layout.Controls.Add(Wrap("Tùy chọn password", configObscurePassBox), 0, 3);
            layout.SetColumnSpan(configObscurePassBox.Parent, 2);

            var paramsPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 0, 12, 8), BackColor = _surface };
            paramsPanel.Controls.Add(new Label
            {
                Text = "Tham số config, mỗi dòng một key=value",
                Dock = DockStyle.Top,
                Height = 22,
                Font = new Font("Segoe UI", 8.5F, FontStyle.Bold)
            });
            configParamsBox = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ScrollBars = ScrollBars.Both,
                Font = new Font("Consolas", 9.5F),
                Text = "client_id=\r\nclient_secret="
            };
            paramsPanel.Controls.Add(configParamsBox);
            configParamsBox.BringToFront();
            layout.Controls.Add(paramsPanel, 0, 4);
            layout.SetColumnSpan(paramsPanel, 2);

            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0, 12, 0, 0), BackColor = _surface };
            actions.Controls.Add(ActionButton("Kiểm tra kết nối", async (s, e) => await CheckConfigConnectionAsync(true), _surface, _text, 162));
            actions.Controls.Add(ActionButton("Thêm config", async (s, e) => await AddConfigFromUiAsync(), _primary, Color.White, 132));
            actions.Controls.Add(ActionButton("Mở wizard", (s, e) => OpenConfig(), _surface, _text, 112));
            configCheckLabel = new Label { Text = "Chưa kiểm tra", AutoSize = false, Width = 360, Height = 36, TextAlign = ContentAlignment.MiddleLeft };
            actions.Controls.Add(configCheckLabel);
            pageLayout.Controls.Add(actions, 0, 1);

            var help = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                WordWrap = true,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Color.FromArgb(248, 250, 252),
                Font = new Font("Segoe UI", 9F),
                Text = "Ví dụ:\r\n" +
                       "SFTP: host=1.2.3.4, user=root, pass=<mật khẩu đã rclone obscure>\r\n" +
                       "FTP: host=ftp.example.com, user=name, pass=<mật khẩu đã rclone obscure>\r\n" +
                       "S3: provider=AWS, access_key_id=..., secret_access_key=..., region=...\r\n" +
                       "WebDAV: url=https://example.com/webdav, vendor=other, user=..., pass=...\r\n\r\n" +
                       "Lưu ý:\r\n" +
                       "- User/Password phía trên sẽ tự ghi thành user/pass trong rclone config.\r\n" +
                       "- Nếu bật mã hóa password, app chạy rclone obscure trước khi lưu.\r\n" +
                       "- Không nhập lại user/pass ở ô tham số nếu đã nhập ở ô riêng phía trên.\r\n" +
                       "- Google Drive/OneDrive cần OAuth, nên dùng Mở wizard nếu chưa có token/client params.\r\n" +
                       "- SFTP/FTP/WebDAV thường cần host, user, pass.\r\n" +
                       "- S3 thường cần provider, access_key_id, secret_access_key, region hoặc endpoint.\r\n" +
                       "- Sau khi thêm config, app sẽ refresh danh sách remote và tạo profile mới.\r\n" +
                       "- Nếu kiểm tra kết nối báo lỗi, xem log bên phải để biết host/ping/rclone lỗi ở đâu.\r\n" +
                       "- Có thể dùng nút UI web nếu muốn cấu hình giống giao diện web của rclone."
            };
            pageLayout.Controls.Add(help, 0, 2);
            return page;
        }

        private Button HeaderButton(string text, EventHandler click)
        {
            var b = SmallButton(text, click);
            b.Height = 34;
            b.Width = Math.Max(100, text.Length * 9 + 24);
            return b;
        }

        private Button ActionButton(string text, EventHandler click, Color backColor, Color foreColor, int width)
        {
            var b = new Button
            {
                Text = text,
                Width = width,
                Height = 36,
                Margin = new Padding(4, 3, 4, 3),
                BackColor = backColor,
                ForeColor = foreColor,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8.7F, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            b.FlatAppearance.BorderColor = backColor == _surface ? Color.FromArgb(203, 213, 225) : Color.FromArgb(29, 78, 216);
            b.FlatAppearance.BorderSize = 1;
            b.FlatAppearance.MouseOverBackColor = backColor == _surface ? Color.FromArgb(248, 250, 252) : Color.FromArgb(30, 64, 175);
            b.FlatAppearance.MouseDownBackColor = backColor == _surface ? Color.FromArgb(241, 245, 249) : Color.FromArgb(30, 58, 138);
            b.Click += click;
            return b;
        }

        private Button SmallButton(string text, EventHandler click)
        {
            var b = ActionButton(text, click, _surface, _text, 92);
            b.Font = new Font("Segoe UI", 9F);
            return b;
        }

        private Button PrimaryButton(string text, EventHandler click)
        {
            return ActionButton(text, click, _primary, Color.White, 120);
        }

        private void SelectTab(string text)
        {
            if (mainTabs == null) return;
            foreach (TabPage page in mainTabs.TabPages)
            {
                if (string.Equals(page.Text, text, StringComparison.OrdinalIgnoreCase))
                {
                    mainTabs.SelectedTab = page;
                    break;
                }
            }
        }

        private Button OldSmallButton(string text, EventHandler click)
        {
            var b = new Button { Text = text, Width = 92, Height = 32, Margin = new Padding(4), FlatStyle = FlatStyle.System };
            b.Click += click;
            return b;
        }

        private Button OldPrimaryButton(string text, EventHandler click)
        {
            var b = SmallButton(text, click);
            b.Width = 120;
            return b;
        }

        private Control Wrap(string label, Control input)
        {
            var p = new Panel { Dock = DockStyle.Fill, Height = input is TextBox && ((TextBox)input).Multiline ? 104 : 76, Padding = new Padding(0, 0, 14, 12), BackColor = _surface };
            p.Controls.Add(new Label { Text = label, Dock = DockStyle.Top, Height = 24, Font = new Font("Segoe UI", 8.5F, FontStyle.Bold), ForeColor = _muted });
            input.Dock = DockStyle.Top;
            input.Top = 28;
            input.BackColor = Color.White;
            input.ForeColor = _text;
            p.Controls.Add(input);
            input.BringToFront();
            return p;
        }

        private TextBox AddText(TableLayoutPanel panel, string label, string value, int col, int row)
        {
            var input = new TextBox { Text = value, Height = 28 };
            panel.Controls.Add(Wrap(label, input), col, row);
            return input;
        }

        private ComboBox AddCombo(TableLayoutPanel panel, string label, int col, int row)
        {
            var input = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Height = 28 };
            panel.Controls.Add(Wrap(label, input), col, row);
            return input;
        }

        private NumericUpDown AddNumber(TableLayoutPanel panel, string label, decimal value, decimal min, decimal max, int col, int row)
        {
            var input = new NumericUpDown { Minimum = min, Maximum = max, Value = value, Height = 28 };
            panel.Controls.Add(Wrap(label, input), col, row);
            return input;
        }

        private void AddLog(string message, string level = "INFO")
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => AddLog(message, level)));
                return;
            }
            var line = DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss") + "  " + level + "  " + message + Environment.NewLine;
            logBox.AppendText(line);
            logBox.SelectionStart = logBox.TextLength;
            logBox.ScrollToCaret();
        }

        private void RunStartupDiagnostics()
        {
            if (!File.Exists(_rcloneExe))
            {
                AddLog("Không thấy rclone.exe trong thư mục app: " + _rcloneExe, "ERROR");
                return;
            }

            try
            {
                using (var identity = WindowsIdentity.GetCurrent())
                {
                    var principal = new WindowsPrincipal(identity);
                    if (principal.IsInRole(WindowsBuiltInRole.Administrator))
                        AddLog("App đang chạy bằng quyền Administrator. Nếu ổ không hiện trong This PC thường, hãy chạy Explorer cùng quyền hoặc mở lại app không dùng Administrator.", "WARN");
                }
            }
            catch
            {
            }

            var winfspPaths = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "WinFsp", "bin", "winfsp-x64.dll"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WinFsp", "bin", "winfsp-x64.dll")
            };
            if (!winfspPaths.Any(File.Exists))
                AddLog("Chưa phát hiện WinFsp. Mount rclone trên Windows cần WinFsp để tạo ổ đĩa.", "WARN");
        }

        private async Task EnsureRcloneAvailableAsync()
        {
            if (File.Exists(_rcloneExe)) return;
            AddLog("Không thấy rclone.exe trong thư mục app.");
            var confirm = MessageBox.Show(
                "Chưa có rclone.exe cạnh RcloneDrive.exe.\r\n\r\nBạn có muốn tải rclone bản Windows 64-bit mới nhất từ rclone.org không?",
                "Tải rclone.exe",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (confirm != DialogResult.Yes) return;
            await DownloadAndInstallRcloneAsync();
        }

        private async Task DownloadAndInstallRcloneAsync()
        {
            const string url = "https://downloads.rclone.org/rclone-current-windows-amd64.zip";
            var tempRoot = Path.Combine(Path.GetTempPath(), "RcloneDriveManager", Guid.NewGuid().ToString("N"));
            var zipPath = Path.Combine(tempRoot, "rclone-current-windows-amd64.zip");
            var extractDir = Path.Combine(tempRoot, "extract");
            try
            {
                Directory.CreateDirectory(tempRoot);
                Directory.CreateDirectory(extractDir);
                statusLabel.Text = "Đang tải rclone...";
                AddLog("Tải rclone từ " + url);
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                using (var client = new WebClient())
                {
                    await client.DownloadFileTaskAsync(new Uri(url), zipPath);
                }

                statusLabel.Text = "Đang giải nén rclone...";
                AddLog("Giải nén rclone...");
                ZipFile.ExtractToDirectory(zipPath, extractDir);
                var extractedExe = Directory.GetFiles(extractDir, "rclone.exe", SearchOption.AllDirectories).FirstOrDefault();
                if (string.IsNullOrWhiteSpace(extractedExe) || !File.Exists(extractedExe))
                    throw new FileNotFoundException("Không tìm thấy rclone.exe trong file zip.");

                File.Copy(extractedExe, _rcloneExe, true);
                statusLabel.Text = "Đã tải rclone";
                AddLog("Đã cài rclone.exe vào: " + _rcloneExe);
            }
            catch (Exception ex)
            {
                statusLabel.Text = "Tải rclone lỗi";
                AddLog("Tải rclone thất bại: " + ex.Message, "ERROR");
                MessageBox.Show("Tải rclone thất bại:\r\n" + ex.Message, "RcloneDrive", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true); } catch { }
            }
        }

        private void LoadProfiles()
        {
            Directory.CreateDirectory(_dataDir);
            _profiles.Clear();
            if (File.Exists(_profilesFile))
            {
                try
                {
                    var loaded = _json.Deserialize<List<DriveProfile>>(File.ReadAllText(_profilesFile, Encoding.UTF8));
                    if (loaded != null) _profiles.AddRange(loaded);
                }
                catch (Exception ex)
                {
                    AddLog("Cannot read profiles: " + ex.Message, "WARN");
                }
            }
            if (_profiles.Count == 0)
                _profiles.Add(new DriveProfile { Name = "Ổ api", Remote = "api:" });
            EnsureProfileIds();
            RenderProfiles();
        }

        private void EnsureProfileIds()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var changed = false;
            foreach (var p in _profiles)
            {
                if (string.IsNullOrWhiteSpace(p.Id) || seen.Contains(p.Id))
                {
                    p.Id = Guid.NewGuid().ToString("N");
                    changed = true;
                }
                seen.Add(p.Id);
            }
            if (changed) SaveProfiles();
        }

        private void SaveProfiles()
        {
            Directory.CreateDirectory(_dataDir);
            File.WriteAllText(_profilesFile, _json.Serialize(_profiles), Encoding.UTF8);
        }

        private void RenderProfiles()
        {
            ScanMountedDrives();
            profileList.Items.Clear();
            foreach (var p in _profiles)
            {
                var item = new ListViewItem(p.Name);
                item.SubItems.Add(DriveDisplay(p));
                var mounted = IsMountedProfile(p);
                item.SubItems.Add(mounted ? "Kết nối" : "Rảnh");
                item.SubItems.Add("Profile");
                item.BackColor = mounted ? Color.FromArgb(236, 253, 245) : Color.FromArgb(248, 250, 252);
                item.ForeColor = mounted ? Color.FromArgb(6, 95, 70) : _text;
                item.Tag = p;
                profileList.Items.Add(item);
            }
            foreach (var drive in _mountedExternalDrives)
            {
                var item = new ListViewItem(drive.Name);
                item.SubItems.Add(drive.DriveLetter);
                item.SubItems.Add("Đang bật");
                item.SubItems.Add(drive.Provider);
                item.BackColor = Color.FromArgb(239, 246, 255);
                item.ForeColor = Color.FromArgb(30, 64, 175);
                item.Tag = drive;
                profileList.Items.Add(item);
            }
        }

        private void SelectFirstProfile()
        {
            if (profileList.Items.Count > 0)
                profileList.Items[0].Selected = true;
        }

        private void RefreshMountedDriveList()
        {
            RenderProfiles();
            foreach (var drive in _detectedRcloneDrives.Keys.ToList())
                SetDriveIcon(drive, true);
            RefreshDriveLetters();
            statusLabel.Text = "Đã quét ổ mount";
            AddLog("Đã quét lại các ổ rclone/WinFsp đang mount.");
            RefreshExplorer();
        }

        private void ScanMountedDrives()
        {
            _mountedExternalDrives.Clear();
            _detectedRcloneDrives.Clear();
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var profile in _profiles)
            {
                if (!IsAutoDrive(profile.DriveLetter))
                    known.Add(NormalizeDriveChoice(profile.DriveLetter));
                string active;
                if (_activeDrives.TryGetValue(profile, out active))
                    known.Add(active);
            }

            foreach (var drive in DriveInfo.GetDrives())
            {
                var letter = drive.Name.Substring(0, 2);
                var root = GetDriveDisplayRoot(letter);
                if (string.IsNullOrWhiteSpace(root)) continue;
                if (!IsRcloneMountedDrive(letter, root)) continue;
                var detected = new MountedDriveInfo
                {
                    DriveLetter = letter,
                    DisplayRoot = root,
                    Provider = "rclone/WinFsp",
                    Name = FriendlyMountedName(letter, root)
                };
                AddDetectedRcloneDrive(detected, known);
            }

            foreach (var mount in GetRcloneMountProcesses())
            {
                if (string.IsNullOrWhiteSpace(mount.DriveLetter)) continue;
                var detected = new MountedDriveInfo
                {
                    DriveLetter = mount.DriveLetter,
                    DisplayRoot = string.IsNullOrWhiteSpace(mount.Source) ? mount.CommandLine : mount.Source,
                    Provider = "rclone/process",
                    Name = !string.IsNullOrWhiteSpace(mount.VolumeName) ? mount.VolumeName :
                           !string.IsNullOrWhiteSpace(mount.Source) ? mount.Source : "rclone " + mount.DriveLetter
                };
                AddDetectedRcloneDrive(detected, known);
            }
        }

        private void AddDetectedRcloneDrive(MountedDriveInfo detected, HashSet<string> known)
        {
            _detectedRcloneDrives[detected.DriveLetter] = detected;
            if (!known.Contains(detected.DriveLetter) &&
                !_mountedExternalDrives.Any(d => string.Equals(d.DriveLetter, detected.DriveLetter, StringComparison.OrdinalIgnoreCase)))
            {
                _mountedExternalDrives.Add(detected);
            }
        }

        private string GetDriveDisplayRoot(string letter)
        {
            try
            {
                var output = RunCommandCapture("cmd.exe", "/c net use " + letter);
                foreach (var line in output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("Remote name", StringComparison.OrdinalIgnoreCase))
                    {
                        var idx = trimmed.IndexOf('\\');
                        if (idx >= 0) return trimmed.Substring(idx).Trim();
                    }
                    if (trimmed.Contains(@"\\"))
                    {
                        var idx = trimmed.IndexOf(@"\\", StringComparison.Ordinal);
                        return trimmed.Substring(idx).Trim();
                    }
                }
            }
            catch
            {
            }
            try
            {
                var name = letter.TrimEnd(':');
                var output = RunCommandCapture("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -Command \"(Get-PSDrive -Name '" + name + "' -PSProvider FileSystem -ErrorAction SilentlyContinue).DisplayRoot\"");
                var root = output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).FirstOrDefault(x => x.StartsWith(@"\\", StringComparison.Ordinal));
                if (!string.IsNullOrWhiteSpace(root)) return root;
            }
            catch
            {
            }
            return "";
        }

        private string RunCommandCapture(string file, string args)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = file,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using (var proc = Process.Start(psi))
                {
                    if (proc == null) return "";
                    var stdoutTask = proc.StandardOutput.ReadToEndAsync();
                    var stderrTask = proc.StandardError.ReadToEndAsync();
                    if (!proc.WaitForExit(5000))
                    {
                        try { proc.Kill(); } catch { }
                        Task.WaitAll(new Task[] { stdoutTask, stderrTask }, 1000);
                        return SafeTaskResult(stdoutTask) + SafeTaskResult(stderrTask);
                    }
                    Task.WaitAll(stdoutTask, stderrTask);
                    return stdoutTask.Result + stderrTask.Result;
                }
            }
            catch
            {
                return "";
            }
        }

        private string SafeTaskResult(Task<string> task)
        {
            return task.IsCompleted && !task.IsFaulted && !task.IsCanceled ? task.Result : "";
        }

        private bool IsRcloneMountedDrive(string letter, string root)
        {
            if (!root.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase)) return false;
            if (root.IndexOf(@"\RaiDrive-", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (root.IndexOf("RaiDrive", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (root.IndexOf("CBFS", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            var output = RunCommandCapture("cmd.exe", "/c net use " + letter);
            return output.IndexOf("WinFsp", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   output.IndexOf(@"\\server\", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   output.IndexOf(@"\\localhost\", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   root.IndexOf(@"\\server\", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   root.IndexOf(@"\\localhost\", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   output.IndexOf("rclone", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private List<RcloneMountProcessInfo> GetRcloneMountProcesses()
        {
            var results = new List<RcloneMountProcessInfo>();
            var output = RunCommandCapture("wmic.exe", "process where \"name='rclone.exe'\" get ProcessId,CommandLine /format:list");
            var blocks = output.Split(new[] { "\r\r\n\r\r\n", "\r\n\r\n", "\n\n" }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var block in blocks)
            {
                var commandLine = ExtractWmicValue(block, "CommandLine");
                if (string.IsNullOrWhiteSpace(commandLine)) continue;
                if (commandLine.IndexOf(" mount ", StringComparison.OrdinalIgnoreCase) < 0) continue;
                var drive = ExtractMountDrive(commandLine);
                if (string.IsNullOrWhiteSpace(drive)) continue;
                int pid;
                int.TryParse(ExtractWmicValue(block, "ProcessId"), out pid);
                results.Add(new RcloneMountProcessInfo
                {
                    ProcessId = pid,
                    CommandLine = commandLine,
                    DriveLetter = NormalizeDriveChoice(drive),
                    Source = ExtractMountSource(commandLine),
                    VolumeName = ExtractMountVolName(commandLine)
                });
            }
            return results;
        }

        private string ExtractWmicValue(string block, string key)
        {
            foreach (var line in block.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries))
            {
                var prefix = key + "=";
                if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return line.Substring(prefix.Length).Trim().Trim('"');
            }
            return "";
        }

        private string ExtractMountDrive(string commandLine)
        {
            foreach (Match match in Regex.Matches(commandLine ?? "", @"(?<!\S)([A-Za-z]:)(?!\S)"))
                return match.Groups[1].Value.ToUpperInvariant();
            return "";
        }

        private string ExtractMountSource(string commandLine)
        {
            var match = Regex.Match(commandLine ?? "", @"\bmount\s+(""([^""]+)""|(\S+))\s+", RegexOptions.IgnoreCase);
            if (!match.Success) return "";
            return !string.IsNullOrEmpty(match.Groups[2].Value) ? match.Groups[2].Value : match.Groups[3].Value;
        }

        private string ExtractMountVolName(string commandLine)
        {
            var match = Regex.Match(commandLine ?? "", @"--volname\s+(""([^""]+)""|(\S+))", RegexOptions.IgnoreCase);
            if (!match.Success) return "";
            return !string.IsNullOrEmpty(match.Groups[2].Value) ? match.Groups[2].Value : match.Groups[3].Value;
        }

        private string FriendlyMountedName(string letter, string root)
        {
            var name = root.Trim('\\');
            var parts = name.Split('\\');
            if (parts.Length > 1) return parts[parts.Length - 1];
            return letter + " " + root;
        }

        private DriveProfile SelectedProfile
        {
            get
            {
                return profileList.SelectedItems.Count == 0 ? null : profileList.SelectedItems[0].Tag as DriveProfile;
            }
        }

        private MountedDriveInfo SelectedMountedDrive
        {
            get
            {
                return profileList.SelectedItems.Count == 0 ? null : profileList.SelectedItems[0].Tag as MountedDriveInfo;
            }
        }

        private void LoadSelectedProfileIntoFields()
        {
            var p = SelectedProfile;
            if (p == null)
            {
                var mounted = SelectedMountedDrive;
                if (mounted != null)
                {
                    nameBox.Text = mounted.Name;
                    pathBox.Text = mounted.DisplayRoot;
                    SelectComboValue(driveCombo, mounted.DriveLetter);
                    statusLabel.Text = "Ổ đang bật sẵn: " + mounted.DriveLetter;
                }
                return;
            }
            _loadingProfileFields = true;
            try
            {
                nameBox.Text = p.Name;
                SelectComboValue(remoteCombo, p.Remote);
                pathBox.Text = DriveProfile.NormalizeRemotePath(p.RemotePath, p.Remote);
                SelectComboValue(driveCombo, p.DriveLetter);
                SelectComboValue(cacheModeCombo, p.CacheMode);
                cacheDirBox.Text = p.CacheDir;
                cacheMaxAgeBox.Text = string.IsNullOrWhiteSpace(p.VfsCacheMaxAge) ? "72h" : p.VfsCacheMaxAge;
                writeBackBox.Text = string.IsNullOrWhiteSpace(p.VfsWriteBack) ? "5s" : p.VfsWriteBack;
                readOnlyBox.Checked = p.ReadOnly;
                autoMountBox.Checked = p.AutoMount;
                networkModeBox.Checked = p.NetworkMode;
                transfersBox.Value = Math.Max(transfersBox.Minimum, Math.Min(transfersBox.Maximum, p.Transfers <= 0 ? 4 : p.Transfers));
                bufferBox.Value = Math.Max(bufferBox.Minimum, Math.Min(bufferBox.Maximum, p.BufferSizeMb <= 0 ? 32 : p.BufferSizeMb));
                extraArgsBox.Text = p.ExtraArgs ?? "";
                _profileNameEditedByUser = !ShouldAutoReplaceProfileName(p.Name);
            }
            finally
            {
                _loadingProfileFields = false;
            }
        }

        private void AutoNameFromSelectedRemote()
        {
            if (_loadingProfileFields) return;
            var remote = Convert.ToString(remoteCombo.SelectedItem ?? "").Trim();
            if (string.IsNullOrWhiteSpace(remote)) return;
            var remoteName = remote.TrimEnd(':');
            var current = (nameBox.Text ?? "").Trim();
            if (!_profileNameEditedByUser && ShouldAutoReplaceProfileName(current))
            {
                _changingProfileNameAutomatically = true;
                try
                {
                    nameBox.Text = remoteName;
                }
                finally
                {
                    _changingProfileNameAutomatically = false;
                }
            }
        }

        private bool ShouldAutoReplaceProfileName(string current)
        {
            if (string.IsNullOrWhiteSpace(current)) return true;
            if (string.Equals(current, "Ổ mới", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(current, "Ổ đĩa", StringComparison.OrdinalIgnoreCase)) return true;
            if (Regex.IsMatch(current, @"^Ổ\s+\d+$", RegexOptions.IgnoreCase)) return true;
            return false;
        }

        private void SaveCurrentProfile()
        {
            var p = SelectedProfile;
            if (p == null) return;
            p.Name = string.IsNullOrWhiteSpace(nameBox.Text) ? "Ổ đĩa" : nameBox.Text.Trim();
            p.Remote = Convert.ToString(remoteCombo.SelectedItem ?? remoteCombo.Text ?? "").Trim();
            p.RemotePath = DriveProfile.NormalizeRemotePath(pathBox.Text, p.Remote);
            p.DriveLetter = NormalizeDriveChoice(Convert.ToString(driveCombo.SelectedItem ?? driveCombo.Text ?? "AUTO"));
            p.CacheMode = Convert.ToString(cacheModeCombo.SelectedItem ?? "full");
            p.CacheDir = cacheDirBox.Text.Trim();
            p.VfsCacheMaxAge = string.IsNullOrWhiteSpace(cacheMaxAgeBox.Text) ? "72h" : cacheMaxAgeBox.Text.Trim();
            p.VfsWriteBack = string.IsNullOrWhiteSpace(writeBackBox.Text) ? "5s" : writeBackBox.Text.Trim();
            p.ReadOnly = readOnlyBox.Checked;
            p.AutoMount = autoMountBox.Checked;
            p.NetworkMode = networkModeBox.Checked;
            p.Transfers = (int)transfersBox.Value;
            p.BufferSizeMb = (int)bufferBox.Value;
            p.ExtraArgs = extraArgsBox.Text.Trim();
            SaveProfiles();
            RenderProfiles();
            SelectProfile(p);
            AddLog("Đã lưu profile: " + p.Name);
        }

        private void ApplyCodeIdePreset()
        {
            SelectComboValue(cacheModeCombo, "full");
            cacheMaxAgeBox.Text = "72h";
            writeBackBox.Text = "5s";
            transfersBox.Value = Math.Max(transfersBox.Minimum, Math.Min(transfersBox.Maximum, 4));
            bufferBox.Value = Math.Max(bufferBox.Minimum, Math.Min(bufferBox.Maximum, 32));
            networkModeBox.Checked = true;
            readOnlyBox.Checked = false;

            SaveCurrentProfile();
            AddLog("Đã áp dụng preset Code IDE: cache full, giữ cache 72h, tự upload sau 5s.");
        }

        private void NewProfile()
        {
            var remote = Convert.ToString(remoteCombo.SelectedItem ?? remoteCombo.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(remote))
                remote = _remotes.FirstOrDefault() ?? "";
            var baseName = string.IsNullOrWhiteSpace(nameBox.Text)
                ? (string.IsNullOrWhiteSpace(remote) ? "Ổ" : remote.TrimEnd(':'))
                : nameBox.Text.Trim();
            var p = new DriveProfile
            {
                Name = UniqueProfileName(baseName),
                Remote = remote,
                RemotePath = DriveProfile.NormalizeRemotePath(pathBox.Text, remote),
                DriveLetter = GetFreeDriveLetters().FirstOrDefault() ?? "Z:",
                CacheMode = Convert.ToString(cacheModeCombo.SelectedItem ?? "full"),
                CacheDir = cacheDirBox.Text.Trim(),
                VfsCacheMaxAge = string.IsNullOrWhiteSpace(cacheMaxAgeBox.Text) ? "72h" : cacheMaxAgeBox.Text.Trim(),
                VfsWriteBack = string.IsNullOrWhiteSpace(writeBackBox.Text) ? "5s" : writeBackBox.Text.Trim(),
                ReadOnly = readOnlyBox.Checked,
                AutoMount = autoMountBox.Checked,
                NetworkMode = networkModeBox.Checked,
                Transfers = (int)transfersBox.Value,
                BufferSizeMb = (int)bufferBox.Value,
                ExtraArgs = extraArgsBox.Text.Trim()
            };
            _profiles.Add(p);
            SaveProfiles();
            RenderProfiles();
            SelectProfile(p);
            AddLog("Đã tạo profile mới: " + p.Name + " dùng " + p.Remote + p.RemotePath + " -> " + p.DriveLetter);
        }

        private string UniqueProfileName(string baseName)
        {
            baseName = string.IsNullOrWhiteSpace(baseName) ? "Ổ" : baseName.Trim();
            if (!_profiles.Any(p => string.Equals(p.Name, baseName, StringComparison.OrdinalIgnoreCase)))
                return baseName;
            for (var i = 2; i < 1000; i++)
            {
                var candidate = baseName + " " + i;
                if (!_profiles.Any(p => string.Equals(p.Name, candidate, StringComparison.OrdinalIgnoreCase)))
                    return candidate;
            }
            return baseName + " " + DateTime.Now.ToString("HHmmss");
        }

        private void DeleteCurrentProfile()
        {
            var p = SelectedProfile;
            if (p == null) return;
            if (IsMountedProfile(p))
            {
                MessageBox.Show("Hãy ngắt kết nối profile trước.", "Trình quản lý ổ Rclone", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            _profiles.Remove(p);
            SaveProfiles();
            RenderProfiles();
            SelectFirstProfile();
        }

        private void BrowseCacheDirForSelectedProfile()
        {
            var p = SelectedProfile;
            if (p == null)
            {
                MessageBox.Show("Hãy chọn một profile trước.", "Trình quản lý ổ Rclone", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var selected = PickCacheDirectory(p.CacheDir);
            if (string.IsNullOrWhiteSpace(selected)) return;
            p.CacheDir = selected;
            cacheDirBox.Text = selected;
            SaveProfiles();
            AddLog("Đã đặt thư mục cache cho " + p.Name + ": " + selected);
        }

        private void BrowseCacheDirForAllProfiles()
        {
            var selected = PickCacheDirectory(_profiles.Select(p => p.CacheDir).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)));
            if (string.IsNullOrWhiteSpace(selected)) return;
            foreach (var p in _profiles)
                p.CacheDir = selected;
            if (cacheDirBox != null) cacheDirBox.Text = selected;
            SaveProfiles();
            RenderProfiles();
            AddLog("Đã đặt thư mục cache cho tất cả ổ: " + selected);
        }

        private string PickCacheDirectory(string initial)
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Chọn thư mục cache rclone";
                dialog.ShowNewFolderButton = true;
                var expanded = Environment.ExpandEnvironmentVariables(initial ?? "");
                if (!string.IsNullOrWhiteSpace(expanded) && Directory.Exists(expanded))
                    dialog.SelectedPath = expanded;
                return dialog.ShowDialog(this) == DialogResult.OK ? dialog.SelectedPath : "";
            }
        }

        private void OpenDriveSettingsDialog()
        {
            var p = SelectedProfile;
            if (p == null)
            {
                MessageBox.Show("Hãy chọn một profile ổ trước.", "Trình quản lý ổ Rclone", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (var form = new Form())
            {
                form.Text = "Cài đặt ổ - " + p.Name;
                form.Width = 700;
                form.Height = 720;
                form.StartPosition = FormStartPosition.CenterParent;
                form.MinimizeBox = false;
                form.MaximizeBox = false;
                form.FormBorderStyle = FormBorderStyle.FixedDialog;
                form.Font = new Font("Segoe UI", 9F);

                var layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(14) };
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
                form.Controls.Add(layout);

                var dName = DialogText(layout, "Tên profile", p.Name, 0, 0);
                var dRemote = DialogCombo(layout, "Remote", _remotes, p.Remote, 1, 0, true);
                var dPath = DialogText(layout, "Đường dẫn remote", p.RemotePath, 0, 1);
                var dDrive = DialogCombo(layout, "Ký tự ổ đĩa", new[] { "Tự chọn ổ trống" }.Concat(GetFreeDriveLetters()).Concat(new[] { p.DriveLetter, "Z:", "Y:", "X:", "W:" }).Distinct(StringComparer.OrdinalIgnoreCase), IsAutoDrive(p.DriveLetter) ? "Tự chọn ổ trống" : p.DriveLetter, 1, 1, true);
                var dCacheMode = DialogCombo(layout, "Chế độ VFS cache", new[] { "off", "minimal", "writes", "full" }, p.CacheMode, 0, 2, false);
                var dCacheDir = DialogText(layout, "Thư mục cache", p.CacheDir, 1, 2);
                var cachePicker = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 38, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(14, 0, 14, 0) };
                cachePicker.Controls.Add(ActionButton("Browse cache", (s, e) =>
                {
                    var picked = PickCacheDirectory(dCacheDir.Text);
                    if (!string.IsNullOrWhiteSpace(picked)) dCacheDir.Text = picked;
                }, _surface, _text, 126));
                form.Controls.Add(cachePicker);
                cachePicker.BringToFront();
                var dTransfers = DialogNumber(layout, "Transfers", p.Transfers <= 0 ? 4 : p.Transfers, 1, 64, 0, 3);
                var dBuffer = DialogNumber(layout, "Bộ đệm MB", p.BufferSizeMb <= 0 ? 32 : p.BufferSizeMb, 1, 1024, 1, 3);
                var dCacheMaxAge = DialogText(layout, "Giữ cache tối đa", string.IsNullOrWhiteSpace(p.VfsCacheMaxAge) ? "72h" : p.VfsCacheMaxAge, 0, 4);
                var dWriteBack = DialogText(layout, "Upload sau khi sửa", string.IsNullOrWhiteSpace(p.VfsWriteBack) ? "5s" : p.VfsWriteBack, 1, 4);
                var dExtra = DialogText(layout, "Tham số rclone thêm", p.ExtraArgs ?? "", 0, 5);
                layout.SetColumnSpan(dExtra.Parent, 2);

                var checks = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 54, Padding = new Padding(14, 8, 14, 4) };
                var dReadOnly = new CheckBox { Text = "Chỉ đọc", Width = 90, Checked = p.ReadOnly };
                var dAuto = new CheckBox { Text = "Tự mount khi mở app", Width = 180, Checked = p.AutoMount };
                var dNetwork = new CheckBox { Text = "Network mode", Width = 130, Checked = p.NetworkMode };
                checks.Controls.Add(dReadOnly);
                checks.Controls.Add(dAuto);
                checks.Controls.Add(dNetwork);
                form.Controls.Add(checks);
                checks.BringToFront();

                var note = new Label
                {
                    Dock = DockStyle.Top,
                    Height = 58,
                    Padding = new Padding(14, 8, 14, 0),
                    Text = "Cài đặt này áp dụng cho profile đang chọn. Nếu ổ đang mount, hãy Ngắt rồi Kết nối lại để nhận cấu hình mới."
                };
                form.Controls.Add(note);
                note.BringToFront();

                var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 58, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(14, 10, 14, 10) };
                var ok = new Button { Text = "Lưu", Width = 96, DialogResult = DialogResult.OK };
                var cancel = new Button { Text = "Hủy", Width = 96, DialogResult = DialogResult.Cancel };
                buttons.Controls.Add(ok);
                buttons.Controls.Add(cancel);
                form.Controls.Add(buttons);
                form.AcceptButton = ok;
                form.CancelButton = cancel;

                if (form.ShowDialog(this) != DialogResult.OK) return;

                p.Name = string.IsNullOrWhiteSpace(dName.Text) ? "Ổ đĩa" : dName.Text.Trim();
                p.Remote = Convert.ToString(dRemote.SelectedItem ?? dRemote.Text ?? "").Trim();
                p.RemotePath = DriveProfile.NormalizeRemotePath(dPath.Text, p.Remote);
                p.DriveLetter = NormalizeDriveChoice(Convert.ToString(dDrive.SelectedItem ?? dDrive.Text ?? "AUTO"));
                p.CacheMode = Convert.ToString(dCacheMode.SelectedItem ?? dCacheMode.Text ?? "full").Trim();
                p.CacheDir = dCacheDir.Text.Trim();
                p.VfsCacheMaxAge = string.IsNullOrWhiteSpace(dCacheMaxAge.Text) ? "72h" : dCacheMaxAge.Text.Trim();
                p.VfsWriteBack = string.IsNullOrWhiteSpace(dWriteBack.Text) ? "5s" : dWriteBack.Text.Trim();
                p.Transfers = (int)dTransfers.Value;
                p.BufferSizeMb = (int)dBuffer.Value;
                p.ExtraArgs = dExtra.Text.Trim();
                p.ReadOnly = dReadOnly.Checked;
                p.AutoMount = dAuto.Checked;
                p.NetworkMode = dNetwork.Checked;

                SaveProfiles();
                RenderProfiles();
                SelectProfile(p);
                LoadSelectedProfileIntoFields();
                AddLog("Đã cập nhật cài đặt ổ: " + p.Name);
            }
        }

        private TextBox DialogText(TableLayoutPanel panel, string label, string value, int col, int row)
        {
            var input = new TextBox { Text = value ?? "", Height = 28 };
            panel.Controls.Add(Wrap(label, input), col, row);
            return input;
        }

        private ComboBox DialogCombo(TableLayoutPanel panel, string label, IEnumerable<string> values, string selected, int col, int row, bool editable)
        {
            var input = new ComboBox { Height = 28, DropDownStyle = editable ? ComboBoxStyle.DropDown : ComboBoxStyle.DropDownList };
            foreach (var value in values.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase))
                input.Items.Add(value);
            if (!string.IsNullOrWhiteSpace(selected))
            {
                var i = input.Items.IndexOf(selected);
                if (i >= 0) input.SelectedIndex = i;
                else input.Text = selected;
            }
            else if (input.Items.Count > 0) input.SelectedIndex = 0;
            panel.Controls.Add(Wrap(label, input), col, row);
            return input;
        }

        private NumericUpDown DialogNumber(TableLayoutPanel panel, string label, int value, int min, int max, int col, int row)
        {
            var input = new NumericUpDown { Minimum = min, Maximum = max, Value = Math.Max(min, Math.Min(max, value)), Height = 28 };
            panel.Controls.Add(Wrap(label, input), col, row);
            return input;
        }

        private void SelectProfile(DriveProfile profile)
        {
            foreach (ListViewItem item in profileList.Items)
            {
                if (ReferenceEquals(item.Tag, profile))
                {
                    item.Selected = true;
                    item.EnsureVisible();
                    break;
                }
            }
        }

        private async Task RefreshRemotesAsync()
        {
            if (!File.Exists(_rcloneExe))
            {
                statusLabel.Text = "rclone.exe not found";
                AddLog("Cannot find rclone.exe in " + _appDir, "ERROR");
                return;
            }
            statusLabel.Text = "Refreshing remotes...";
            var output = await RunCaptureAsync("listremotes");
            _remotes.Clear();
            _remotes.AddRange(output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0));
            FillRemoteCombos();
            statusLabel.Text = _remotes.Count + " remotes";
            AddLog("Loaded " + _remotes.Count + " remotes.");
        }

        private async Task RefreshAllAsync()
        {
            RefreshMountedDriveList();
            await RefreshRemotesAsync();
            RenderProfiles();
            RefreshDriveLetters();
        }

        private void FillRemoteCombos()
        {
            var remoteItems = _remotes
                .Concat(_profiles.Select(p => p.Remote))
                .Where(r => !string.IsNullOrWhiteSpace(r))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var combos = new[] { remoteCombo, browseRemoteCombo, transferSourceRemoteCombo, transferDestRemoteCombo };
            foreach (var combo in combos)
            {
                var old = Convert.ToString(combo.SelectedItem);
                combo.Items.Clear();
                foreach (var r in remoteItems) combo.Items.Add(r);
                if (!string.IsNullOrEmpty(old)) SelectComboValue(combo, old);
                if (combo.SelectedIndex < 0 && combo.Items.Count > 0) combo.SelectedIndex = 0;
            }
            LoadSelectedProfileIntoFields();
        }

        private void RefreshDriveLetters()
        {
            var old = Convert.ToString(driveCombo.SelectedItem ?? driveCombo.Text ?? "");
            driveCombo.Items.Clear();
            driveCombo.Items.Add("Tự chọn ổ trống");
            foreach (var d in GetFreeDriveLetters().Concat(new[] { old, "Z:", "Y:", "X:", "W:" }).Where(d => !string.IsNullOrWhiteSpace(d)).Distinct(StringComparer.OrdinalIgnoreCase))
                driveCombo.Items.Add(d);
            if (!string.IsNullOrWhiteSpace(old)) SelectComboValue(driveCombo, old);
            if (driveCombo.SelectedIndex < 0 && driveCombo.Items.Count > 0) driveCombo.SelectedIndex = 0;
        }

        private IEnumerable<string> GetFreeDriveLetters()
        {
            var used = DriveInfo.GetDrives().Select(d => d.Name.Substring(0, 2)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var letters = "ZYXWVUTSRQPONMLKJIHGFEDC".Select(c => c + ":");
            return letters.Where(d => !used.Contains(d));
        }

        private bool IsAutoDrive(string drive)
        {
            return string.IsNullOrWhiteSpace(drive) ||
                   string.Equals(drive, "AUTO", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(drive, "Tự chọn ổ trống", StringComparison.OrdinalIgnoreCase);
        }

        private string NormalizeDriveChoice(string value)
        {
            if (IsAutoDrive(value)) return "AUTO";
            value = (value ?? "").Trim().ToUpperInvariant();
            if (value.Length == 1) value += ":";
            return value;
        }

        private string DriveDisplay(DriveProfile profile)
        {
            string active;
            if (_activeDrives.TryGetValue(profile, out active) && !string.IsNullOrWhiteSpace(active))
                return active;
            return IsAutoDrive(profile.DriveLetter) ? "Tự động" : profile.DriveLetter;
        }

        private string ResolveDriveForMount(DriveProfile profile)
        {
            if (!IsAutoDrive(profile.DriveLetter)) return NormalizeDriveChoice(profile.DriveLetter);
            var free = GetFreeDriveLetters().FirstOrDefault();
            if (string.IsNullOrWhiteSpace(free))
                throw new InvalidOperationException("Không tìm thấy ký tự ổ đĩa trống.");
            return free;
        }

        private string ActiveDriveForProfile(DriveProfile profile)
        {
            string active;
            if (_activeDrives.TryGetValue(profile, out active) && !string.IsNullOrWhiteSpace(active))
                return active;
            return NormalizeDriveChoice(profile.DriveLetter);
        }

        private string ProfileDuration(string value, string fallback)
        {
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }

        private bool IsDriveAvailableForMount(string drive)
        {
            if (string.IsNullOrWhiteSpace(drive) || IsAutoDrive(drive)) return false;
            var normalized = NormalizeDriveChoice(drive);
            if (_mounts.ContainsKey(normalized)) return false;
            return !DriveInfo.GetDrives().Any(d => string.Equals(d.Name.Substring(0, 2), normalized, StringComparison.OrdinalIgnoreCase));
        }

        private void SelectComboValue(ComboBox combo, string value)
        {
            if (value == null) return;
            if (IsAutoDrive(value)) value = "Tự chọn ổ trống";
            var i = combo.Items.IndexOf(value);
            if (i >= 0) combo.SelectedIndex = i;
            else if (combo.DropDownStyle != ComboBoxStyle.DropDownList) combo.Text = value;
        }

        private bool IsMounted(string drive)
        {
            if (string.IsNullOrWhiteSpace(drive)) return false;
            if (IsAutoDrive(drive)) return false;
            drive = NormalizeDriveChoice(drive);
            Process proc;
            if (_mounts.TryGetValue(drive, out proc) && proc != null && !proc.HasExited)
                return true;
            return _detectedRcloneDrives.ContainsKey(drive);
        }

        private bool IsMountedProfile(DriveProfile profile)
        {
            string drive;
            if (_activeDrives.TryGetValue(profile, out drive))
                return IsMounted(drive);
            if (IsAutoDrive(profile.DriveLetter)) return false;
            var normalized = NormalizeDriveChoice(profile.DriveLetter);
            MountedDriveInfo detected;
            return _detectedRcloneDrives.TryGetValue(normalized, out detected) &&
                   string.Equals(NormalizeSourceForCompare(detected.DisplayRoot), NormalizeSourceForCompare(profile.Source), StringComparison.OrdinalIgnoreCase);
        }

        private string NormalizeSourceForCompare(string source)
        {
            return (source ?? "").Trim().Trim('"').Replace('\\', '/').TrimEnd('/');
        }

        private void CleanupDeadMountState(DriveProfile profile)
        {
            string drive;
            if (!_activeDrives.TryGetValue(profile, out drive)) return;
            Process proc;
            if (!_mounts.TryGetValue(drive, out proc) || proc == null || proc.HasExited)
                CleanupMountState(profile, drive);
        }

        private void CleanupMountState(DriveProfile profile, string drive)
        {
            if (!string.IsNullOrWhiteSpace(drive))
                _mounts.Remove(drive);
            _activeDrives.Remove(profile);
        }

        private async Task MountSelectedAsync()
        {
            var p = SelectedProfile;
            if (p == null)
                p = CreateProfileFromCurrentFields();
            else if (IsMountedProfile(p))
            {
                var baseName = string.IsNullOrWhiteSpace(nameBox.Text) ? p.Name : nameBox.Text.Trim();
                p = CreateProfileFromCurrentFields(UniqueProfileName(baseName), true);
                if (p != null)
                    AddLog("Profile đang kết nối, tự tạo ổ mới để mount thêm.");
            }
            else
                SaveCurrentProfile();
            if (p == null) return;
            await MountProfileAsync(p);
        }

        private DriveProfile CreateProfileFromCurrentFields(string forcedName = null, bool preferFreeDrive = false)
        {
            var remote = Convert.ToString(remoteCombo.SelectedItem ?? remoteCombo.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(remote))
            {
                AddLog("Hãy chọn remote trước khi kết nối.", "ERROR");
                SelectTab("Ổ đĩa");
                return null;
            }
            var p = new DriveProfile
            {
                Name = string.IsNullOrWhiteSpace(forcedName)
                    ? UniqueProfileName(string.IsNullOrWhiteSpace(nameBox.Text) ? remote.TrimEnd(':') : nameBox.Text.Trim())
                    : forcedName.Trim(),
                Remote = remote,
                RemotePath = DriveProfile.NormalizeRemotePath(pathBox.Text, remote),
                DriveLetter = DriveChoiceFromForm(preferFreeDrive),
                CacheMode = Convert.ToString(cacheModeCombo.SelectedItem ?? "full"),
                CacheDir = cacheDirBox.Text.Trim(),
                VfsCacheMaxAge = string.IsNullOrWhiteSpace(cacheMaxAgeBox.Text) ? "72h" : cacheMaxAgeBox.Text.Trim(),
                VfsWriteBack = string.IsNullOrWhiteSpace(writeBackBox.Text) ? "5s" : writeBackBox.Text.Trim(),
                ReadOnly = readOnlyBox.Checked,
                AutoMount = autoMountBox.Checked,
                NetworkMode = networkModeBox.Checked,
                Transfers = (int)transfersBox.Value,
                BufferSizeMb = (int)bufferBox.Value,
                ExtraArgs = extraArgsBox.Text.Trim()
            };
            _profiles.Add(p);
            SaveProfiles();
            RenderProfiles();
            SelectProfile(p);
            AddLog("Đã tạo profile mới từ form hiện tại: " + p.Name);
            return p;
        }

        private string DriveChoiceFromForm(bool preferFreeDrive)
        {
            var selected = NormalizeDriveChoice(Convert.ToString(driveCombo.SelectedItem ?? driveCombo.Text ?? "AUTO"));
            if (!preferFreeDrive) return selected;
            if (!IsAutoDrive(selected) && IsDriveAvailableForMount(selected)) return selected;
            return GetFreeDriveLetters().FirstOrDefault() ?? "AUTO";
        }

        private async Task MountProfileAsync(DriveProfile p)
        {
            if (string.IsNullOrWhiteSpace(p.Remote))
            {
                AddLog("Profile has no remote.", "ERROR");
                return;
            }
            CleanupDeadMountState(p);
            if (IsMountedProfile(p))
            {
                AddLog(DriveDisplay(p) + " is already mounted.", "WARN");
                return;
            }

            string mountDrive;
            try
            {
                mountDrive = ResolveDriveForMount(p);
            }
            catch (Exception ex)
            {
                AddLog(ex.Message, "ERROR");
                RefreshDriveLetters();
                return;
            }
            if (!IsDriveAvailableForMount(mountDrive))
            {
                AddLog("Ổ " + mountDrive + " đang được Windows sử dụng. Hãy chọn ký tự khác hoặc dùng Tự chọn ổ trống.", "ERROR");
                RefreshDriveLetters();
                return;
            }
            var preflight = await TestRemoteBeforeMountAsync(p.Source);
            if (!preflight)
            {
                CleanupMountState(p, mountDrive);
                RenderProfiles();
                RefreshDriveLetters();
                return;
            }

            var args = BuildMountArgs(p, mountDrive, SafeVolName(p, mountDrive));
            AddLog("Mount " + p.Source + " -> " + mountDrive);
            var proc = StartRclone(args, false);
            proc.EnableRaisingEvents = true;
            proc.OutputDataReceived += (s, e) => { if (e.Data != null) AddLog(e.Data); };
            proc.ErrorDataReceived += (s, e) => { if (e.Data != null) AddLog(e.Data, "RCLONE"); };
            proc.Exited += (s, e) =>
            {
                AddLog("Mount process exited for " + mountDrive + " code " + proc.ExitCode, proc.ExitCode == 0 ? "INFO" : "WARN");
                BeginInvoke(new Action(() =>
                {
                    CleanupMountState(p, mountDrive);
                    RenderProfiles();
                    RefreshDriveLetters();
                }));
            };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            await Task.Delay(900);
            if (proc.HasExited)
            {
                CleanupMountState(p, mountDrive);
                RenderProfiles();
                RefreshDriveLetters();
                AddLog("Mount exited early. Check WinFsp install, remote auth, or drive letter.", "ERROR");
                return;
            }
            _mounts[mountDrive] = proc;
            _activeDrives[p] = mountDrive;
            RenderProfiles();
            if (await WaitForDriveReadyAsync(mountDrive, 8000))
            {
                AddLog("Mounted " + p.Name + " at " + mountDrive + " PID " + proc.Id);
                SetDriveIcon(mountDrive, true);
                RefreshExplorer();
                OpenDriveInExplorer(mountDrive);
            }
            else if (proc.HasExited)
            {
                CleanupMountState(p, mountDrive);
                RenderProfiles();
                RefreshDriveLetters();
                AddLog("Mount thất bại cho " + mountDrive + ". Xem log rclone phía trên để biết nguyên nhân.", "ERROR");
            }
            else
            {
                AddLog("Process mount đang chạy nhưng Windows chưa thấy ổ " + mountDrive + ". Thử gõ " + mountDrive + "\\ trên thanh địa chỉ Explorer; nếu vẫn không thấy, kiểm tra WinFsp hoặc log rclone.", "WARN");
            }
        }

        private List<string> BuildMountArgs(DriveProfile p, string mountDrive, string volumeName)
        {
            var args = new List<string> { "mount", p.Source, mountDrive };
            if (!string.IsNullOrWhiteSpace(p.CacheMode) && !string.Equals(p.CacheMode, "off", StringComparison.OrdinalIgnoreCase))
            {
                args.Add("--vfs-cache-mode");
                args.Add(p.CacheMode);
                args.Add("--vfs-cache-max-age");
                args.Add(ProfileDuration(p.VfsCacheMaxAge, "72h"));
                args.Add("--vfs-write-back");
                args.Add(ProfileDuration(p.VfsWriteBack, "5s"));
                if (!string.IsNullOrWhiteSpace(p.CacheDir))
                {
                    args.Add("--cache-dir");
                    args.Add(Environment.ExpandEnvironmentVariables(p.CacheDir));
                }
            }
            if (p.ReadOnly) args.Add("--read-only");
            if (p.NetworkMode) args.Add("--network-mode");
            if (!HasExtraArg(p.ExtraArgs, "--links") && !HasExtraArg(p.ExtraArgs, "-l"))
                args.Add("--links");
            args.Add("--volname");
            args.Add(volumeName);
            args.Add("--dir-cache-time");
            args.Add("1m");
            args.Add("--buffer-size");
            args.Add(Math.Max(1, p.BufferSizeMb) + "M");
            args.Add("--transfers");
            args.Add(Math.Max(1, p.Transfers).ToString());
            AddExtraArgs(args, p.ExtraArgs);
            args.Add("-v");
            return args;
        }

        private async Task<bool> TestRemoteBeforeMountAsync(string source)
        {
            AddLog("Kiểm tra remote trước khi mount: " + source);
            var result = await RunRcloneResultAsync(45000, "rclone lsf " + source + " --max-depth 1", "lsf", source, "--max-depth", "1");
            if (result.TimedOut)
            {
                AddLog("Kiểm tra remote quá thời gian. Không mount để tránh app bị kẹt.", "ERROR");
                return false;
            }
            if (result.ExitCode != 0)
            {
                AddLog(PreflightErrorMessage(result.Output, result.ExitCode), "ERROR");
                return false;
            }
            AddLog("Remote OK, bắt đầu mount.");
            return true;
        }

        private string PreflightErrorMessage(string output, int exitCode)
        {
            var text = output ?? "";
            if (text.IndexOf("Too many connections", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("421", StringComparison.OrdinalIgnoreCase) >= 0)
                return "FTP server đang giới hạn quá nhiều kết nối từ IP này. Hãy ngắt các ổ/phiên FTP cũ, đợi vài phút rồi thử lại. Không mount. Exit code: " + exitCode;
            if (text.IndexOf("Login authentication failed", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("530", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Sai user/pass hoặc tài khoản FTP không được phép đăng nhập. Không mount. Exit code: " + exitCode;
            if (text.IndexOf("directory not found", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Đường dẫn remote không tồn tại. Với rclone hãy nhập dạng /thu-muc, không nhập \\\\server\\share. Không mount. Exit code: " + exitCode;
            return "Remote chưa sẵn sàng. Không mount. Exit code: " + exitCode;
        }

        private void UnmountSelected()
        {
            var p = SelectedProfile;
            if (p == null)
            {
                var external = SelectedMountedDrive;
                if (external == null) return;
                UnmountDriveLetter(external.DriveLetter, external.Name);
                AddLog("Đã gửi lệnh ngắt ổ đang bật sẵn: " + external.DriveLetter);
                RenderProfiles();
                return;
            }
            Process proc;
            var drive = ActiveDriveForProfile(p);
            if (_mounts.TryGetValue(drive, out proc))
            {
                try
                {
                    if (!proc.HasExited) proc.Kill();
                    _mounts.Remove(drive);
                    _activeDrives.Remove(p);
                    AddLog("Stopped mount process for " + drive);
                }
                catch (Exception ex)
                {
                    AddLog("Cannot stop process: " + ex.Message, "WARN");
                }
            }
            if (!IsAutoDrive(drive))
                UnmountDriveLetter(drive, SafeVolName(p, drive));
            RenderProfiles();
        }

        private void UnmountDriveLetter(string drive, string nameHint)
        {
            if (IsAutoDrive(drive)) return;
            drive = NormalizeDriveChoice(drive);
            KillRcloneMountForDrive(drive, nameHint);
            var output = RunCommandCapture("cmd.exe", "/c net use " + drive + " /delete /y");
            if (!string.IsNullOrWhiteSpace(output)) AddLog(output.Trim());
            if (WaitForDriveRemoved(drive, 6000))
            {
                AddLog("Đã ngắt ổ " + drive);
            }
            else
            {
                AddLog("Ổ " + drive + " vẫn còn sau lệnh ngắt. Có thể process rclone đang chạy quyền khác; hãy chạy app cùng quyền với lúc mount.", "ERROR");
            }
            RefreshExplorer();
            CleanupMountStateForDrive(drive);
            SetDriveIcon(drive, false);
            RefreshDriveLetters();
        }

        private void CleanupMountStateForDrive(string drive)
        {
            _mounts.Remove(drive);
            foreach (var item in _activeDrives.Where(kv => string.Equals(kv.Value, drive, StringComparison.OrdinalIgnoreCase)).ToList())
                _activeDrives.Remove(item.Key);
        }

        private bool WaitForDriveRemoved(string drive, int timeoutMs)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                if (!DriveInfo.GetDrives().Any(d => string.Equals(d.Name.Substring(0, 2), drive, StringComparison.OrdinalIgnoreCase)))
                    return true;
                Application.DoEvents();
                System.Threading.Thread.Sleep(250);
            }
            return false;
        }

        private void KillRcloneMountForDrive(string drive, string nameHint)
        {
            try
            {
                var shareHint = string.IsNullOrWhiteSpace(nameHint) ? "" : nameHint;
                foreach (var mount in GetRcloneMountProcesses())
                {
                    var matchesDrive = string.Equals(mount.DriveLetter, drive, StringComparison.OrdinalIgnoreCase);
                    var matchesShare = !string.IsNullOrWhiteSpace(shareHint) &&
                                       mount.CommandLine.IndexOf(shareHint, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!matchesDrive && !matchesShare) continue;
                    if (mount.ProcessId > 0)
                    {
                        try
                        {
                            var process = Process.GetProcessById(mount.ProcessId);
                            process.Kill();
                            AddLog("Đã dừng rclone PID " + mount.ProcessId + " cho ổ " + drive);
                        }
                        catch (Exception ex)
                        {
                            AddLog("Không dừng được rclone PID " + mount.ProcessId + ": " + ex.Message, "WARN");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AddLog("Không quét được process rclone để ngắt ổ: " + ex.Message, "WARN");
            }
        }

        private void OpenSelectedDrive()
        {
            var p = SelectedProfile;
            if (p == null)
            {
                var external = SelectedMountedDrive;
                if (external != null)
                    OpenDriveInExplorer(external.DriveLetter);
                return;
            }
            var drive = ActiveDriveForProfile(p);
            if (IsAutoDrive(drive))
            {
                MessageBox.Show("Profile này đang chọn tự động nhưng chưa được mount.", "Trình quản lý ổ Rclone", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            OpenDriveInExplorer(drive);
        }

        private async Task MountAutoProfilesAsync()
        {
            foreach (var p in _profiles.Where(x => x.AutoMount))
                await MountProfileAsync(p);
        }

        private async Task MountDisconnectedProfilesAsync()
        {
            SaveCurrentProfile();
            var pending = _profiles.Where(p =>
                !string.IsNullOrWhiteSpace(p.Remote) &&
                !string.IsNullOrWhiteSpace(p.DriveLetter) &&
                !IsMountedProfile(p)).ToList();

            if (pending.Count == 0)
            {
                AddLog("Không có config nào cần mount.");
                return;
            }

            AddLog("Mount " + pending.Count + " config chưa kết nối...");
            foreach (var profile in pending)
                await MountProfileAsync(profile);
        }

        private void StartWebUi()
        {
            if (!File.Exists(_rcloneExe))
            {
                AddLog("Không tìm thấy rclone.exe để chạy UI web.", "ERROR");
                return;
            }

            try
            {
                if (_webUiProcess != null && !_webUiProcess.HasExited)
                {
                    AddLog("UI web đang chạy, mở lại trình duyệt.");
                    Process.Start(new ProcessStartInfo("http://127.0.0.1:5572") { UseShellExecute = true });
                    return;
                }

                var args = new List<string>
                {
                    "rcd",
                    "--rc-web-gui",
                    "--rc-addr", "127.0.0.1:5572",
                    "--rc-no-auth",
                    "--log-level", "INFO"
                };

                var psi = new ProcessStartInfo
                {
                    FileName = _rcloneExe,
                    Arguments = string.Join(" ", args.Select(QuoteIfNeeded)),
                    WorkingDirectory = _appDir,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                _webUiProcess = Process.Start(psi);
                _webUiProcess.EnableRaisingEvents = true;
                _webUiProcess.OutputDataReceived += (s, e) => { if (e.Data != null) AddLog(e.Data); };
                _webUiProcess.ErrorDataReceived += (s, e) => { if (e.Data != null) AddLog(e.Data, "WEB"); };
                _webUiProcess.Exited += (s, e) => AddLog("UI web đã dừng.");
                _webUiProcess.BeginOutputReadLine();
                _webUiProcess.BeginErrorReadLine();

                AddLog("Đã chạy Rclone Web GUI tại http://127.0.0.1:5572");
                Task.Delay(1800).ContinueWith(_ =>
                {
                    try { Process.Start(new ProcessStartInfo("http://127.0.0.1:5572") { UseShellExecute = true }); }
                    catch (Exception ex) { AddLog("Không mở được trình duyệt: " + ex.Message, "WARN"); }
                });
            }
            catch (Exception ex)
            {
                AddLog("Không chạy được UI web: " + ex.Message, "ERROR");
            }
        }

        private string SafeVolName(DriveProfile p)
        {
            var raw = ConfigName(p);
            foreach (var c in Path.GetInvalidFileNameChars()) raw = raw.Replace(c, '_');
            return raw.Replace(":", "");
        }

        private string ConfigName(DriveProfile p)
        {
            if (p != null && !string.IsNullOrWhiteSpace(p.Remote))
                return p.Remote.Trim().TrimEnd(':');
            if (p != null && !string.IsNullOrWhiteSpace(p.Name))
                return p.Name.Trim();
            return "rclone";
        }

        private string SafeVolName(DriveProfile p, string drive)
        {
            var baseName = SafeVolName(p);
            var suffix = (drive ?? "").Replace(":", "").Replace("\\", "").Trim();
            if (string.IsNullOrWhiteSpace(suffix)) return baseName;
            var volName = baseName + " " + suffix;
            return volName.Length > 32 ? volName.Substring(0, 32) : volName;
        }

        private void AddExtraArgs(List<string> args, string extra)
        {
            if (string.IsNullOrWhiteSpace(extra)) return;
            foreach (var item in SplitCommandLine(extra))
                args.Add(item);
        }

        private bool HasExtraArg(string extra, string arg)
        {
            if (string.IsNullOrWhiteSpace(extra)) return false;
            return SplitCommandLine(extra).Any(x => string.Equals(x, arg, StringComparison.OrdinalIgnoreCase));
        }

        private async Task<bool> WaitForDriveReadyAsync(string drive, int timeoutMs)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            var root = drive.TrimEnd('\\') + "\\";
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    if (Directory.Exists(root))
                        return true;
                }
                catch
                {
                }
                await Task.Delay(500);
            }
            return false;
        }

        private void OpenDriveInExplorer(string drive)
        {
            try
            {
                var root = drive.TrimEnd('\\') + "\\";
                Process.Start(new ProcessStartInfo("explorer.exe", root) { UseShellExecute = true });
                AddLog("Đã mở Explorer tại " + root);
            }
            catch (Exception ex)
            {
                AddLog("Không mở được Explorer: " + ex.Message, "WARN");
            }
        }

        private void SetDriveIcon(string drive, bool enabled)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(drive)) return;
                var letter = NormalizeDriveChoice(drive).TrimEnd(':');
                if (letter.Length != 1) return;
                var iconPath = Path.Combine(_appDir, "RcloneDriveManager", "RcloneDrive.ico");
                if (enabled && !File.Exists(iconPath))
                {
                    AddLog("Không thấy file icon ổ rclone: " + iconPath, "WARN");
                    return;
                }

                var paths = new[]
                {
                    @"Software\Microsoft\Windows\CurrentVersion\Explorer\DriveIcons\" + letter,
                    @"Software\Classes\Applications\Explorer.exe\Drives\" + letter
                };

                foreach (var path in paths)
                {
                    if (enabled)
                    {
                        using (var iconKey = Registry.CurrentUser.CreateSubKey(path + @"\DefaultIcon"))
                            iconKey.SetValue("", iconPath + ",0");
                        using (var labelKey = Registry.CurrentUser.CreateSubKey(path + @"\DefaultLabel"))
                            labelKey.SetValue("", "Rclone " + letter);
                    }
                    else
                    {
                        try { Registry.CurrentUser.DeleteSubKeyTree(path, false); } catch { }
                    }
                }
                AddLog((enabled ? "Đã đặt icon rclone cho ổ " : "Đã xóa icon rclone cho ổ ") + letter + ":");
            }
            catch (Exception ex)
            {
                AddLog("Không cập nhật được icon ổ: " + ex.Message, "WARN");
            }
        }

        private void RefreshExplorer()
        {
            try
            {
                Type shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType == null) return;
                dynamic shell = Activator.CreateInstance(shellType);
                dynamic windows = shell.Windows();
                for (int i = 0; i < windows.Count; i++)
                {
                    try { windows.Item(i).Refresh(); } catch { }
                }
            }
            catch
            {
            }
        }

        private IEnumerable<string> SplitCommandLine(string text)
        {
            var result = new List<string>();
            var current = new StringBuilder();
            var quoted = false;
            foreach (var ch in text)
            {
                if (ch == '"') { quoted = !quoted; continue; }
                if (char.IsWhiteSpace(ch) && !quoted)
                {
                    if (current.Length > 0) { result.Add(current.ToString()); current.Clear(); }
                }
                else current.Append(ch);
            }
            if (current.Length > 0) result.Add(current.ToString());
            return result;
        }

        private async Task BrowseListAsync()
        {
            browserList.Items.Clear();
            var target = RemotePath(browseRemoteCombo, browsePathBox);
            var output = await RunCaptureAsync("lsf", target);
            foreach (var line in output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
                browserList.Items.Add(new ListViewItem(line));
        }

        private async Task BrowseMkdirAsync()
        {
            var name = Prompt.Show("Folder name", "Create folder");
            if (string.IsNullOrWhiteSpace(name)) return;
            await RunCaptureAsync("mkdir", RemotePath(browseRemoteCombo, browsePathBox).TrimEnd('/') + "/" + name.Trim());
            await BrowseListAsync();
        }

        private async Task BrowseDeleteAsync()
        {
            if (browserList.SelectedItems.Count == 0) return;
            var name = browserList.SelectedItems[0].Text;
            if (MessageBox.Show("Delete " + name + " ?", "Confirm", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            await RunCaptureAsync("deletefile", RemotePath(browseRemoteCombo, browsePathBox).TrimEnd('/') + "/" + name.TrimEnd('/'));
            await BrowseListAsync();
        }

        private async Task RunTransferAsync()
        {
            var mode = Convert.ToString(transferModeCombo.SelectedItem ?? "copy");
            var args = new List<string> { mode, RemotePath(transferSourceRemoteCombo, transferSourcePathBox), RemotePath(transferDestRemoteCombo, transferDestPathBox), "--progress", "--transfers", "4" };
            if (dryRunBox.Checked) args.Add("--dry-run");
            await RunCaptureAsync(args.ToArray());
        }

        private async Task RunSimpleForSelectedAsync(string command)
        {
            var p = SelectedProfile;
            if (p == null) return;
            await RunCaptureAsync(command, p.Source);
        }

        private async Task SyncConfigUpAsync()
        {
            SaveProfiles();

            var defaultRemote = "";
            var selected = SelectedProfile;
            if (selected != null && !string.IsNullOrWhiteSpace(selected.Remote))
                defaultRemote = selected.Remote.TrimEnd(':') + ":/RcloneDriveManagerBackup";
            else if (_remotes.Count > 0)
                defaultRemote = _remotes[0].TrimEnd(':') + ":/RcloneDriveManagerBackup";

            var dest = Prompt.Show("Nhập remote/path để đồng bộ config lên", "Đồng bộ config lên", defaultRemote);
            if (string.IsNullOrWhiteSpace(dest)) return;
            dest = dest.Trim().Replace("\\", "/").TrimEnd('/');
            if (!dest.Contains(":"))
            {
                MessageBox.Show("Đích phải có dạng remote:/thu-muc, ví dụ api:/RcloneDriveManagerBackup.", "Trình quản lý ổ Rclone", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            AddLog("Đồng bộ config lên: " + dest);
            await RunCaptureAsync("mkdir", dest);

            var rcloneConfig = await GetRcloneConfigPathAsync();
            if (!string.IsNullOrWhiteSpace(rcloneConfig) && File.Exists(rcloneConfig))
            {
                await RunCaptureAsync("copyto", rcloneConfig, dest + "/rclone.conf");
            }
            else
            {
                AddLog("Không tìm thấy rclone.conf để đồng bộ.", "WARN");
            }

            if (File.Exists(_profilesFile))
            {
                await RunCaptureAsync("copyto", _profilesFile, dest + "/profiles.json");
            }
            else
            {
                AddLog("Không tìm thấy profiles.json để đồng bộ.", "WARN");
            }

            var manifest = Path.Combine(_dataDir, "sync-manifest.txt");
            Directory.CreateDirectory(_dataDir);
            File.WriteAllText(manifest,
                "Rclone Drive Manager config backup\r\n" +
                "Time: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\r\n" +
                "Machine: " + Environment.MachineName + "\r\n" +
                "User: " + Environment.UserName + "\r\n" +
                "App: " + Application.ExecutablePath + "\r\n",
                Encoding.UTF8);
            await RunCaptureAsync("copyto", manifest, dest + "/sync-manifest.txt");
            AddLog("Đã đồng bộ config lên " + dest);
        }

        private async Task<string> GetRcloneConfigPathAsync()
        {
            var output = await RunCaptureAsync("config", "file");
            foreach (var line in output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (trimmed.EndsWith(".conf", StringComparison.OrdinalIgnoreCase) && File.Exists(trimmed))
                    return trimmed;
            }
            return "";
        }

        private async Task<bool> CheckConfigConnectionAsync(bool showMessage)
        {
            var name = (configNameBox.Text ?? "").Trim();
            var type = Convert.ToString(configTypeCombo.SelectedItem ?? "").Trim();
            configCheckLabel.Text = "Checking...";

            if (!File.Exists(_rcloneExe))
            {
                configCheckLabel.Text = "rclone.exe not found";
                AddLog("Cannot add config because rclone.exe is missing.", "ERROR");
                return false;
            }
            if (!IsValidRemoteName(name))
            {
                configCheckLabel.Text = "Invalid remote name";
                AddLog("Remote name must use letters, numbers, dash, underscore or dot only.", "ERROR");
                return false;
            }
            if (_remotes.Any(r => string.Equals(r.TrimEnd(':'), name, StringComparison.OrdinalIgnoreCase)))
            {
                configCheckLabel.Text = "Remote already exists";
                AddLog("Remote already exists: " + name + ":", "ERROR");
                return false;
            }
            if (string.IsNullOrWhiteSpace(type))
            {
                configCheckLabel.Text = "Missing type";
                AddLog("Select a storage type first.", "ERROR");
                return false;
            }

            var parameters = ParseConfigParameters();
            var targetHost = ExtractConnectionHost(type, parameters);
            if (configRequireInternetBox.Checked && RequiresInternet(type))
            {
                var internetOk = await HasNetworkAsync(targetHost);
                if (!internetOk)
                {
                    configCheckLabel.Text = "Connection failed";
                    AddLog("Connection check failed before creating config.", "ERROR");
                    return false;
                }
            }

            var version = await RunCaptureAsync("version");
            if (string.IsNullOrWhiteSpace(version))
            {
                configCheckLabel.Text = "rclone check failed";
                return false;
            }

            configCheckLabel.Text = "Connection OK";
            if (showMessage) AddLog("Connection check OK. Config can be created.");
            return true;
        }

        private async Task AddConfigFromUiAsync()
        {
            if (!await CheckConfigConnectionAsync(false)) return;

            var name = (configNameBox.Text ?? "").Trim();
            var type = Convert.ToString(configTypeCombo.SelectedItem ?? "").Trim();
            var args = new List<string> { "--non-interactive", "config", "create", name, type };
            var parameters = ParseConfigParameters();
            if (!string.IsNullOrWhiteSpace(configUserBox.Text))
                parameters["user"] = configUserBox.Text.Trim();
            if (!string.IsNullOrEmpty(configPassBox.Text))
            {
                parameters["pass"] = configObscurePassBox.Checked
                    ? await ObscurePasswordAsync(configPassBox.Text)
                    : configPassBox.Text;
            }

            foreach (var pair in parameters)
            {
                if (!string.IsNullOrWhiteSpace(pair.Value))
                {
                    args.Add(pair.Key);
                    args.Add(pair.Value);
                }
            }

            AddLog("Creating config " + name + ": type " + type);
            var output = await RunCaptureSensitiveAsync(args.ToArray(), "rclone --non-interactive config create " + name + " " + type + " ... pass=<ẩn>");
            await RefreshRemotesAsync();

            if (!_remotes.Any(r => string.Equals(r.TrimEnd(':'), name, StringComparison.OrdinalIgnoreCase)))
            {
                configCheckLabel.Text = "Create failed";
                AddLog("Config was not added. rclone output: " + output, "ERROR");
                return;
            }

            var testPath = DriveProfile.NormalizeRemotePath(configTestPathBox.Text, name + ":");
            AddLog("Testing new remote " + name + ":" + testPath);
            var test = await RunCaptureAsync("lsd", name + ":" + testPath);
            configCheckLabel.Text = test.IndexOf("Failed", StringComparison.OrdinalIgnoreCase) >= 0 ? "Created, test warning" : "Created OK";

            var profile = new DriveProfile
            {
                Name = "Drive " + name,
                Remote = name + ":",
                RemotePath = testPath,
                DriveLetter = GetFreeDriveLetters().FirstOrDefault() ?? "Z:"
            };
            _profiles.Add(profile);
            SaveProfiles();
            RenderProfiles();
            SelectProfile(profile);
            AddLog("Added config and profile: " + name + ":");
        }

        private Dictionary<string, string> ParseConfigParameters()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rawLine in (configParamsBox.Text ?? "").Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                var at = line.IndexOf('=');
                if (at <= 0)
                {
                    AddLog("Ignored invalid config line: " + line, "WARN");
                    continue;
                }
                var key = line.Substring(0, at).Trim();
                var value = line.Substring(at + 1).Trim();
                if (key.Length > 0) result[key] = value;
            }
            return result;
        }

        private async Task<string> ObscurePasswordAsync(string password)
        {
            if (string.IsNullOrEmpty(password)) return "";
            var result = await RunRcloneNoLogAsync("obscure", password);
            var value = result.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(value))
            {
                AddLog("Không mã hóa được password bằng rclone obscure.", "WARN");
                return password;
            }
            AddLog("Đã mã hóa password bằng rclone obscure.");
            return value.Trim();
        }

        private bool IsValidRemoteName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            return name.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.');
        }

        private bool RequiresInternet(string type)
        {
            return !string.Equals(type, "local", StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(type, "alias", StringComparison.OrdinalIgnoreCase);
        }

        private string ExtractConnectionHost(string type, Dictionary<string, string> parameters)
        {
            string value;
            if (parameters.TryGetValue("host", out value) && !string.IsNullOrWhiteSpace(value)) return value;
            if (parameters.TryGetValue("server", out value) && !string.IsNullOrWhiteSpace(value)) return value;
            if (parameters.TryGetValue("endpoint", out value) && !string.IsNullOrWhiteSpace(value)) return HostFromUrl(value);
            if (parameters.TryGetValue("url", out value) && !string.IsNullOrWhiteSpace(value)) return HostFromUrl(value);
            if (string.Equals(type, "drive", StringComparison.OrdinalIgnoreCase)) return "www.googleapis.com";
            if (string.Equals(type, "onedrive", StringComparison.OrdinalIgnoreCase)) return "graph.microsoft.com";
            if (string.Equals(type, "dropbox", StringComparison.OrdinalIgnoreCase)) return "api.dropboxapi.com";
            if (string.Equals(type, "box", StringComparison.OrdinalIgnoreCase)) return "api.box.com";
            if (string.Equals(type, "mega", StringComparison.OrdinalIgnoreCase)) return "mega.nz";
            return "1.1.1.1";
        }

        private string HostFromUrl(string value)
        {
            Uri uri;
            if (Uri.TryCreate(value, UriKind.Absolute, out uri)) return uri.Host;
            return value.Replace("https://", "").Replace("http://", "").Split('/')[0];
        }

        private Task<bool> HasNetworkAsync(string host)
        {
            return Task.Run(() =>
            {
                try
                {
                    using (var ping = new Ping())
                    {
                        var target = string.IsNullOrWhiteSpace(host) ? "1.1.1.1" : host;
                        var reply = ping.Send(target, 2500);
                        AddLog("Ping " + target + ": " + reply.Status);
                        return reply.Status == IPStatus.Success;
                    }
                }
                catch (Exception ex)
                {
                    AddLog("Ping failed: " + ex.Message, "WARN");
                    return false;
                }
            });
        }

        private string RemotePath(ComboBox remote, TextBox path)
        {
            var r = Convert.ToString(remote.SelectedItem ?? "");
            var p = DriveProfile.NormalizeRemotePath(path.Text, r);
            return DriveProfile.BuildSource(r, p);
        }

        private Task<string> RunCaptureAsync(params string[] args)
        {
            return RunCaptureInternalAsync(args, null);
        }

        private Task<string> RunCaptureSensitiveAsync(string[] args, string safeLogLine)
        {
            return RunCaptureInternalAsync(args, safeLogLine);
        }

        private Task<string> RunRcloneNoLogAsync(params string[] args)
        {
            return Task.Run(() =>
            {
                try
                {
                    if (!File.Exists(_rcloneExe)) return "";
                    var psi = new ProcessStartInfo
                    {
                        FileName = _rcloneExe,
                        Arguments = string.Join(" ", args.Select(QuoteIfNeeded)),
                        WorkingDirectory = _appDir,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        RedirectStandardInput = true,
                        CreateNoWindow = true
                    };
                    using (var proc = Process.Start(psi))
                    {
                        proc.StandardInput.Close();
                        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
                        var stderrTask = proc.StandardError.ReadToEndAsync();
                        if (!proc.WaitForExit(30000))
                        {
                            try { proc.Kill(); } catch { }
                            return "";
                        }
                        Task.WaitAll(stdoutTask, stderrTask);
                        return stdoutTask.Result + stderrTask.Result;
                    }
                }
                catch
                {
                    return "";
                }
            });
        }

        private Task<RcloneResult> RunRcloneResultAsync(int timeoutMs, string safeLogLine, params string[] args)
        {
            return Task.Run(() =>
            {
                var result = new RcloneResult { ExitCode = -1, Output = "", TimedOut = false };
                try
                {
                    if (!File.Exists(_rcloneExe))
                    {
                        AddLog("rclone.exe not found: " + _rcloneExe, "ERROR");
                        return result;
                    }

                    AddLog(safeLogLine ?? ("rclone " + string.Join(" ", args.Select(QuoteIfNeeded))));
                    var psi = new ProcessStartInfo
                    {
                        FileName = _rcloneExe,
                        Arguments = string.Join(" ", args.Select(QuoteIfNeeded)),
                        WorkingDirectory = _appDir,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        RedirectStandardInput = true,
                        CreateNoWindow = true
                    };

                    using (var proc = Process.Start(psi))
                    {
                        proc.StandardInput.Close();
                        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
                        var stderrTask = proc.StandardError.ReadToEndAsync();
                        if (!proc.WaitForExit(timeoutMs))
                        {
                            result.TimedOut = true;
                            try { proc.Kill(); } catch { }
                            AddLog("Command timed out after " + timeoutMs + " ms.", "ERROR");
                            return result;
                        }

                        Task.WaitAll(stdoutTask, stderrTask);
                        var stdout = stdoutTask.Result;
                        var stderr = stderrTask.Result;
                        result.ExitCode = proc.ExitCode;
                        result.Output = stdout + stderr;
                        if (!string.IsNullOrWhiteSpace(stdout)) AddLog(stdout.Trim());
                        if (!string.IsNullOrWhiteSpace(stderr)) AddLog(stderr.Trim(), proc.ExitCode == 0 ? "INFO" : "WARN");
                        return result;
                    }
                }
                catch (Exception ex)
                {
                    AddLog(ex.Message, "ERROR");
                    result.Output = ex.Message;
                    return result;
                }
            });
        }

        private Task<string> RunCaptureInternalAsync(string[] args, string safeLogLine)
        {
            return Task.Run(() =>
            {
                try
                {
                    if (!File.Exists(_rcloneExe))
                    {
                        AddLog("rclone.exe not found: " + _rcloneExe, "ERROR");
                        return "";
                    }
                    AddLog(safeLogLine ?? ("rclone " + string.Join(" ", args.Select(QuoteIfNeeded))));
                    var psi = new ProcessStartInfo
                    {
                        FileName = _rcloneExe,
                        Arguments = string.Join(" ", args.Select(QuoteIfNeeded)),
                        WorkingDirectory = _appDir,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        RedirectStandardInput = true,
                        CreateNoWindow = true
                    };
                    using (var proc = Process.Start(psi))
                    {
                        proc.StandardInput.Close();
                        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
                        var stderrTask = proc.StandardError.ReadToEndAsync();
                        if (!proc.WaitForExit(120000))
                        {
                            try { proc.Kill(); } catch { }
                            AddLog("Command timed out after 120 seconds.", "ERROR");
                            return "";
                        }
                        Task.WaitAll(stdoutTask, stderrTask);
                        var stdout = stdoutTask.Result;
                        var stderr = stderrTask.Result;
                        if (!string.IsNullOrWhiteSpace(stdout)) AddLog(stdout.Trim());
                        if (!string.IsNullOrWhiteSpace(stderr)) AddLog(stderr.Trim(), proc.ExitCode == 0 ? "INFO" : "WARN");
                        return stdout + stderr;
                    }
                }
                catch (Exception ex)
                {
                    AddLog(ex.Message, "ERROR");
                    return "";
                }
            });
        }

        private Process StartRclone(List<string> args, bool shell)
        {
            var psi = new ProcessStartInfo
            {
                FileName = _rcloneExe,
                Arguments = string.Join(" ", args.Select(QuoteIfNeeded)),
                WorkingDirectory = _appDir,
                UseShellExecute = shell,
                RedirectStandardOutput = !shell,
                RedirectStandardError = !shell,
                CreateNoWindow = !shell
            };
            return Process.Start(psi);
        }

        private string QuoteIfNeeded(string value)
        {
            if (value == null) return "\"\"";
            if (value.Length == 0 || value.Any(char.IsWhiteSpace) || value.Contains("\""))
                return "\"" + value.Replace("\"", "\\\"") + "\"";
            return value;
        }

        private void RunHidden(string file, string args)
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = file, Arguments = args, CreateNoWindow = true, UseShellExecute = false });
            }
            catch (Exception ex)
            {
                AddLog(ex.Message, "WARN");
            }
        }

        private void OpenConfig()
        {
            if (!File.Exists(_rcloneExe)) return;
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/k \"" + _rcloneExe + "\" config",
                WorkingDirectory = _appDir,
                UseShellExecute = true
            });
        }

        private void SetStartup(bool enabled)
        {
            try
            {
                var exe = Application.ExecutablePath;
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (enabled)
                        key.SetValue("RcloneDriveManager", "\"" + exe + "\" --automount");
                    else
                        key.DeleteValue("RcloneDriveManager", false);
                }
                AddLog(enabled ? "Startup enabled." : "Startup disabled.");
            }
            catch (Exception ex)
            {
                AddLog("Startup change failed: " + ex.Message, "ERROR");
            }
        }

        private void CreateBatForSelected()
        {
            SaveCurrentProfile();
            var p = SelectedProfile;
            if (p == null) return;
            var file = Path.Combine(_appDir, "mount-" + SafeVolName(p) + ".bat");
            if (IsAutoDrive(p.DriveLetter))
            {
                var lines = new List<string>
                {
                    "@echo off",
                    "setlocal EnableDelayedExpansion",
                    "cd /d \"%~dp0\"",
                    "set \"DRIVE=\"",
                    "for %%L in (Z Y X W V U T S R Q P O N M L K J I H G F E D C) do (",
                    "  if not exist %%L:\\ (set \"DRIVE=%%L:\" & goto found)",
                    ")",
                    "echo Khong tim thay ky tu o dia trong.",
                    "pause",
                    "exit /b 1",
                    ":found"
                };
                var args = BuildMountArgs(p, "%DRIVE%", SafeVolName(p) + " %DRIVE:~0,1%");
                lines.Add("\"%~dp0rclone.exe\" " + string.Join(" ", args.Select(QuoteIfNeeded)));
                lines.Add("pause");
                File.WriteAllText(file, string.Join("\r\n", lines) + "\r\n", Encoding.ASCII);
            }
            else
            {
                var drive = NormalizeDriveChoice(p.DriveLetter);
                var args = BuildMountArgs(p, drive, SafeVolName(p, drive));
                File.WriteAllText(file, "@echo off\r\ncd /d \"%~dp0\"\r\n\"%~dp0rclone.exe\" " + string.Join(" ", args.Select(QuoteIfNeeded)) + "\r\npause\r\n", Encoding.ASCII);
            }
            AddLog("Created " + file);
        }

        private void CreateUnmountBatForSelected()
        {
            SaveCurrentProfile();
            var p = SelectedProfile;
            if (p == null) return;
            var file = Path.Combine(_appDir, "unmount-" + SafeVolName(p) + ".bat");
            var lines = new List<string>
            {
                "@echo off",
                "setlocal EnableDelayedExpansion",
                "cd /d \"%~dp0\""
            };
            if (IsAutoDrive(p.DriveLetter))
            {
                lines.Add("set /p DRIVE=Nhap ky tu o can ngat, vi du X: ");
            }
            else
            {
                lines.Add("set \"DRIVE=" + NormalizeDriveChoice(p.DriveLetter) + "\"");
            }
            lines.Add("if \"%DRIVE:~-1%\" NEQ \":\" set \"DRIVE=%DRIVE%:\"");
            lines.Add("echo Dang ngat %DRIVE% ...");
            lines.Add("powershell -NoProfile -ExecutionPolicy Bypass -Command \"$d=$env:DRIVE; Get-CimInstance Win32_Process -Filter \\\"Name='rclone.exe'\\\" | Where-Object { $_.CommandLine -match ' mount ' -and $_.CommandLine -like ('* ' + $d + '*') } | ForEach-Object { Write-Host ('Stop rclone PID ' + $_.ProcessId); Stop-Process -Id $_.ProcessId -Force }\"");
            lines.Add("net use %DRIVE% /delete /y");
            lines.Add("timeout /t 2 /nobreak >nul");
            lines.Add("if exist %DRIVE%\\ (");
            lines.Add("  echo Van con thay o %DRIVE%. Hay chay file nay cung quyen voi luc mount hoac mo Task Manager kill rclone.exe mount.");
            lines.Add(") else (");
            lines.Add("  echo Da ngat %DRIVE%.");
            lines.Add(")");
            lines.Add("pause");
            File.WriteAllText(file, string.Join("\r\n", lines) + "\r\n", Encoding.ASCII);
            AddLog("Created " + file);
        }
    }

    internal static class Prompt
    {
        public static string Show(string text, string caption)
        {
            return Show(text, caption, "");
        }

        public static string Show(string text, string caption, string defaultValue)
        {
            using (var form = new Form())
            using (var label = new Label())
            using (var input = new TextBox())
            using (var ok = new Button())
            using (var cancel = new Button())
            {
                form.Text = caption;
                form.Width = 420;
                form.Height = 150;
                form.StartPosition = FormStartPosition.CenterParent;
                label.Text = text;
                label.Left = 12;
                label.Top = 12;
                label.Width = 370;
                input.Left = 12;
                input.Top = 38;
                input.Width = 380;
                input.Text = defaultValue ?? "";
                ok.Text = "OK";
                ok.Left = 216;
                ok.Top = 74;
                ok.DialogResult = DialogResult.OK;
                cancel.Text = "Cancel";
                cancel.Left = 300;
                cancel.Top = 74;
                cancel.DialogResult = DialogResult.Cancel;
                form.Controls.AddRange(new Control[] { label, input, ok, cancel });
                form.AcceptButton = ok;
                form.CancelButton = cancel;
                return form.ShowDialog() == DialogResult.OK ? input.Text : "";
            }
        }
    }
}

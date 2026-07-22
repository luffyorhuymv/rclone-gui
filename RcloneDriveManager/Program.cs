using Microsoft.Win32;
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace RcloneDriveManager
{
    internal static class Program
    {
        private static Mutex _singleInstanceMutex;

        [STAThread]
        private static void Main(string[] args)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            bool createdNew;
            _singleInstanceMutex = new Mutex(true, "RcloneDriveManager.SingleInstance", out createdNew);
            if (!createdNew)
            {
                MessageBox.Show("RcloneDrive đang mở rồi.", "RcloneDrive", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(args));
            try { _singleInstanceMutex.ReleaseMutex(); } catch { }
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
        public string LocalWorkDir { get; set; }
        public string VfsCacheMaxAge { get; set; }
        public string VfsWriteBack { get; set; }
        public bool ReadOnly { get; set; }
        public bool AutoMount { get; set; }
        public bool RestoreOnStartup { get; set; }
        public bool NetworkMode { get; set; }
        public int Transfers { get; set; }
        public int BufferSizeMb { get; set; }
        public string MountPreset { get; set; }
        public string ExtraArgs { get; set; }
        public bool TunnelEnabled { get; set; }
        public string TunnelHostname { get; set; }
        public int TunnelLocalPort { get; set; }
        public string TunnelCommand { get; set; }
        public bool CodeWorkspaceEnabled { get; set; }
        public int CodeWorkspaceDelaySeconds { get; set; }
        public bool CodeWorkspaceSkipNewerRemote { get; set; }
        public string CodeWorkspaceIgnores { get; set; }

        public DriveProfile()
        {
            Id = Guid.NewGuid().ToString("N");
            Name = "New Drive";
            Remote = "";
            RemotePath = "/";
            DriveLetter = "Z:";
            CacheMode = "full";
            CacheDir = "%USERPROFILE%\\.cache\\rclone";
            LocalWorkDir = "";
            VfsCacheMaxAge = "72h";
            VfsWriteBack = "5s";
            ReadOnly = false;
            AutoMount = false;
            RestoreOnStartup = false;
            NetworkMode = true;
            Transfers = 4;
            BufferSizeMb = 32;
            MountPreset = "Nhanh/RaiDrive";
            ExtraArgs = "";
            TunnelEnabled = false;
            TunnelHostname = "";
            TunnelLocalPort = 0;
            TunnelCommand = "";
            CodeWorkspaceEnabled = false;
            CodeWorkspaceDelaySeconds = 2;
            CodeWorkspaceSkipNewerRemote = true;
            CodeWorkspaceIgnores = DefaultCodeWorkspaceIgnores;
        }

        public const string DefaultCodeWorkspaceIgnores = ".git/**;node_modules/**;vendor/**;cache/**;tmp/**;.env;.user.ini;.ftpquota;.well-known/**;*.tmp;*.swp;~$*";

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

            var pathWasNetworkShare = path.StartsWith("//", StringComparison.Ordinal);
            path = StripWindowsNetworkHost(path);

            while (path.StartsWith("//", StringComparison.Ordinal))
                path = path.Substring(1);

            while (path.Contains("//"))
                path = path.Replace("//", "/");

            var strippedPseudoHost = false;
            if (path.StartsWith("/server/", StringComparison.OrdinalIgnoreCase))
            {
                path = path.Substring("/server".Length);
                strippedPseudoHost = true;
            }
            if (path.StartsWith("/localhost/", StringComparison.OrdinalIgnoreCase))
            {
                path = path.Substring("/localhost".Length);
                strippedPseudoHost = true;
            }

            if ((pathWasNetworkShare || strippedPseudoHost) && path.StartsWith("/", StringComparison.Ordinal) && path.Length > 1)
                path = path.Substring(1);

            if (path.Length == 0) path = "/";
            return path;
        }

        private static string StripWindowsNetworkHost(string path)
        {
            if (!path.StartsWith("//", StringComparison.Ordinal)) return path;
            var withoutSlashes = path.TrimStart('/');
            var slash = withoutSlashes.IndexOf('/');
            if (slash < 0) return "/";
            return withoutSlashes.Substring(slash);
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

    public sealed class AppUpdateInfo
    {
        public string Version { get; set; }
        public string DownloadUrl { get; set; }
        public string PageUrl { get; set; }
    }

    public sealed class WorkspaceWatchState
    {
        public string ProfileId { get; set; }
        public string ProfileName { get; set; }
        public string LocalDir { get; set; }
        public FileSystemWatcher Watcher { get; set; }
        public readonly Dictionary<string, System.Threading.Timer> Timers = new Dictionary<string, System.Threading.Timer>(StringComparer.OrdinalIgnoreCase);
        public readonly object SyncRoot = new object();
    }

    public sealed class CapacityInfo
    {
        public string Text { get; set; }
        public double? UsedRatio { get; set; }
        public DateTime ExpiresAtUtc { get; set; }
    }

    public sealed class RcloneFileItem
    {
        public string Path { get; set; }
        public string Name { get; set; }
        public long Size { get; set; }
        public string MimeType { get; set; }
        public string ModTime { get; set; }
        public bool IsDir { get; set; }
    }

    public sealed class RcloneMountProcessInfo
    {
        public int ProcessId { get; set; }
        public string CommandLine { get; set; }
        public string DriveLetter { get; set; }
        public string Source { get; set; }
        public string VolumeName { get; set; }
    }
    public sealed class FlatTabControl : TabControl
    {
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        public FlatTabControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            DoubleBuffered = true;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x1328 && !DesignMode) // TCM_ADJUSTRECT
            {
                var rect = new RECT();
                rect.Left = 0;
                rect.Top = ItemSize.Height + 4;
                rect.Right = Width;
                rect.Bottom = Height;
                System.Runtime.InteropServices.Marshal.StructureToPtr(rect, m.LParam, true);
                m.Result = (IntPtr)1;
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var bgColor = Parent?.BackColor ?? BackColor;
            using (var brush = new SolidBrush(bgColor))
                e.Graphics.FillRectangle(brush, ClientRectangle);

            if (SelectedTab != null)
            {
                var dispRect = DisplayRectangle;
                using (var pageBrush = new SolidBrush(SelectedTab.BackColor))
                    e.Graphics.FillRectangle(pageBrush, dispRect);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            // Manually paint tab headers since UserPaint suppresses DrawItem events
            if (TabCount == 0) return;
            for (int i = 0; i < TabCount; i++)
            {
                var tabRect = GetTabRect(i);
                var args = new DrawItemEventArgs(e.Graphics, Font, tabRect, i,
                    i == SelectedIndex ? DrawItemState.Selected : DrawItemState.Default);
                OnDrawItem(args);
            }
        }
    }

    public sealed class MainForm : Form
    {
        private const string AppUpdateCommitApiUrl = "https://api.github.com/repos/luffyorhuymv/rclone-gui/commits/main";
        private const string AppUpdateReleaseApiUrl = "https://api.github.com/repos/luffyorhuymv/rclone-gui/releases/latest";
        private const string AppVersion = "1.0.63";
        private const int MaxLogLines = 2000;
        private readonly string[] _args;
        private readonly string _appDir;
        private readonly string _rcloneExe;
        private readonly string _dataDir;
        private readonly string _profilesFile;
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer();
        private readonly List<DriveProfile> _profiles = new List<DriveProfile>();
        private readonly Dictionary<string, Process> _mounts = new Dictionary<string, Process>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Process> _tunnels = new Dictionary<string, Process>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<DriveProfile, string> _activeDrives = new Dictionary<DriveProfile, string>();
        private readonly List<string> _remotes = new List<string>();
        private readonly List<MountedDriveInfo> _mountedExternalDrives = new List<MountedDriveInfo>();
        private readonly Dictionary<string, MountedDriveInfo> _detectedRcloneDrives = new Dictionary<string, MountedDriveInfo>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, CapacityInfo> _capacityCache = new Dictionary<string, CapacityInfo>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _capacityRefreshPending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly object _capacityLock = new object();
        private readonly Dictionary<string, WorkspaceWatchState> _workspaceWatchers = new Dictionary<string, WorkspaceWatchState>(StringComparer.OrdinalIgnoreCase);
        private readonly List<Tuple<string, string, string>> _pendingLogLines = new List<Tuple<string, string, string>>();
        private Process _webUiProcess;
        private readonly Color _bg = Color.FromArgb(204, 209, 216);
        private readonly Color _surface = Color.FromArgb(218, 222, 228);
        private readonly Color _line = Color.FromArgb(178, 186, 197);
        private readonly Color _text = Color.FromArgb(25, 28, 29);
        private readonly Color _muted = Color.FromArgb(66, 71, 84);
        private readonly Color _primary = Color.FromArgb(33, 112, 228);
        private readonly Color _danger = Color.FromArgb(239, 68, 68);
        private readonly Color _success = Color.FromArgb(16, 185, 129);

        private ListView profileList;
        private Button headerConnectButton;
        private Button driveConnectButton;
        private FlatTabControl mainTabs;
        private ComboBox remoteCombo;
        private ComboBox driveCombo;
        private ComboBox cacheModeCombo;
        private ComboBox mountPresetCombo;
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
        private CheckBox tunnelEnabledBox;
        private CheckBox codeWorkspaceAutoUploadBox;
        private CheckBox codeWorkspaceSkipNewerRemoteBox;
        private NumericUpDown tunnelPortBox;
        private NumericUpDown codeWorkspaceDelayBox;
        private TextBox tunnelCommandBox;
        private TextBox codeWorkspaceIgnoreBox;
        private RichTextBox logBox;
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
        private Label versionLabel;
        private TextBox configNameBox;
        private ComboBox configTypeCombo;
        private TextBox configParamsBox;
        private TextBox configTestPathBox;
        private TextBox configUserBox;
        private TextBox configPassBox;
        private CheckBox configObscurePassBox;
        private CheckBox configRequireInternetBox;
        private Label configCheckLabel;
        private Label codeWorkspaceStatusLabel;
        private Label liveLogLabel;
        private ToolTip driveActionTip;
        private string lastDriveActionTipText;
        private bool _loadingProfileFields;
        private bool _profileNameEditedByUser;
        private bool _changingProfileNameAutomatically;
        private bool _newProfileDraft;
        private bool _connectionBusy;
        private string _connectionBusyText = "Đang kết nối";
        private int _connectionBusyStep;
        private System.Windows.Forms.Timer _connectionBusyTimer;
        private NotifyIcon _trayIcon;
        private ContextMenuStrip _trayMenu;
        private bool _allowExit;

        public MainForm(string[] args)
        {
            _args = args ?? new string[0];
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            DoubleBuffered = true;
            _appDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            _rcloneExe = Path.Combine(_appDir, "rclone.exe");
            _dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RcloneDriveManager");
            _profilesFile = Path.Combine(_dataDir, "profiles.json");

            Text = "Trình quản lý ổ Rclone";
            Width = 1200;
            Height = 720;
            MinimumSize = new Size(1100, 680);
            StartPosition = FormStartPosition.Manual;
            Font = new Font("Segoe UI", 9F);
            var iconPath = Path.Combine(_appDir, "RcloneDriveManager", "RcloneDrive.ico");
            if (File.Exists(iconPath))
                Icon = new Icon(iconPath);
            SetupTrayIcon();

            BuildUi();
            Shown += (s, e) =>
            {
                CenterOnActiveScreen();
                BeginInvoke(new Action(() =>
                {
                    Invalidate(true);
                    Update();
                }));
            };
            Load += async (s, e) =>
            {
                LoadProfiles();
                RefreshDriveLetters();
                await EnsureRcloneAvailableAsync();
                RunStartupDiagnostics();
                if (File.Exists(_rcloneExe))
                    await RefreshRemotesAsync();
                SelectFirstProfile();
                _ = CheckForAppUpdateOnStartupAsync();
                if (_args.Any(a => string.Equals(a, "--automount", StringComparison.OrdinalIgnoreCase)))
                    await MountAutoProfilesAsync(true);
                else
                    await MountAutoProfilesAsync(false);
                StartSavedCodeWorkspaceWatchers();
            };
            FormClosing += (s, e) =>
            {
                SaveProfiles();
                if (!_allowExit && e.CloseReason == CloseReason.UserClosing)
                {
                    var choice = MessageBox.Show(
                        "Bạn muốn ẩn app xuống khay hệ thống để tiếp tục giữ các ổ mount?\n\nYes: Ẩn xuống tray\nNo: Thoát app\nCancel: Hủy",
                        "RcloneDrive",
                        MessageBoxButtons.YesNoCancel,
                        MessageBoxIcon.Question);

                    if (choice == DialogResult.Yes)
                    {
                        e.Cancel = true;
                        HideToTray();
                    }
                    else if (choice == DialogResult.Cancel)
                    {
                        e.Cancel = true;
                    }
                    else
                    {
                        _allowExit = true;
                        if (_trayIcon != null)
                            _trayIcon.Visible = false;
                    }
                }
            };
        }

        private void SetupTrayIcon()
        {
            _trayMenu = new ContextMenuStrip();
            _trayMenu.Items.Add("Mở app", null, (s, e) => RestoreFromTray());
            _trayMenu.Items.Add("Mount lại ổ tự động", null, async (s, e) =>
            {
                RestoreFromTray();
                await MountAutoProfilesAsync(true);
            });
            _trayMenu.Items.Add(new ToolStripSeparator());
            _trayMenu.Items.Add("Thoát", null, (s, e) => ExitFromTray());

            _trayIcon = new NotifyIcon
            {
                Text = "RcloneDrive v" + AppVersion,
                Icon = Icon ?? SystemIcons.Application,
                ContextMenuStrip = _trayMenu,
                Visible = true
            };
            _trayIcon.DoubleClick += (s, e) => RestoreFromTray();
        }

        private void HideToTray()
        {
            ShowInTaskbar = false;
            Hide();
            if (_trayIcon != null)
                _trayIcon.Visible = true;
        }

        private void RestoreFromTray()
        {
            Show();
            ShowInTaskbar = true;
            WindowState = FormWindowState.Normal;
            Activate();
        }

        private void ExitFromTray()
        {
            _allowExit = true;
            if (_trayIcon != null)
                _trayIcon.Visible = false;
            Close();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_trayIcon != null)
                {
                    _trayIcon.Visible = false;
                    _trayIcon.Dispose();
                    _trayIcon = null;
                }
                if (_trayMenu != null)
                {
                    _trayMenu.Dispose();
                    _trayMenu = null;
                }
                StopAllCodeWorkspaceWatchers();
            }
            base.Dispose(disposing);
        }

        private void BuildUi()
        {
            BackColor = _bg;
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, BackColor = _bg };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 140));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 430));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Controls.Add(root);

            var header = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = _surface, Padding = new Padding(14, 6, 14, 6), ColumnCount = 2, RowCount = 1 };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 480));
            root.Controls.Add(header, 0, 0);
            root.SetColumnSpan(header, 2);

            var titleBlock = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, BackColor = _surface };
            titleBlock.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            titleBlock.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));
            titleBlock.Controls.Add(new Label { Text = "RcloneDrive", Dock = DockStyle.Fill, Font = new Font("Segoe UI", 12F, FontStyle.Bold), ForeColor = _text, TextAlign = ContentAlignment.BottomLeft }, 0, 0);
            versionLabel = new Label { Text = "v" + AppVersion, Dock = DockStyle.Fill, Font = new Font("Segoe UI", 8F), ForeColor = _muted, TextAlign = ContentAlignment.TopLeft };
            titleBlock.Controls.Add(versionLabel, 0, 1);
            header.Controls.Add(titleBlock, 0, 0);

            var headerActions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, BackColor = _surface, Padding = new Padding(0, 4, 0, 0) };
            headerActions.Controls.Add(ActionButton("Web UI", (s, e) => StartWebUi(), _surface, _text, 92));
            headerActions.Controls.Add(ActionButton("Làm mới", async (s, e) => await RefreshAllAsync(), _surface, _text, 104));
            headerActions.Controls.Add(ActionButton("Ngắt", (s, e) => UnmountSelected(), _surface, _danger, 86));
            headerConnectButton = ActionButton("Kết nối", async (s, e) => await ToggleSelectedConnectionAsync(), _primary, Color.White, 122);
            headerActions.Controls.Add(headerConnectButton);
            header.Controls.Add(headerActions, 1, 0);

            var left = new Panel { Dock = DockStyle.Fill, BackColor = _bg, Padding = new Padding(12, 12, 10, 12) };
            root.Controls.Add(left, 0, 1);
            root.SetRowSpan(left, 2);

            var leftLayout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1 };
            leftLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            leftLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            leftLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 122));
            left.Controls.Add(leftLayout);
            var driveListTitle = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 1, ColumnCount = 1, BackColor = _bg };
            driveListTitle.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            driveListTitle.Controls.Add(new Label { Text = "Ổ đã cấu hình", Font = new Font("Segoe UI", 10F, FontStyle.Bold), ForeColor = _text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
            leftLayout.Controls.Add(driveListTitle, 0, 0);

            profileList = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                HideSelection = false,
                BorderStyle = BorderStyle.None,
                BackColor = _bg,
                ForeColor = _text,
                Font = new Font("Segoe UI", 9F),
                OwnerDraw = true,
                HeaderStyle = ColumnHeaderStyle.None,
                ShowItemToolTips = true,
                SmallImageList = new ImageList { ImageSize = new Size(1, 74) }
            };
            profileList.Columns.Add("Ổ", 408);
            profileList.Resize += (s, e) => { if (profileList.Columns.Count > 0) profileList.Columns[0].Width = profileList.ClientSize.Width; };
            profileList.DrawColumnHeader += (s, e) => e.DrawDefault = false;
            profileList.DrawItem += DrawDriveListItem;
            profileList.DrawSubItem += DrawDriveListSubItem;
            profileList.MouseClick += async (s, e) => await HandleDriveListMouseClickAsync(e);
            profileList.MouseMove += HandleDriveListMouseMove;
            profileList.MouseLeave += (s, e) =>
            {
                profileList.Cursor = Cursors.Default;
                lastDriveActionTipText = null;
                if (driveActionTip != null)
                    driveActionTip.SetToolTip(profileList, "");
            };
            driveActionTip = new ToolTip { AutomaticDelay = 250, ReshowDelay = 80, ShowAlways = true };
            profileList.SelectedIndexChanged += (s, e) =>
            {
                LoadSelectedProfileIntoFields();
                UpdateConnectButtonState();
            };
            leftLayout.Controls.Add(profileList, 0, 1);

            var leftActions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Padding = new Padding(0, 4, 0, 0), BackColor = _bg };
            leftActions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            leftActions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            leftActions.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            leftActions.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            leftActions.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            leftActions.Controls.Add(ActionButton("Mới", (s, e) => NewProfile(), _surface, _text, 142), 0, 0);
            leftActions.Controls.Add(ActionButton("Lưu", (s, e) => SaveCurrentProfile(), _primary, Color.White, 142), 1, 0);
            leftActions.Controls.Add(ActionButton("Cài đặt", (s, e) => OpenDriveSettingsDialog(), _surface, _text, 142), 0, 1);
            leftActions.Controls.Add(ActionButton("Xóa", (s, e) => DeleteCurrentProfile(), _surface, _danger, 142), 1, 1);
            statusLabel = new Label
            {
                Text = "Sẵn sàng",
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(238, 241, 245),
                ForeColor = _muted,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Segoe UI", 8.2F, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(8, 0, 8, 0),
                AutoEllipsis = true
            };
            leftActions.Controls.Add(statusLabel, 0, 2);
            leftActions.SetColumnSpan(statusLabel, 2);
            leftLayout.Controls.Add(leftActions, 0, 2);

            mainTabs = new FlatTabControl
            {
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                Padding = new Point(14, 6),
                DrawMode = TabDrawMode.OwnerDrawFixed,
                SizeMode = TabSizeMode.Fixed,
                ItemSize = new Size(160, 30)
            };
            mainTabs.DrawItem += DrawMainTab;
            root.Controls.Add(mainTabs, 1, 1);
            mainTabs.TabPages.Add(BuildDriveTab());
            mainTabs.TabPages.Add(BuildBrowseTransferTab());
            mainTabs.TabPages.Add(BuildConfigToolsTab());

            var logPanel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, BackColor = Color.FromArgb(15, 23, 42), Padding = new Padding(8) };
            logPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            logPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            logPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            liveLogLabel = new Label
            {
                Text = "Log sẵn sàng",
                Dock = DockStyle.Fill,
                AutoEllipsis = true,
                ForeColor = Color.FromArgb(125, 211, 252),
                BackColor = Color.FromArgb(30, 41, 59),
                Font = new Font("Segoe UI", 8.5F, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(8, 0, 8, 0)
            };
            logPanel.Controls.Add(liveLogLabel, 0, 0);
            logBox = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                ScrollBars = RichTextBoxScrollBars.ForcedVertical,
                BackColor = Color.FromArgb(15, 23, 42),
                ForeColor = Color.FromArgb(203, 213, 225),
                Font = new Font("Consolas", 9F),
                BorderStyle = BorderStyle.None,
                WordWrap = true,
                DetectUrls = false,
                HideSelection = false
            };
            logPanel.Controls.Add(logBox, 0, 1);
            var logActions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, BackColor = Color.FromArgb(15, 23, 42), Padding = new Padding(0, 2, 0, 1), Margin = new Padding(0) };
            logActions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 38));
            logActions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 46));
            logActions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 42));
            logActions.Controls.Add(LogButton("Lỗi", (s, e) => ShowErrorLog(), _text, 0), 0, 0);
            logActions.Controls.Add(LogButton("Copy", (s, e) => CopyLog(), _text, 0), 1, 0);
            logActions.Controls.Add(LogButton("Xóa", (s, e) => ClearLog(), _danger, 0), 2, 0);
            logPanel.Controls.Add(logActions, 0, 2);
            FlushPendingLogs();
            root.Controls.Add(logPanel, 1, 2);
            AddLog("Log sẵn sàng.");
        }

        private void CenterOnActiveScreen()
        {
            var screen = Screen.FromPoint(Cursor.Position);
            if (screen == null)
                screen = Screen.PrimaryScreen;
            var area = screen.WorkingArea;
            var targetWidth = Math.Min(1200, Math.Max(MinimumSize.Width, area.Width - 24));
            var targetHeight = Math.Min(720, Math.Max(MinimumSize.Height, area.Height - 24));
            if (targetWidth < Width || Width > area.Width)
                Width = targetWidth;
            if (targetHeight < Height || Height > area.Height)
                Height = targetHeight;
            Left = area.Left + Math.Max(0, (area.Width - Width) / 2);
            Top = area.Top + Math.Max(0, (area.Height - Height) / 2);
        }

        private TabPage BuildDriveTab()
        {
            var page = new TabPage("Cấu hình") { BackColor = _surface, Padding = new Padding(14), UseVisualStyleBackColor = false };
            page.AutoScroll = true;
            var pageLayout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, BackColor = _surface };
            pageLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            pageLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            page.Controls.Add(pageLayout);

            var actionBar = BuildDriveActionBar();
            pageLayout.Controls.Add(actionBar, 0, 0);
            var configTabs = new FlatTabControl
            {
                Dock = DockStyle.Fill,
                DrawMode = TabDrawMode.OwnerDrawFixed,
                SizeMode = TabSizeMode.Fixed,
                ItemSize = new Size(100, 28),
                Padding = new Point(10, 5),
                Font = new Font("Segoe UI", 8.5F, FontStyle.Bold)
            };
            configTabs.DrawItem += DrawSecondaryTab;
            pageLayout.Controls.Add(configTabs, 0, 1);

            var basicPage = ConfigSectionPage("Cơ bản");
            var basicPanel = ConfigGrid(3);
            basicPage.Controls.Add(basicPanel);
            nameBox = AddText(basicPanel, "Tên profile", "Ổ mới", 0, 0);
            nameBox.TextChanged += (s, e) =>
            {
                if (!_loadingProfileFields && !_changingProfileNameAutomatically)
                    _profileNameEditedByUser = true;
            };
            remoteCombo = AddCombo(basicPanel, "Remote", 1, 0);
            remoteCombo.SelectedIndexChanged += (s, e) => AutoNameFromSelectedRemote();
            pathBox = AddText(basicPanel, "Đường dẫn remote", "/", 0, 1);
            driveCombo = AddCombo(basicPanel, "Ký tự ổ đĩa", 1, 1);
            var checks = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0, 8, 0, 0), BackColor = _surface };
            readOnlyBox = new CheckBox { Text = "Chỉ đọc", Width = 100 };
            autoMountBox = new CheckBox { Text = "Tự mount khi mở app", Width = 190 };
            networkModeBox = new CheckBox { Text = "Network mode", Width = 140, Checked = true };
            checks.Controls.Add(readOnlyBox);
            checks.Controls.Add(autoMountBox);
            checks.Controls.Add(networkModeBox);
            basicPanel.Controls.Add(checks, 0, 2);
            basicPanel.SetColumnSpan(checks, 2);
            configTabs.TabPages.Add(basicPage);

            var cachePage = ConfigSectionPage("Cache");
            var cachePanel = ConfigGrid(3);
            cachePage.Controls.Add(cachePanel);
            cacheModeCombo = AddCombo(cachePanel, "Chế độ VFS cache", 0, 0);
            cacheModeCombo.Items.AddRange(new object[] { "off", "minimal", "writes", "full" });
            cacheModeCombo.SelectedItem = "full";
            cacheDirBox = AddText(cachePanel, "Thư mục cache", "%USERPROFILE%\\.cache\\rclone", 1, 0);
            transfersBox = AddNumber(cachePanel, "Transfers", 4, 1, 64, 0, 1);
            bufferBox = AddNumber(cachePanel, "Bộ đệm MB", 32, 1, 1024, 1, 1);
            mountPresetCombo = AddCombo(cachePanel, "Preset mount", 0, 2);
            mountPresetCombo.Items.AddRange(new object[] { "Nhanh/RaiDrive", "OpenCode", "Live" });
            mountPresetCombo.SelectedItem = "Nhanh/RaiDrive";
            cacheMaxAgeBox = AddText(cachePanel, "Giữ cache tối đa", "72h", 1, 2);
            configTabs.TabPages.Add(cachePage);

            var tunnelPage = ConfigSectionPage("Tunnel");
            var tunnelPanel = ConfigGrid(3);
            tunnelPage.Controls.Add(tunnelPanel);
            writeBackBox = AddText(tunnelPanel, "Upload sau khi sửa", "5s", 0, 0);
            tunnelPortBox = AddNumber(tunnelPanel, "Tunnel local port (0 = tự chọn)", 0, 0, 65535, 1, 0);
            tunnelEnabledBox = new CheckBox { Text = "Mount Cloudflare tunnel", Width = 220, Dock = DockStyle.Fill };
            tunnelEnabledBox.CheckedChanged += (s, e) =>
            {
                if (!_loadingProfileFields)
                    AddLog("Cloudflare tunnel " + (tunnelEnabledBox.Checked ? "đã bật cho profile/form hiện tại." : "đã tắt cho profile/form hiện tại."), "TUNNEL");
            };
            tunnelPanel.Controls.Add(Wrap("Cloudflare Access", tunnelEnabledBox), 0, 1);
            tunnelCommandBox = new TextBox { Text = "", Height = 54, Multiline = true, ScrollBars = ScrollBars.Vertical };
            tunnelPanel.Controls.Add(Wrap("Lệnh tunnel tùy chỉnh", tunnelCommandBox), 1, 1);
            configTabs.TabPages.Add(tunnelPage);

            var advancedPage = ConfigSectionPage("Nâng cao");
            var advancedPanel = ConfigGrid(6);
            advancedPage.Controls.Add(advancedPanel);
            extraArgsBox = new TextBox { Text = "", Height = 54, Multiline = true, ScrollBars = ScrollBars.Vertical };
            advancedPanel.Controls.Add(Wrap("Tham số rclone thêm", extraArgsBox), 0, 0);
            advancedPanel.SetColumnSpan(extraArgsBox.Parent, 2);
            var workspaceChecks = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0, 8, 0, 0), BackColor = _surface };
            codeWorkspaceAutoUploadBox = new CheckBox { Text = "Code Workspace auto upload", Width = 230 };
            codeWorkspaceSkipNewerRemoteBox = new CheckBox { Text = "Không ghi đè file host mới hơn", Width = 250, Checked = true };
            workspaceChecks.Controls.Add(codeWorkspaceAutoUploadBox);
            workspaceChecks.Controls.Add(codeWorkspaceSkipNewerRemoteBox);
            advancedPanel.Controls.Add(workspaceChecks, 0, 1);
            advancedPanel.SetColumnSpan(workspaceChecks, 2);
            codeWorkspaceDelayBox = AddNumber(advancedPanel, "Delay upload (giây)", 2, 1, 60, 0, 2);
            codeWorkspaceStatusLabel = new Label { Text = "Code Workspace: tắt", Dock = DockStyle.Fill, ForeColor = _muted, TextAlign = ContentAlignment.MiddleLeft, BackColor = _surface };
            advancedPanel.Controls.Add(Wrap("Trạng thái", codeWorkspaceStatusLabel), 1, 2);
            codeWorkspaceIgnoreBox = new TextBox { Text = DriveProfile.DefaultCodeWorkspaceIgnores, Height = 78, Multiline = true, ScrollBars = ScrollBars.Vertical };
            advancedPanel.Controls.Add(Wrap("Bỏ qua khi auto upload", codeWorkspaceIgnoreBox), 0, 3);
            advancedPanel.SetColumnSpan(codeWorkspaceIgnoreBox.Parent, 2);
            configTabs.TabPages.Add(advancedPage);
            return page;
        }

        private Control BuildDriveActionBar()
        {
            var bar = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1,
                RowCount = 2,
                BackColor = _surface,
                Padding = new Padding(0, 2, 0, 8),
                Margin = new Padding(0, 0, 0, 8)
            };
            bar.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            bar.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));

            var primaryRow = ActionRow();
            driveConnectButton = ActionButton("Kết nối", async (s, e) => await ToggleSelectedConnectionAsync(), _primary, Color.White, 104);
            primaryRow.Controls.Add(driveConnectButton);
            primaryRow.Controls.Add(ActionButton("Lưu", (s, e) => SaveCurrentProfile(), _primary, Color.White, 72));
            primaryRow.Controls.Add(ActionButton("Làm mới ổ", async (s, e) => await RefreshSelectedMountAsync(), _surface, _text, 96));
            primaryRow.Controls.Add(ActionButton("Code IDE", (s, e) => ApplyCodeIdePreset(), _surface, _text, 86));

            var toolRow = ActionRow();
            toolRow.Controls.Add(ActionButton("Tải về", async (s, e) => await DownloadRemoteToLocalAsync(), _surface, _text, 76));
            toolRow.Controls.Add(ActionButton("Đẩy lên", async (s, e) => await UploadLocalChangesAsync(), _primary, Color.White, 82));
            toolRow.Controls.Add(ActionButton("Mở local", (s, e) => OpenLocalWorkspace(), _surface, _text, 84));
            toolRow.Controls.Add(ActionButton("Code WS", async (s, e) => await StartCodeWorkspaceModeAsync(), _success, Color.White, 86));
            toolRow.Controls.Add(ActionButton("OpenCode", (s, e) => OpenProjectInOpenCode(), _surface, _text, 92));
            toolRow.Controls.Add(ActionButton("Fix OC", (s, e) => AutoFixOpenCodeForSelectedProject(), _surface, _text, 76));

            bar.Controls.Add(primaryRow, 0, 0);
            bar.Controls.Add(toolRow, 0, 1);
            return bar;
        }

        private FlowLayoutPanel ActionRow()
        {
            return new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                BackColor = _surface,
                Padding = new Padding(0),
                Margin = new Padding(0)
            };
        }

        private TabPage ConfigSectionPage(string title)
        {
            return new TabPage(title) { BackColor = _surface, Padding = new Padding(12, 14, 12, 8), UseVisualStyleBackColor = false, AutoScroll = true };
        }

        private TableLayoutPanel ConfigGrid(int rows)
        {
            var panel = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2,
                RowCount = rows,
                BackColor = _surface
            };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            for (var i = 0; i < rows; i++)
                panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
            return panel;
        }

        private void DrawSecondaryTab(object sender, DrawItemEventArgs e)
        {
            DrawTabHeader(sender as TabControl, e, false);
        }

        private TabPage BuildBrowserTab()
        {
            var page = new TabPage("Duyệt file") { BackColor = _surface, Padding = new Padding(12), UseVisualStyleBackColor = false };
            var pageLayout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, BackColor = _surface };
            pageLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            pageLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            page.Controls.Add(pageLayout);

            var top = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 7, RowCount = 1, BackColor = _surface, Padding = new Padding(0, 4, 0, 4) };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 62));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 46));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 126));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 86));
            pageLayout.Controls.Add(top, 0, 0);

            browseRemoteCombo = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(0, 4, 12, 4) };
            browsePathBox = new TextBox { Dock = DockStyle.Fill, Text = "/", Margin = new Padding(0, 4, 12, 4) };
            top.Controls.Add(new Label { Text = "Remote", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
            top.Controls.Add(browseRemoteCombo, 1, 0);
            top.Controls.Add(new Label { Text = "Path", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 2, 0);
            top.Controls.Add(browsePathBox, 3, 0);
            top.Controls.Add(ActionButton("Liệt kê", async (s, e) => await BrowseListAsync(), _primary, Color.White, 92), 4, 0);
            top.Controls.Add(ActionButton("Tạo thư mục", async (s, e) => await BrowseMkdirAsync(), _surface, _text, 116), 5, 0);
            top.Controls.Add(ActionButton("Xóa", async (s, e) => await BrowseDeleteAsync(), _surface, _danger, 78), 6, 0);

            browserList = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, Font = new Font("Consolas", 9.5F), BorderStyle = BorderStyle.FixedSingle, BackColor = Color.White };
            browserList.Columns.Add("Loại", 82);
            browserList.Columns.Add("Tên", 260);
            browserList.Columns.Add("Kích thước", 110);
            browserList.Columns.Add("Ngày sửa", 168);
            browserList.Columns.Add("Path", 360);
            browserList.DoubleClick += async (s, e) => await BrowseOpenSelectedAsync();
            pageLayout.Controls.Add(browserList, 0, 1);
            return page;
        }

        private TabPage BuildTransferTab()
        {
            var page = new TabPage("Truyền dữ liệu") { BackColor = _surface, Padding = new Padding(12), UseVisualStyleBackColor = false };
            var pageLayout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, BackColor = _surface };
            pageLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            pageLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            page.Controls.Add(pageLayout);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, BackColor = _surface };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
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
            var page = new TabPage("Công cụ") { BackColor = _surface, Padding = new Padding(22), UseVisualStyleBackColor = false };
            page.AutoScroll = true;
            var layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, RowCount = 5, ColumnCount = 1, BackColor = _surface };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            page.Controls.Add(layout);

            layout.Controls.Add(new Label { Text = "Công cụ", Dock = DockStyle.Fill, Font = new Font("Segoe UI", 13F, FontStyle.Bold), ForeColor = _text, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, 0, 0, 12) }, 0, 0);

            var remoteActions = ToolGroup("Remote");
            remoteActions.Controls.Add(ActionButton("Thông tin", async (s, e) => await RunSimpleForSelectedAsync("about"), _surface, _text, 104));
            remoteActions.Controls.Add(ActionButton("Dung lượng", async (s, e) => await RunSimpleForSelectedAsync("size"), _surface, _text, 116));
            remoteActions.Controls.Add(ActionButton("Cleanup", async (s, e) => await RunSimpleForSelectedAsync("cleanup"), _surface, _text, 104));
            remoteActions.Controls.Add(ActionButton("Phiên bản", async (s, e) => await RunCaptureAsync("version"), _surface, _text, 104));
            layout.Controls.Add(remoteActions, 0, 1);

            var configActions = ToolGroup("Config");
            configActions.Controls.Add(ActionButton("Thêm config", (s, e) => SelectTab("Thêm config"), _surface, _text, 124));
            configActions.Controls.Add(ActionButton("Xem config", (s, e) => ShowConfigFromUi(), _surface, _text, 118));
            configActions.Controls.Add(ActionButton("Xóa config", async (s, e) => await DeleteConfigFromUiAsync(), _surface, _danger, 118));
            configActions.Controls.Add(ActionButton("Mở wizard", (s, e) => OpenConfig(), _surface, _text, 112));
            configActions.Controls.Add(ActionButton("Đồng bộ config lên", async (s, e) => await SyncConfigUpAsync(), _primary, Color.White, 168));
            configActions.Controls.Add(ActionButton("UI web", (s, e) => StartWebUi(), _surface, _text, 92));
            layout.Controls.Add(configActions, 0, 2);

            var mountActions = ToolGroup("Mount / BAT");
            mountActions.Controls.Add(ActionButton("Mount chưa kết nối", async (s, e) => await MountDisconnectedProfilesAsync(), _surface, _text, 166));
            mountActions.Controls.Add(ActionButton("Tạo BAT mount", (s, e) => CreateBatForSelected(), _surface, _text, 132));
            mountActions.Controls.Add(ActionButton("Tạo BAT ngắt", (s, e) => CreateUnmountBatForSelected(), _surface, _danger, 126));
            mountActions.Controls.Add(ActionButton("Cache tất cả", (s, e) => BrowseCacheDirForAllProfiles(), _surface, _text, 116));
            mountActions.Controls.Add(ActionButton("Dọn cache", (s, e) => ClearCacheForSelectedProfile(), _surface, _danger, 112));
            mountActions.Controls.Add(ActionButton("Dọn cache tất cả", (s, e) => ClearCacheForAllProfiles(), _surface, _danger, 148));
            mountActions.Controls.Add(ActionButton("Tải về máy", async (s, e) => await DownloadRemoteToLocalAsync(), _surface, _text, 112));
            mountActions.Controls.Add(ActionButton("Đẩy lên host", async (s, e) => await UploadLocalChangesAsync(), _primary, Color.White, 112));
            mountActions.Controls.Add(ActionButton("Mở local", (s, e) => OpenLocalWorkspace(), _surface, _text, 96));
            layout.Controls.Add(mountActions, 0, 3);

            var systemActions = ToolGroup("Hệ thống");
            systemActions.Controls.Add(ActionButton("Quét ổ", (s, e) => RefreshMountedDriveList(), _surface, _text, 88));
            systemActions.Controls.Add(ActionButton("Làm mới", async (s, e) => await RefreshAllAsync(), _surface, _text, 104));
            systemActions.Controls.Add(ActionButton("Cập nhật", async (s, e) => await CheckForAppUpdateAsync(true), _surface, _text, 104));
            systemActions.Controls.Add(ActionButton("Cài WinFsp", async (s, e) => await EnsureWinFspAvailableAsync(true), _surface, _text, 112));
            systemActions.Controls.Add(ActionButton("Startup ON", (s, e) => SetStartup(true), _surface, _text, 112));
            systemActions.Controls.Add(ActionButton("Startup OFF", (s, e) => SetStartup(false), _surface, _danger, 116));
            layout.Controls.Add(systemActions, 0, 4);
            return page;
        }

        private TabPage BuildBrowseTransferTab()
        {
            var page = new TabPage("Duyệt & Truyền") { BackColor = _surface, Padding = new Padding(4), UseVisualStyleBackColor = false };
            var subTabs = new FlatTabControl
            {
                Dock = DockStyle.Fill,
                DrawMode = TabDrawMode.OwnerDrawFixed,
                SizeMode = TabSizeMode.Fixed,
                ItemSize = new Size(100, 28),
                Padding = new Point(10, 5),
                Font = new Font("Segoe UI", 8.5F, FontStyle.Bold)
            };
            subTabs.DrawItem += DrawSecondaryTab;
            var browserPage = BuildBrowserTab();
            browserPage.Text = "Duyệt file";
            subTabs.TabPages.Add(browserPage);
            var transferPage = BuildTransferTab();
            transferPage.Text = "Truyền dữ liệu";
            subTabs.TabPages.Add(transferPage);
            page.Controls.Add(subTabs);
            return page;
        }

        private TabPage BuildConfigToolsTab()
        {
            var page = new TabPage("Config & Công cụ") { BackColor = _surface, Padding = new Padding(4), UseVisualStyleBackColor = false };
            var subTabs = new FlatTabControl
            {
                Dock = DockStyle.Fill,
                DrawMode = TabDrawMode.OwnerDrawFixed,
                SizeMode = TabSizeMode.Fixed,
                ItemSize = new Size(100, 28),
                Padding = new Point(10, 5),
                Font = new Font("Segoe UI", 8.5F, FontStyle.Bold)
            };
            subTabs.DrawItem += DrawSecondaryTab;
            var configPage = BuildAddConfigTab();
            configPage.Text = "Thêm config";
            subTabs.TabPages.Add(configPage);
            var toolsPage = BuildToolsTab();
            toolsPage.Text = "Công cụ";
            subTabs.TabPages.Add(toolsPage);
            page.Controls.Add(subTabs);
            return page;
        }

        private FlowLayoutPanel ToolGroup(string title)
        {
            var panel = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = _surface,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                Padding = new Padding(0, 8, 0, 6),
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

        private FlowLayoutPanel CompactButtonGroup(string title)
        {
            var panel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = _surface,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                Padding = new Padding(0, 0, 8, 0),
                Margin = new Padding(0)
            };
            panel.Controls.Add(new Label
            {
                Text = title,
                AutoSize = false,
                Width = 78,
                Height = 36,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Segoe UI", 8.8F, FontStyle.Bold),
                ForeColor = _muted,
                Margin = new Padding(0, 3, 8, 3)
            });
            return panel;
        }

        private TabPage BuildAddConfigTab()
        {
            var page = new TabPage("Thêm config") { BackColor = _surface, Padding = new Padding(12), UseVisualStyleBackColor = false, AutoScroll = true };
            var pageLayout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, RowCount = 3, ColumnCount = 1, BackColor = _surface };
            pageLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            pageLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            pageLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            page.Controls.Add(pageLayout);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 5, ColumnCount = 2, BackColor = _surface };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
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

            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0, 8, 0, 4), BackColor = _surface, WrapContents = true, AutoScroll = false };
            actions.Controls.Add(ActionButton("Kiểm tra kết nối", async (s, e) => await CheckConfigConnectionAsync(true), _surface, _text, 162));
            actions.Controls.Add(ActionButton("Thêm config", async (s, e) => await AddConfigFromUiAsync(), _primary, Color.White, 132));
            actions.Controls.Add(ActionButton("Lưu config", async (s, e) => await SaveConfigFromUiAsync(), _surface, _text, 124));
            actions.Controls.Add(ActionButton("Xem config", (s, e) => ShowConfigFromUi(), _surface, _text, 118));
            actions.Controls.Add(ActionButton("Xóa config", async (s, e) => await DeleteConfigFromUiAsync(), _surface, _danger, 118));
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
                BackColor = Color.White,
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
            var actualBg = backColor == _surface ? Color.White : backColor;
            
            Color hoverBg, hoverBorder, hoverFore;
            Color pressedBg, pressedBorder, pressedFore;
            Color border;
            
            if (actualBg == Color.White)
            {
                border = Color.FromArgb(226, 232, 240); // Slate 200
                
                if (foreColor == _danger)
                {
                    hoverBg = Color.FromArgb(254, 242, 242); // Red 50
                    hoverBorder = Color.FromArgb(252, 165, 165); // Red 300
                    hoverFore = Color.FromArgb(220, 38, 38); // Red 600
                    
                    pressedBg = Color.FromArgb(254, 226, 226); // Red 100
                    pressedBorder = Color.FromArgb(239, 68, 68); // Red 500
                    pressedFore = Color.FromArgb(185, 28, 28); // Red 700
                }
                else
                {
                    hoverBg = Color.FromArgb(243, 248, 255); // Light Blue 50
                    hoverBorder = Color.FromArgb(191, 219, 254); // Blue 200
                    hoverFore = _primary; // Primary blue text on hover
                    
                    pressedBg = Color.FromArgb(219, 234, 254); // Blue 100
                    pressedBorder = Color.FromArgb(96, 165, 250); // Blue 400
                    pressedFore = Color.FromArgb(30, 58, 138); // Dark blue text
                }
            }
            else if (actualBg == _primary)
            {
                border = _primary;
                hoverBg = Color.FromArgb(29, 78, 216); // Darker blue
                hoverBorder = Color.FromArgb(29, 78, 216);
                hoverFore = Color.White;
                
                pressedBg = Color.FromArgb(30, 58, 138);
                pressedBorder = Color.FromArgb(30, 58, 138);
                pressedFore = Color.White;
            }
            else if (actualBg == _danger)
            {
                border = _danger;
                hoverBg = Color.FromArgb(220, 38, 38);
                hoverBorder = Color.FromArgb(220, 38, 38);
                hoverFore = Color.White;
                
                pressedBg = Color.FromArgb(185, 28, 28);
                pressedBorder = Color.FromArgb(185, 28, 28);
                pressedFore = Color.White;
            }
            else
            {
                border = actualBg;
                hoverBg = Color.FromArgb(
                    Math.Max(0, actualBg.R - 20),
                    Math.Max(0, actualBg.G - 20),
                    Math.Max(0, actualBg.B - 20)
                );
                hoverBorder = hoverBg;
                hoverFore = foreColor;
                
                pressedBg = Color.FromArgb(
                    Math.Max(0, actualBg.R - 40),
                    Math.Max(0, actualBg.G - 40),
                    Math.Max(0, actualBg.B - 40)
                );
                pressedBorder = pressedBg;
                pressedFore = foreColor;
            }

            var b = new RoundedButton
            {
                Text = text,
                Width = width,
                Height = 32,
                Margin = new Padding(3, 4, 3, 4),
                BackColor = actualBg,
                ForeColor = foreColor,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                BorderRadius = 10,
                BorderSize = 1,
                BorderColor = border,
                HoverBackColor = hoverBg,
                HoverBorderColor = hoverBorder,
                HoverForeColor = hoverFore,
                PressedBackColor = pressedBg,
                PressedBorderColor = pressedBorder,
                PressedForeColor = pressedFore
            };
            b.TabStop = false;
            b.UseVisualStyleBackColor = false;
            b.Click += click;
            return b;
        }

        private void DrawMainTab(object sender, DrawItemEventArgs e)
        {
            DrawTabHeader(sender as TabControl, e, true);
        }

        private void DrawTabHeader(TabControl tabs, DrawItemEventArgs e, bool isMain)
        {
            if (tabs == null) return;
            var selected = e.Index == tabs.SelectedIndex;
            var bounds = e.Bounds;
            var g = e.Graphics;

            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            var parentBgColor = tabs.Parent?.BackColor ?? Color.FromArgb(204, 209, 216);
            
            // Inflate fill bounds slightly to overwrite OS-drawn tab outlines/borders
            var fillRect = bounds;
            fillRect.Inflate(3, 3);
            
            using (var clearBrush = new SolidBrush(parentBgColor))
                g.FillRectangle(clearBrush, fillRect);

            using (var borderPen = new Pen(_line, 1F))
            {
                g.DrawLine(borderPen, bounds.Left - 4, bounds.Bottom - 1, bounds.Right + 4, bounds.Bottom - 1);
            }

            if (selected)
            {
                using (var accentBrush = new SolidBrush(_primary))
                {
                    g.FillRectangle(accentBrush, bounds.Left + 4, bounds.Bottom - 3, bounds.Width - 8, 3);
                }
            }

            using (var font = new Font(tabs.Font.FontFamily, tabs.Font.Size, selected ? FontStyle.Bold : FontStyle.Regular))
            {
                var foreClr = selected ? _primary : _muted;
                TextRenderer.DrawText(
                    g,
                    tabs.TabPages[e.Index].Text,
                    font,
                    bounds,
                    foreClr,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
        }

        private void DrawDriveListItem(object sender, DrawListViewItemEventArgs e)
        {
            DrawDriveRow(e.Graphics, e.Item, e.Bounds);
        }

        private void DrawDriveListSubItem(object sender, DrawListViewSubItemEventArgs e)
        {
            if (e.ColumnIndex == 0)
                DrawDriveRow(e.Graphics, e.Item, e.Bounds);
        }

        private void DrawDriveRow(Graphics g, ListViewItem item, Rectangle itemBounds)
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var bounds = itemBounds;
            bounds.Inflate(-4, -3);

            var selected = item.Selected;
            var mounted = false;
            var name = item.Text;
            var drive = "";
            var source = "";
            var status = "Rảnh";
            var capacityText = "Dung lượng chưa xác định";
            double? usedRatio = null;

            var profile = item.Tag as DriveProfile;
            if (profile != null)
            {
                mounted = IsMountedProfile(profile);
                name = profile.Name;
                drive = DriveDisplay(profile);
                source = profile.Remote + DriveProfile.NormalizeRemotePath(profile.RemotePath, profile.Remote);
                status = mounted ? "Đang kết nối" : "Chưa kết nối";
                if (mounted)
                    capacityText = TryGetRcloneCapacityText(source, drive, out usedRatio);
            }
            else
            {
                var external = item.Tag as MountedDriveInfo;
                if (external != null)
                {
                    mounted = true;
                    name = external.Name;
                    drive = external.DriveLetter;
                    source = external.DisplayRoot;
                    status = "Đang bật";
                    capacityText = TryGetRcloneCapacityText(source, drive, out usedRatio);
                }
            }

            var cardColor = selected ? Color.FromArgb(198, 216, 242) : Color.FromArgb(230, 233, 238);
            using (var bg = new SolidBrush(cardColor))
                g.FillRectangle(bg, bounds);

            using (var cardBorder = new Pen(selected ? _primary : Color.FromArgb(226, 232, 240)))
                g.DrawRectangle(cardBorder, bounds);

            var accent = mounted ? _success : Color.FromArgb(203, 213, 225);
            using (var accentBrush = new SolidBrush(accent))
                g.FillRectangle(accentBrush, bounds.Left, bounds.Top, 5, bounds.Height);

            var actionRects = GetDriveRowActionRects(bounds);
            var actionLeft = actionRects.Count == 0 ? bounds.Right - 8 : actionRects.Min(r => r.Left);
            var textLeft = bounds.Left + 50;
            var textRight = actionLeft - 10;
            var textWidth = Math.Max(24, textRight - textLeft);

            var driveRect = new Rectangle(bounds.Left + 16, bounds.Top + (bounds.Height - 18) / 2, 28, 18);
            TextRenderer.DrawText(g, IsAutoDrive(drive) ? "A:" : drive, new Font("Segoe UI", 7.6F, FontStyle.Bold),
                driveRect, selected ? _primary : _text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            TextRenderer.DrawText(g, name, new Font("Segoe UI", 9.3F, FontStyle.Bold),
                new Rectangle(textLeft, bounds.Top + 6, textWidth, 20),
                _text, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            TextRenderer.DrawText(g, source, new Font("Segoe UI", 7.5F),
                new Rectangle(textLeft, bounds.Top + 26, textWidth, 16),
                _muted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            TextRenderer.DrawText(g, capacityText, new Font("Segoe UI", 7.8F),
                new Rectangle(textLeft, bounds.Top + 42, textWidth, 16),
                _muted,
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            var barRect = new Rectangle(textLeft, bounds.Top + 60, textWidth, 5);
            using (var barBack = new SolidBrush(Color.FromArgb(226, 232, 240)))
                g.FillRectangle(barBack, barRect);
            if (usedRatio.HasValue)
            {
                var fillWidth = Math.Max(1, Math.Min(barRect.Width, (int)Math.Round(barRect.Width * usedRatio.Value)));
                using (var barFill = new SolidBrush(_primary))
                    g.FillRectangle(barFill, barRect.Left, barRect.Top, fillWidth, barRect.Height);
            }

            DrawDriveRowAction(g, actionRects[0], mounted ? DriveRowActionIcon.Disconnect : DriveRowActionIcon.Connect, mounted ? _danger : _success, true);
            DrawDriveRowAction(g, actionRects[1], DriveRowActionIcon.Settings, _muted, profile != null);
            DrawDriveRowAction(g, actionRects[2], DriveRowActionIcon.Folder, _muted, mounted);
            DrawDriveRowAction(g, actionRects[3], DriveRowActionIcon.Delete, _danger, true);
        }

        private enum DriveRowActionIcon
        {
            Connect,
            Disconnect,
            Settings,
            Folder,
            Delete
        }

        private List<Rectangle> GetDriveRowActionRects(Rectangle bounds)
        {
            var compact = bounds.Width < 430;
            var medium = bounds.Width < 500;
            var size = compact ? 26 : medium ? 30 : 34;
            var gap = compact ? 5 : medium ? 7 : 10;
            var top = bounds.Top + (bounds.Height - size) / 2;
            var right = bounds.Right - (compact ? 6 : 12);
            var rects = new List<Rectangle>();
            for (var i = 0; i < 4; i++)
            {
                var left = right - size - i * (size + gap);
                rects.Insert(0, new Rectangle(left, top, size, size));
            }
            return rects;
        }

        private void DrawDriveRowAction(Graphics g, Rectangle rect, DriveRowActionIcon icon, Color accent, bool enabled)
        {
            var compact = rect.Width < 30;
            var border = enabled && (icon == DriveRowActionIcon.Connect || icon == DriveRowActionIcon.Disconnect)
                ? accent
                : enabled ? Color.FromArgb(170, 179, 191) : Color.FromArgb(204, 209, 216);
            var iconColor = enabled
                ? (icon == DriveRowActionIcon.Connect || icon == DriveRowActionIcon.Disconnect ? Color.White : Color.FromArgb(75, 85, 99))
                : Color.FromArgb(156, 163, 175);

            if (enabled && (icon == DriveRowActionIcon.Connect || icon == DriveRowActionIcon.Disconnect))
            {
                using (var fillBrush = new SolidBrush(accent))
                    g.FillEllipse(fillBrush, rect);
            }

            using (var pen = new Pen(border, compact ? 1.6F : 2F))
                g.DrawEllipse(pen, rect);
            var inner = Rectangle.Inflate(rect, compact ? -8 : -10, compact ? -8 : -10);
            if (inner.Width < 8 || inner.Height < 8)
                inner = Rectangle.Inflate(rect, -7, -7);

            using (var brush = new SolidBrush(iconColor))
            using (var pen = new Pen(iconColor, compact ? 1.5F : 1.8F))
            {
                pen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                pen.LineJoin = System.Drawing.Drawing2D.LineJoin.Round;

                if (icon == DriveRowActionIcon.Connect)
                {
                    var points = new[]
                    {
                        new Point(inner.Left + 1, inner.Top),
                        new Point(inner.Right, inner.Top + inner.Height / 2),
                        new Point(inner.Left + 1, inner.Bottom)
                    };
                    g.FillPolygon(brush, points);
                }
                else if (icon == DriveRowActionIcon.Disconnect)
                {
                    g.FillRectangle(brush, inner);
                }
                else if (icon == DriveRowActionIcon.Settings)
                {
                    var cx = rect.Left + rect.Width / 2;
                    var cy = rect.Top + rect.Height / 2;
                    var radius = Math.Max(3, inner.Width / 3);
                    g.DrawEllipse(pen, cx - radius, cy - radius, radius * 2, radius * 2);
                    for (var i = 0; i < 8; i++)
                    {
                        var angle = Math.PI * 2 * i / 8;
                        var x1 = cx + (int)Math.Round(Math.Cos(angle) * (radius + 2));
                        var y1 = cy + (int)Math.Round(Math.Sin(angle) * (radius + 2));
                        var x2 = cx + (int)Math.Round(Math.Cos(angle) * (radius + 5));
                        var y2 = cy + (int)Math.Round(Math.Sin(angle) * (radius + 5));
                        g.DrawLine(pen, x1, y1, x2, y2);
                    }
                    g.FillEllipse(brush, cx - 1, cy - 1, 3, 3);
                }
                else if (icon == DriveRowActionIcon.Folder)
                {
                    var folder = new Rectangle(inner.Left - 1, inner.Top + 2, inner.Width + 2, inner.Height - 2);
                    var tabWidth = Math.Max(5, folder.Width / 3);
                    var points = new[]
                    {
                        new Point(folder.Left, folder.Top + 3),
                        new Point(folder.Left + tabWidth, folder.Top + 3),
                        new Point(folder.Left + tabWidth + 3, folder.Top),
                        new Point(folder.Right, folder.Top),
                        new Point(folder.Right, folder.Bottom),
                        new Point(folder.Left, folder.Bottom)
                    };
                    g.DrawPolygon(pen, points);
                }
                else if (icon == DriveRowActionIcon.Delete)
                {
                    g.DrawLine(pen, inner.Left, inner.Top, inner.Right, inner.Bottom);
                    g.DrawLine(pen, inner.Right, inner.Top, inner.Left, inner.Bottom);
                }
            }
        }

        private async Task HandleDriveListMouseClickAsync(MouseEventArgs e)
        {
            if (_connectionBusy)
            {
                AddLog("Đang xử lý kết nối/ngắt, vui lòng đợi.", "WARN");
                return;
            }
            var item = profileList.GetItemAt(e.X, e.Y);
            if (item == null) return;
            item.Selected = true;
            var bounds = item.Bounds;
            bounds.Inflate(-4, -3);
            var rects = GetDriveRowActionRects(bounds);
            var action = rects.FindIndex(r => r.Contains(e.Location));
            if (action < 0) return;
            var profile = item.Tag as DriveProfile;
            var external = item.Tag as MountedDriveInfo;

            if (action == 0)
            {
                if (profile != null)
                {
                    SelectProfile(profile);
                    if (IsMountedProfile(profile))
                    {
                        AddLog("Người dùng bấm ngắt trên card: " + profile.Name);
                        SetConnectionBusy(true, "Đang ngắt");
                        try { UnmountSelected(); }
                        finally { SetConnectionBusy(false, ""); }
                    }
                    else
                    {
                        AddLog("Người dùng bấm kết nối trên card: " + profile.Name);
                        SetConnectionBusy(true, "Đang kết nối");
                        try { await SaveAndMountCurrentProfileAsync(); }
                        finally { SetConnectionBusy(false, ""); }
                    }
                }
                else if (external != null)
                {
                    UnmountDriveLetter(external.DriveLetter, external.Name);
                    AddLog("Đã gửi lệnh ngắt ổ đang bật sẵn: " + external.DriveLetter);
                    RenderProfiles();
                }
                return;
            }
            if (action == 1)
            {
                if (profile != null)
                {
                    SelectProfile(profile);
                    OpenDriveSettingsDialog();
                }
                return;
            }
            if (action == 2)
            {
                if (profile != null)
                {
                    SelectProfile(profile);
                    OpenSelectedDrive();
                }
                else if (external != null)
                    OpenDriveInExplorer(external.DriveLetter);
                return;
            }
            if (action == 3)
            {
                if (profile != null)
                {
                    SelectProfile(profile);
                    DeleteCurrentProfile();
                }
                else if (external != null)
                {
                    UnmountDriveLetter(external.DriveLetter, external.Name);
                    AddLog("Đã gửi lệnh ngắt ổ đang bật sẵn: " + external.DriveLetter);
                    RenderProfiles();
                }
            }
        }

        private void HandleDriveListMouseMove(object sender, MouseEventArgs e)
        {
            var hit = GetDriveRowActionHit(e.Location);
            profileList.Cursor = hit.Action >= 0 ? Cursors.Hand : Cursors.Default;
            var text = hit.Action >= 0 ? GetDriveRowActionTip(hit.Item, hit.Action) : "";
            if (text == lastDriveActionTipText)
                return;
            lastDriveActionTipText = text;
            if (driveActionTip != null)
                driveActionTip.SetToolTip(profileList, text);
        }

        private DriveRowActionHit GetDriveRowActionHit(Point location)
        {
            var item = profileList.GetItemAt(location.X, location.Y);
            if (item == null)
                return new DriveRowActionHit(null, -1);
            var bounds = item.Bounds;
            bounds.Inflate(-4, -3);
            var rects = GetDriveRowActionRects(bounds);
            var action = rects.FindIndex(r => r.Contains(location));
            return new DriveRowActionHit(item, action);
        }

        private string GetDriveRowActionTip(ListViewItem item, int action)
        {
            if (item == null || action < 0)
                return "";
            var profile = item.Tag as DriveProfile;
            var external = item.Tag as MountedDriveInfo;
            if (action == 0)
            {
                if (profile != null)
                    return IsMountedProfile(profile) ? "Ngắt kết nối ổ này" : "Kết nối ổ này";
                return external != null ? "Ngắt ổ ngoài này" : "";
            }
            if (action == 1)
                return profile != null ? "Cài đặt profile này" : "Ổ ngoài không có cài đặt";
            if (action == 2)
                return "Mở ổ trong Explorer";
            if (action == 3)
                return profile != null ? "Xóa profile này" : "Ngắt ổ ngoài này";
            return "";
        }

        private struct DriveRowActionHit
        {
            public readonly ListViewItem Item;
            public readonly int Action;

            public DriveRowActionHit(ListViewItem item, int action)
            {
                Item = item;
                Action = action;
            }
        }

        private string TryGetRcloneCapacityText(string source, string drive, out double? usedRatio)
        {
            usedRatio = null;
            var key = CapacityCacheKey(source, drive);
            lock (_capacityLock)
            {
                CapacityInfo cached;
                if (_capacityCache.TryGetValue(key, out cached) && cached.ExpiresAtUtc > DateTime.UtcNow)
                {
                    usedRatio = cached.UsedRatio;
                    return cached.Text;
                }
            }

            QueueCapacityRefresh(source, drive, key);
            return IsRcloneRemoteSource(source) ? "Đang lấy dung lượng..." : "Dung lượng chưa xác định";
        }

        private string CapacityCacheKey(string source, string drive)
        {
            if (!string.IsNullOrWhiteSpace(source))
                return source.Trim();
            if (!string.IsNullOrWhiteSpace(drive))
                return NormalizeDriveChoice(drive);
            return "";
        }

        private void QueueCapacityRefresh(string source, string drive, string key)
        {
            if (string.IsNullOrWhiteSpace(key) || !IsRcloneRemoteSource(source))
                return;
            lock (_capacityLock)
            {
                if (_capacityRefreshPending.Contains(key))
                    return;
                _capacityRefreshPending.Add(key);
            }

            Task.Run(() =>
            {
                var info = ReadRcloneCapacityInfo(source);
                lock (_capacityLock)
                {
                    _capacityRefreshPending.Remove(key);
                    _capacityCache[key] = info;
                }
                try
                {
                    if (!IsDisposed && profileList != null && profileList.IsHandleCreated)
                        BeginInvoke(new Action(() => profileList.Invalidate()));
                }
                catch
                {
                }
            });
        }

        private bool IsRcloneRemoteSource(string source)
        {
            if (string.IsNullOrWhiteSpace(source))
                return false;
            source = source.Trim();
            var colon = source.IndexOf(':');
            if (colon <= 0)
                return false;
            if (colon == 1 && source.Length >= 2 && char.IsLetter(source[0]))
                return false;
            var beforeColon = source.Substring(0, colon);
            return beforeColon.IndexOf('\\') < 0 &&
                   beforeColon.IndexOf('/') < 0 &&
                   beforeColon.IndexOf(' ') < 0;
        }

        private CapacityInfo ReadRcloneCapacityInfo(string source)
        {
            var result = new CapacityInfo
            {
                Text = "Dung lượng chưa xác định",
                UsedRatio = null,
                ExpiresAtUtc = DateTime.UtcNow.AddMinutes(5)
            };
            try
            {
                if (!File.Exists(_rcloneExe))
                    return result;

                var psi = new ProcessStartInfo
                {
                    FileName = _rcloneExe,
                    Arguments = "about " + QuoteIfNeeded(source) + " --json",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                using (var proc = Process.Start(psi))
                {
                    if (proc == null)
                        return result;
                    var stdoutTask = proc.StandardOutput.ReadToEndAsync();
                    var stderrTask = proc.StandardError.ReadToEndAsync();
                    if (!proc.WaitForExit(12000))
                    {
                        try { proc.Kill(); } catch { }
                        result.ExpiresAtUtc = DateTime.UtcNow.AddMinutes(1);
                        return result;
                    }
                    Task.WaitAll(new Task[] { stdoutTask, stderrTask }, 1000);
                    if (proc.ExitCode != 0)
                    {
                        result.ExpiresAtUtc = DateTime.UtcNow.AddMinutes(2);
                        return result;
                    }

                    var data = _json.Deserialize<Dictionary<string, object>>(SafeTaskResult(stdoutTask));
                    var total = JsonLong(data, "total");
                    var used = JsonLong(data, "used");
                    var free = JsonLong(data, "free");
                    if (total > 0)
                    {
                        if (used < 0 && free >= 0)
                            used = Math.Max(0, total - free);
                        if (free < 0 && used >= 0)
                            free = Math.Max(0, total - used);
                        if (free >= 0)
                            result.Text = FormatBytes(free) + " trống của " + FormatBytes(total);
                        else if (used >= 0)
                            result.Text = FormatBytes(used) + " đã dùng của " + FormatBytes(total);
                        if (used >= 0)
                            result.UsedRatio = Math.Max(0, Math.Min(1, used / (double)total));
                    }
                    else if (used >= 0)
                    {
                        result.Text = FormatBytes(used) + " đã dùng";
                    }
                }
            }
            catch
            {
            }
            return result;
        }

        private long JsonLong(Dictionary<string, object> data, string key)
        {
            try
            {
                object value;
                if (data != null && data.TryGetValue(key, out value) && value != null)
                    return Convert.ToInt64(value);
            }
            catch
            {
            }
            return -1;
        }

        private void UpdateConnectButtonState()
        {
            if (_connectionBusy)
            {
                UpdateConnectionBusyButtons();
                return;
            }
            var profile = SelectedProfile;
            var external = SelectedMountedDrive;
            var mounted = profile != null ? IsMountedProfile(profile) : external != null;
            var text = mounted ? "Ngắt kết nối" : "Kết nối";
            var back = mounted ? _danger : _primary;

            UpdateConnectButton(headerConnectButton, text, back);
            UpdateConnectButton(driveConnectButton, text, back);
        }

        private void UpdateConnectButton(Button button, string text, Color back)
        {
            if (button == null) return;
            button.Text = text;
            button.BackColor = back;
            button.ForeColor = Color.White;
            button.FlatAppearance.BorderColor = back;
            button.FlatAppearance.MouseOverBackColor = back == _danger ? Color.FromArgb(185, 28, 28) : Color.FromArgb(29, 78, 216);
            button.FlatAppearance.MouseDownBackColor = back == _danger ? Color.FromArgb(153, 27, 27) : Color.FromArgb(30, 58, 138);
        }

        private void SetConnectionBusy(bool busy, string text)
        {
            _connectionBusy = busy;
            _connectionBusyText = string.IsNullOrWhiteSpace(text) ? "Đang kết nối" : text;
            _connectionBusyStep = 0;

            if (busy)
            {
                if (_connectionBusyTimer == null)
                {
                    _connectionBusyTimer = new System.Windows.Forms.Timer { Interval = 450 };
                    _connectionBusyTimer.Tick += (s, e) =>
                    {
                        _connectionBusyStep = (_connectionBusyStep + 1) % 4;
                        UpdateConnectionBusyButtons();
                    };
                }
                _connectionBusyTimer.Start();
                UpdateConnectionBusyButtons();
            }
            else
            {
                if (_connectionBusyTimer != null)
                    _connectionBusyTimer.Stop();
                SetConnectButtonEnabled(headerConnectButton, true);
                SetConnectButtonEnabled(driveConnectButton, true);
                UpdateConnectButtonState();
            }
        }

        private void UpdateConnectionBusyButtons()
        {
            var dots = new string('.', _connectionBusyStep);
            UpdateConnectButton(headerConnectButton, _connectionBusyText + dots, Color.FromArgb(245, 158, 11));
            UpdateConnectButton(driveConnectButton, _connectionBusyText + dots, Color.FromArgb(245, 158, 11));
            SetConnectButtonEnabled(headerConnectButton, true);
            SetConnectButtonEnabled(driveConnectButton, true);
        }

        private void SetConnectButtonEnabled(Button button, bool enabled)
        {
            if (button == null) return;
            button.Enabled = enabled;
        }

        private Button LogButton(string text, EventHandler click, Color foreColor, int width)
        {
            Color hoverBg, hoverBorder, hoverFore;
            Color pressedBg, pressedBorder, pressedFore;
            
            if (foreColor == _danger)
            {
                hoverBg = Color.FromArgb(254, 242, 242);
                hoverBorder = Color.FromArgb(252, 165, 165);
                hoverFore = Color.FromArgb(220, 38, 38);
                
                pressedBg = Color.FromArgb(254, 226, 226);
                pressedBorder = Color.FromArgb(239, 68, 68);
                pressedFore = Color.FromArgb(185, 28, 28);
            }
            else
            {
                hoverBg = Color.FromArgb(243, 248, 255);
                hoverBorder = Color.FromArgb(191, 219, 254);
                hoverFore = _primary;
                
                pressedBg = Color.FromArgb(219, 234, 254);
                pressedBorder = Color.FromArgb(96, 165, 250);
                pressedFore = Color.FromArgb(30, 58, 138);
            }

            var b = new RoundedButton
            {
                Text = text,
                Width = width > 0 ? width : 1,
                Height = 20,
                Dock = width > 0 ? DockStyle.None : DockStyle.Fill,
                Margin = new Padding(1, 0, 1, 0),
                BackColor = Color.White,
                ForeColor = foreColor,
                Font = new Font("Segoe UI", 7.5F, FontStyle.Bold),
                BorderRadius = 6,
                BorderSize = 1,
                BorderColor = Color.FromArgb(203, 213, 225),
                HoverBackColor = hoverBg,
                HoverBorderColor = hoverBorder,
                HoverForeColor = hoverFore,
                PressedBackColor = pressedBg,
                PressedBorderColor = pressedBorder,
                PressedForeColor = pressedFore
            };
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
                    return;
                }
                foreach (Control child in page.Controls)
                {
                    var nested = child as TabControl;
                    if (nested == null) continue;
                    foreach (TabPage subPage in nested.TabPages)
                    {
                        if (string.Equals(subPage.Text, text, StringComparison.OrdinalIgnoreCase))
                        {
                            mainTabs.SelectedTab = page;
                            nested.SelectedTab = subPage;
                            return;
                        }
                    }
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
            var isMultiline = input is TextBox && ((TextBox)input).Multiline;
            var p = new Panel { Dock = DockStyle.Fill, Height = isMultiline ? 90 : 54, Padding = new Padding(4, 2, 14, 4), BackColor = _surface };
            p.Controls.Add(new Label { Text = label, Dock = DockStyle.Top, Height = 18, Font = new Font("Segoe UI", 8.2F, FontStyle.Bold), ForeColor = _muted, TextAlign = ContentAlignment.BottomLeft });
            input.Dock = DockStyle.Top;
            input.Top = 18;
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
            UpdateStatusLine(message, level);
            if (logBox == null)
            {
                _pendingLogLines.Add(Tuple.Create(line, level, message));
                return;
            }
            FlushPendingLogs();
            AppendColoredLogLine(line, level, message);
            TrimLogLines();
            if (logBox != null) logBox.ScrollToCaret();
        }

        private void AppendColoredLogLine(string line, string level, string message)
        {
            if (logBox == null) return;
            logBox.SelectionStart = logBox.TextLength;
            logBox.SelectionLength = 0;
            logBox.SelectionColor = LogLineColor(level, message);
            logBox.AppendText(line);
            logBox.SelectionColor = logBox.ForeColor;
            logBox.SelectionStart = logBox.TextLength;
        }

        private void FlushPendingLogs()
        {
            if (logBox == null || _pendingLogLines.Count == 0) return;
            foreach (var row in _pendingLogLines.ToList())
                AppendColoredLogLine(row.Item1, row.Item2, row.Item3);
            _pendingLogLines.Clear();
        }

        private void UpdateStatusLine(string message, string level)
        {
            var clean = (message ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            if (clean.Length > 96) clean = clean.Substring(0, 93) + "...";
            var labelText = (string.IsNullOrWhiteSpace(level) ? "" : level + "  ") + clean;
            var color = LogLineColor(level, message);
            if (statusLabel != null)
            {
                statusLabel.Text = labelText;
                statusLabel.ForeColor = StatusLineColor(level, message);
                statusLabel.BackColor = StatusLineBackColor(level, message);
            }
            if (liveLogLabel != null)
            {
                liveLogLabel.Text = DateTime.Now.ToString("HH:mm:ss") + "  " + labelText;
                liveLogLabel.ForeColor = color;
            }
        }

        private Color StatusLineColor(string level, string message)
        {
            var text = ((level ?? "") + " " + (message ?? "")).ToUpperInvariant();
            if (text.Contains("CRITICAL") || text.Contains("ERROR") || text.Contains("FAILED")) return Color.FromArgb(127, 29, 29);
            if (text.Contains("WARN") || text.Contains("NOTICE")) return Color.FromArgb(113, 63, 18);
            if (text.Contains("MOUNTED") || text.Contains("REMOTE OK") || text.Contains("ĐÃ") || text.Contains("DA ")) return Color.FromArgb(22, 101, 52);
            if (string.Equals(level, "RCLONE", StringComparison.OrdinalIgnoreCase)) return Color.FromArgb(30, 64, 175);
            if (string.Equals(level, "WEB", StringComparison.OrdinalIgnoreCase)) return Color.FromArgb(91, 33, 182);
            if (string.Equals(level, "TUNNEL", StringComparison.OrdinalIgnoreCase)) return Color.FromArgb(21, 94, 117);
            return Color.FromArgb(51, 65, 85);
        }

        private Color StatusLineBackColor(string level, string message)
        {
            var text = ((level ?? "") + " " + (message ?? "")).ToUpperInvariant();
            if (text.Contains("CRITICAL") || text.Contains("ERROR") || text.Contains("FAILED")) return Color.FromArgb(254, 226, 226);
            if (text.Contains("WARN") || text.Contains("NOTICE")) return Color.FromArgb(254, 243, 199);
            if (text.Contains("MOUNTED") || text.Contains("REMOTE OK") || text.Contains("ĐÃ") || text.Contains("DA ")) return Color.FromArgb(220, 252, 231);
            if (string.Equals(level, "RCLONE", StringComparison.OrdinalIgnoreCase)) return Color.FromArgb(219, 234, 254);
            if (string.Equals(level, "WEB", StringComparison.OrdinalIgnoreCase)) return Color.FromArgb(237, 233, 254);
            if (string.Equals(level, "TUNNEL", StringComparison.OrdinalIgnoreCase)) return Color.FromArgb(207, 250, 254);
            return Color.FromArgb(238, 241, 245);
        }

        private Color LogLineColor(string level, string message)
        {
            var text = ((level ?? "") + " " + (message ?? "")).ToUpperInvariant();
            if (text.Contains("CRITICAL") || text.Contains("ERROR") || text.Contains("FAILED")) return Color.FromArgb(252, 165, 165);
            if (text.Contains("WARN") || text.Contains("NOTICE")) return Color.FromArgb(253, 224, 71);
            if (text.Contains("MOUNTED") || text.Contains("REMOTE OK") || text.Contains("ĐÃ") || text.Contains("DA ")) return Color.FromArgb(134, 239, 172);
            if (string.Equals(level, "RCLONE", StringComparison.OrdinalIgnoreCase)) return Color.FromArgb(147, 197, 253);
            if (string.Equals(level, "WEB", StringComparison.OrdinalIgnoreCase)) return Color.FromArgb(196, 181, 253);
            if (string.Equals(level, "TUNNEL", StringComparison.OrdinalIgnoreCase)) return Color.FromArgb(125, 211, 252);
            return Color.FromArgb(203, 213, 225);
        }

        private void TrimLogLines()
        {
            if (logBox == null || logBox.Lines.Length <= MaxLogLines) return;
            var lines = logBox.Lines;
            var keep = lines.Skip(Math.Max(0, lines.Length - MaxLogLines)).ToArray();
            logBox.Clear();
            logBox.SelectionColor = Color.FromArgb(148, 163, 184);
            logBox.AppendText(DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss") + "  INFO  Đã rút gọn log, chỉ giữ " + MaxLogLines + " dòng mới nhất." + Environment.NewLine);
            logBox.SelectionColor = logBox.ForeColor;
            logBox.AppendText(string.Join(Environment.NewLine, keep).TrimEnd() + Environment.NewLine);
        }

        private void CopyLog()
        {
            if (logBox == null || string.IsNullOrEmpty(logBox.Text)) return;
            Clipboard.SetText(logBox.Text);
            AddLog("Đã copy log.");
        }

        private void ClearLog()
        {
            if (logBox == null) return;
            logBox.Clear();
        }

        private void ShowErrorLog()
        {
            if (logBox == null) return;
            var lines = logBox.Lines.Where(l => l.IndexOf("ERROR", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                l.IndexOf("WARN", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                l.IndexOf("CRITICAL", StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
            if (lines.Length == 0)
            {
                MessageBox.Show("Không có dòng ERROR/WARN trong log hiện tại.", "Log rclone", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Clipboard.SetText(string.Join(Environment.NewLine, lines));
            MessageBox.Show("Đã copy " + lines.Length + " dòng lỗi/cảnh báo vào clipboard.", "Log rclone", MessageBoxButtons.OK, MessageBoxIcon.Information);
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

            if (!IsWinFspInstalled())
                AddLog("Chưa phát hiện WinFsp. Mount rclone trên Windows cần WinFsp để tạo ổ đĩa.", "WARN");
        }

        private bool IsWinFspInstalled()
        {
            var winfspPaths = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "WinFsp", "bin", "winfsp-x64.dll"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WinFsp", "bin", "winfsp-x64.dll"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "winfsp-x64.dll")
            };
            if (winfspPaths.Any(File.Exists)) return true;
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WinFsp"))
                    if (key != null) return true;
                using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\WinFsp"))
                    return key != null;
            }
            catch
            {
                return false;
            }
        }

        private void OpenWinFspDownload()
        {
            try
            {
                Process.Start(new ProcessStartInfo("https://winfsp.dev/rel/") { UseShellExecute = true });
                AddLog("Đã mở trang tải WinFsp: https://winfsp.dev/rel/");
            }
            catch (Exception ex)
            {
                AddLog("Không mở được trang tải WinFsp: " + ex.Message, "ERROR");
            }
        }

        private async Task<bool> EnsureWinFspAvailableAsync(bool showAlreadyInstalled)
        {
            if (IsWinFspInstalled())
            {
                if (showAlreadyInstalled) AddLog("WinFsp đã được cài.");
                return true;
            }

            var confirm = MessageBox.Show(
                "Máy chưa có WinFsp nên rclone không tạo được ổ đĩa.\r\n\r\nTải và cài WinFsp tự động bây giờ?\r\n\r\nWindows sẽ hiện UAC để cấp quyền cài driver.",
                "Cài WinFsp",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (confirm != DialogResult.Yes)
            {
                AddLog("Người dùng bỏ qua cài WinFsp.", "WARN");
                return false;
            }

            await DownloadAndInstallWinFspAsync();
            if (IsWinFspInstalled())
            {
                AddLog("WinFsp đã cài xong. Có thể mount ổ rclone.");
                return true;
            }

            AddLog("Chưa xác nhận được WinFsp sau khi cài. Nếu installer vừa chạy xong, hãy mở lại app hoặc khởi động lại Windows nếu cần.", "ERROR");
            return false;
        }

        private async Task DownloadAndInstallWinFspAsync()
        {
            var url = await GetLatestWinFspInstallerUrlAsync();
            var tempRoot = Path.Combine(Path.GetTempPath(), "RcloneDriveManager", "WinFsp-" + Guid.NewGuid().ToString("N"));
            var msiPath = Path.Combine(tempRoot, "winfsp.msi");
            try
            {
                Directory.CreateDirectory(tempRoot);
                statusLabel.Text = "Đang tải WinFsp...";
                AddLog("Tải WinFsp từ " + url);
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                using (var client = new WebClient())
                {
                    client.Headers.Add("User-Agent", "RcloneDriveManager");
                    await client.DownloadFileTaskAsync(new Uri(url), msiPath);
                }

                statusLabel.Text = "Đang cài WinFsp...";
                AddLog("Chạy installer WinFsp. Windows có thể hỏi quyền Administrator.");
                var psi = new ProcessStartInfo
                {
                    FileName = "msiexec.exe",
                    Arguments = "/i " + QuoteIfNeeded(msiPath) + " /passive /norestart",
                    UseShellExecute = true,
                    Verb = "runas"
                };
                using (var proc = Process.Start(psi))
                {
                    if (proc != null)
                    {
                        await Task.Run(() => proc.WaitForExit());
                        AddLog("WinFsp installer exit code: " + proc.ExitCode, proc.ExitCode == 0 ? "INFO" : "WARN");
                    }
                }
                statusLabel.Text = "Đã chạy cài WinFsp";
            }
            catch (Win32Exception ex)
            {
                statusLabel.Text = "Cài WinFsp bị hủy";
                AddLog("Không chạy được installer WinFsp: " + ex.Message, "ERROR");
            }
            catch (Exception ex)
            {
                statusLabel.Text = "Cài WinFsp lỗi";
                AddLog("Cài WinFsp thất bại: " + ex.Message, "ERROR");
                if (MessageBox.Show("Cài WinFsp tự động thất bại:\r\n" + ex.Message + "\r\n\r\nMở trang tải thủ công?", "Cài WinFsp", MessageBoxButtons.YesNo, MessageBoxIcon.Error) == DialogResult.Yes)
                    OpenWinFspDownload();
            }
            finally
            {
                try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true); } catch { }
            }
        }

        private async Task<string> GetLatestWinFspInstallerUrlAsync()
        {
            const string apiUrl = "https://api.github.com/repos/winfsp/winfsp/releases/latest";
            using (var client = new WebClient())
            {
                client.Headers.Add("User-Agent", "RcloneDriveManager");
                var json = await client.DownloadStringTaskAsync(apiUrl);
                var data = _json.DeserializeObject(json) as Dictionary<string, object>;
                if (data != null && data.ContainsKey("assets"))
                {
                    var assets = data["assets"] as object[];
                    if (assets != null)
                    {
                        foreach (var item in assets)
                        {
                            var asset = item as Dictionary<string, object>;
                            if (asset == null) continue;
                            var name = Convert.ToString(asset.ContainsKey("name") ? asset["name"] : "");
                            var download = Convert.ToString(asset.ContainsKey("browser_download_url") ? asset["browser_download_url"] : "");
                            if (name.StartsWith("winfsp-", StringComparison.OrdinalIgnoreCase) &&
                                name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) &&
                                !name.Contains("tests") &&
                                !string.IsNullOrWhiteSpace(download))
                                return download;
                        }
                    }
                }
            }
            throw new InvalidOperationException("Không tìm thấy file MSI WinFsp mới nhất trên GitHub release.");
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

        private async Task CheckForAppUpdateAsync(bool manual)
        {
            try
            {
                if (!File.Exists(Application.ExecutablePath))
                {
                    if (manual) AddLog("Không tìm thấy file app hiện tại để kiểm tra cập nhật.", "ERROR");
                    return;
                }

                AddLog(manual ? "Đang kiểm tra cập nhật app..." : "Tự kiểm tra cập nhật app...");
                var tempRoot = Path.Combine(Path.GetTempPath(), "RcloneDriveManager", "Update-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempRoot);
                var newExe = Path.Combine(tempRoot, "RcloneDrive.exe");
                var update = await GetLatestAppUpdateAsync();
                if (!IsVersionNewer(update.Version, AppVersion))
                {
                    TryDeleteDirectory(tempRoot);
                    var message = "Không cập nhật: GitHub đang có " + update.Version + ", không mới hơn bản đang chạy v" + AppVersion + ".";
                    AddLog(message, "WARN");
                    if (manual) MessageBox.Show(message, "Cập nhật", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                AddLog("Tải file cập nhật từ GitHub...");

                using (var client = CreateWebClient())
                {
                    await client.DownloadFileTaskAsync(new Uri(update.DownloadUrl), newExe);
                }

                if (new FileInfo(newExe).Length < 50000)
                {
                    TryDeleteDirectory(tempRoot);
                    AddLog("File cập nhật tải về không hợp lệ.", "ERROR");
                    return;
                }

                var currentHash = FileSha256(Application.ExecutablePath);
                var newHash = FileSha256(newExe);
                AddLog("Đã tải file cập nhật. SHA hiện tại: " + currentHash.Substring(0, 8) + ", SHA mới: " + newHash.Substring(0, 8));
                if (string.Equals(currentHash, newHash, StringComparison.OrdinalIgnoreCase))
                {
                    TryDeleteDirectory(tempRoot);
                    if (manual) MessageBox.Show("Bạn đang dùng bản mới nhất.", "Cập nhật", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    else AddLog("App đang ở bản mới nhất.");
                    return;
                }

                var confirm = MessageBox.Show(
                    "Có bản RcloneDrive.exe mới trên GitHub.\r\n\r\nCập nhật và mở lại app bây giờ?",
                    "Cập nhật app",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);
                if (confirm != DialogResult.Yes)
                {
                    TryDeleteDirectory(tempRoot);
                    AddLog("Đã bỏ qua cập nhật app.", "WARN");
                    return;
                }

                StartSelfUpdater(newExe, tempRoot);
                Close();
            }
            catch (Exception ex)
            {
                if (manual)
                    MessageBox.Show("Kiểm tra/cập nhật app thất bại:\r\n" + ex.Message, "Cập nhật", MessageBoxButtons.OK, MessageBoxIcon.Error);
                AddLog("Cập nhật app thất bại: " + ex.Message, manual ? "ERROR" : "WARN");
            }
        }

        private async Task CheckForAppUpdateOnStartupAsync()
        {
            try
            {
                await Task.Delay(6000);
                if (IsDisposed || !IsHandleCreated) return;
                await CheckForAppUpdateAsync(false);
            }
            catch (Exception ex)
            {
                AddLog("Tự kiểm tra cập nhật app thất bại: " + ex.Message, "WARN");
            }
        }

        private string FileSha256(string file)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(file))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
        }

        private async Task<string> GetLatestAppExeUrlAsync()
        {
            using (var client = CreateWebClient())
            {
                var json = await client.DownloadStringTaskAsync(AppUpdateCommitApiUrl);
                var data = _json.DeserializeObject(json) as Dictionary<string, object>;
                var sha = Convert.ToString(data != null && data.ContainsKey("sha") ? data["sha"] : "");
                if (string.IsNullOrWhiteSpace(sha))
                    throw new InvalidOperationException("Không lấy được commit mới nhất từ GitHub.");
                AddLog("Commit mới nhất trên GitHub: " + sha.Substring(0, Math.Min(8, sha.Length)));
                return "https://raw.githubusercontent.com/luffyorhuymv/rclone-gui/" + sha + "/RcloneDrive.exe";
            }
        }

        private async Task<AppUpdateInfo> GetLatestAppUpdateAsync()
        {
            using (var client = CreateWebClient())
            {
                var json = await client.DownloadStringTaskAsync(AppUpdateReleaseApiUrl);
                var data = _json.DeserializeObject(json) as Dictionary<string, object>;
                if (data == null)
                    throw new InvalidOperationException("Không đọc được thông tin release mới nhất từ GitHub.");

                var tag = GetJsonString(data, "tag_name");
                if (string.IsNullOrWhiteSpace(tag))
                    throw new InvalidOperationException("Release mới nhất không có tag version.");

                var assets = data.ContainsKey("assets") ? data["assets"] as object[] : null;
                string exeUrl = "";
                if (assets != null)
                {
                    foreach (var item in assets)
                    {
                        var asset = item as Dictionary<string, object>;
                        if (asset == null) continue;
                        var name = GetJsonString(asset, "name");
                        if (!string.Equals(name, "RcloneDrive.exe", StringComparison.OrdinalIgnoreCase)) continue;
                        exeUrl = GetJsonString(asset, "browser_download_url");
                        if (!string.IsNullOrWhiteSpace(exeUrl)) break;
                    }
                }

                if (string.IsNullOrWhiteSpace(exeUrl))
                    throw new InvalidOperationException("Release " + tag + " không có asset RcloneDrive.exe.");

                AddLog("GitHub release mới nhất: " + tag);
                return new AppUpdateInfo
                {
                    Version = NormalizeVersionTag(tag),
                    DownloadUrl = exeUrl,
                    PageUrl = GetJsonString(data, "html_url")
                };
            }
        }

        private string GetJsonString(Dictionary<string, object> data, string key)
        {
            object value;
            return data != null && data.TryGetValue(key, out value) ? Convert.ToString(value) : "";
        }

        private string NormalizeVersionTag(string value)
        {
            value = (value ?? "").Trim();
            return value.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? value.Substring(1) : value;
        }

        private bool IsVersionNewer(string candidate, string current)
        {
            Version candidateVersion;
            Version currentVersion;
            if (!Version.TryParse(NormalizeVersionTag(candidate), out candidateVersion)) return false;
            if (!Version.TryParse(NormalizeVersionTag(current), out currentVersion)) return true;
            return candidateVersion > currentVersion;
        }

        private WebClient CreateWebClient()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var client = new TimedWebClient(30000);
            client.Headers.Add("User-Agent", "RcloneDriveManager");
            client.Headers.Add("Cache-Control", "no-cache");
            client.Headers.Add("Pragma", "no-cache");
            return client;
        }

        private sealed class TimedWebClient : WebClient
        {
            private readonly int _timeoutMs;

            public TimedWebClient(int timeoutMs)
            {
                _timeoutMs = timeoutMs;
            }

            protected override WebRequest GetWebRequest(Uri address)
            {
                var request = base.GetWebRequest(address);
                if (request != null)
                    request.Timeout = _timeoutMs;
                return request;
            }
        }

        private void StartSelfUpdater(string newExe, string tempRoot)
        {
            var currentExe = Application.ExecutablePath;
            var script = Path.Combine(tempRoot, "update-rclonedrive.cmd");
            var lines = new[]
            {
                "@echo off",
                "setlocal",
                "set \"SRC=" + newExe + "\"",
                "set \"DST=" + currentExe + "\"",
                "set \"APPDIR=" + _appDir + "\"",
                "set \"TEMPROOT=" + tempRoot + "\"",
                "timeout /t 2 /nobreak >nul",
                ":retry",
                "copy /y \"%SRC%\" \"%DST%\" >nul",
                "if errorlevel 1 (timeout /t 1 /nobreak >nul & goto retry)",
                "start \"\" \"%DST%\"",
                "timeout /t 2 /nobreak >nul",
                "rd /s /q \"%TEMPROOT%\"",
                "exit /b 0"
            };
            File.WriteAllLines(script, lines, Encoding.ASCII);
            AddLog("Đang cập nhật app. App sẽ đóng và mở lại.");
            Process.Start(new ProcessStartInfo
            {
                FileName = script,
                WorkingDirectory = _appDir,
                CreateNoWindow = true,
                UseShellExecute = false
            });
        }

        private void TryDeleteDirectory(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
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
            RepairProfileLocalWorkDirsOnLoad();
            RenderProfiles();
        }

        private void RepairProfileLocalWorkDirsOnLoad()
        {
            var changed = false;
            foreach (var p in _profiles)
            {
                var before = p.LocalWorkDir ?? "";
                if (RepairProfileLocalWorkDir(p))
                {
                    changed = true;
                    AddLog("Đã sửa thư mục local cho profile " + p.Name + ": " + before + " -> " + p.LocalWorkDir, "WARN");
                }
            }
            if (changed) SaveProfiles();
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
            if (profileList.Columns.Count > 0)
                profileList.Columns[0].Width = Math.Max(360, profileList.ClientSize.Width - 22);
            profileList.Items.Clear();
            foreach (var p in _profiles)
            {
                var item = new ListViewItem(p.Name);
                var mounted = IsMountedProfile(p);
                item.ToolTipText = p.Name + " - " + DriveDisplay(p) + " - " + (mounted ? "Đang kết nối" : "Chưa kết nối");
                item.Tag = p;
                profileList.Items.Add(item);
            }
            foreach (var drive in _mountedExternalDrives)
            {
                var item = new ListViewItem(drive.Name);
                item.ToolTipText = drive.Name + " - " + drive.DriveLetter + " - đang bật";
                item.Tag = drive;
                profileList.Items.Add(item);
            }
            UpdateConnectButtonState();
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
                !DetectedDriveBelongsToProfile(detected) &&
                !_mountedExternalDrives.Any(d => string.Equals(d.DriveLetter, detected.DriveLetter, StringComparison.OrdinalIgnoreCase)))
            {
                _mountedExternalDrives.Add(detected);
            }
        }

        private bool DetectedDriveBelongsToProfile(MountedDriveInfo detected)
        {
            if (detected == null) return false;
            foreach (var profile in _profiles)
            {
                if (profile == null) continue;
                if (!IsAutoDrive(profile.DriveLetter) &&
                    string.Equals(NormalizeDriveChoice(profile.DriveLetter), NormalizeDriveChoice(detected.DriveLetter), StringComparison.OrdinalIgnoreCase))
                    return true;
                if (string.Equals(NormalizeSourceForCompare(detected.DisplayRoot), NormalizeSourceForCompare(profile.Source), StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
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
            _newProfileDraft = false;
            _loadingProfileFields = true;
            try
            {
                nameBox.Text = p.Name;
                SelectComboValue(remoteCombo, p.Remote);
                pathBox.Text = DriveProfile.NormalizeRemotePath(p.RemotePath, p.Remote);
                SelectComboValue(driveCombo, p.DriveLetter);
                SelectComboValue(cacheModeCombo, p.CacheMode);
                cacheDirBox.Text = p.CacheDir;
                p.LocalWorkDir = NormalizeLocalWorkDir(p);
                cacheMaxAgeBox.Text = string.IsNullOrWhiteSpace(p.VfsCacheMaxAge) ? "72h" : p.VfsCacheMaxAge;
                writeBackBox.Text = string.IsNullOrWhiteSpace(p.VfsWriteBack) ? "5s" : p.VfsWriteBack;
                SelectComboValue(mountPresetCombo, string.IsNullOrWhiteSpace(p.MountPreset) ? "Nhanh/RaiDrive" : p.MountPreset);
                readOnlyBox.Checked = p.ReadOnly;
                autoMountBox.Checked = p.AutoMount;
                networkModeBox.Checked = p.NetworkMode;
                transfersBox.Value = Math.Max(transfersBox.Minimum, Math.Min(transfersBox.Maximum, p.Transfers <= 0 ? 4 : p.Transfers));
                bufferBox.Value = Math.Max(bufferBox.Minimum, Math.Min(bufferBox.Maximum, p.BufferSizeMb <= 0 ? 32 : p.BufferSizeMb));
                tunnelEnabledBox.Checked = p.TunnelEnabled || !string.IsNullOrWhiteSpace(p.TunnelCommand);
                tunnelPortBox.Value = Math.Max(tunnelPortBox.Minimum, Math.Min(tunnelPortBox.Maximum, p.TunnelLocalPort));
                tunnelCommandBox.Text = p.TunnelCommand ?? "";
                extraArgsBox.Text = p.ExtraArgs ?? "";
                codeWorkspaceAutoUploadBox.Checked = p.CodeWorkspaceEnabled;
                codeWorkspaceSkipNewerRemoteBox.Checked = p.CodeWorkspaceSkipNewerRemote;
                codeWorkspaceDelayBox.Value = Math.Max(codeWorkspaceDelayBox.Minimum, Math.Min(codeWorkspaceDelayBox.Maximum, p.CodeWorkspaceDelaySeconds <= 0 ? 2 : p.CodeWorkspaceDelaySeconds));
                codeWorkspaceIgnoreBox.Text = string.IsNullOrWhiteSpace(p.CodeWorkspaceIgnores) ? DriveProfile.DefaultCodeWorkspaceIgnores : p.CodeWorkspaceIgnores;
                UpdateCodeWorkspaceStatusLabel(p);
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
            if (p == null)
            {
                if (_newProfileDraft && SelectedMountedDrive == null)
                    CreateProfileFromCurrentFields(null, true, true);
                return;
            }
            _newProfileDraft = false;
            p.Name = string.IsNullOrWhiteSpace(nameBox.Text) ? "Ổ đĩa" : nameBox.Text.Trim();
            p.Remote = Convert.ToString(remoteCombo.SelectedItem ?? remoteCombo.Text ?? "").Trim();
            p.RemotePath = DriveProfile.NormalizeRemotePath(pathBox.Text, p.Remote);
            var driveChoice = NormalizeDriveChoice(Convert.ToString(driveCombo.SelectedItem ?? driveCombo.Text ?? "AUTO"));
            if (IsDriveReservedByAnotherProfile(driveChoice, p))
            {
                MessageBox.Show("Ký tự ổ " + driveChoice + " đang được profile khác trong app giữ. Hãy chọn ổ khác hoặc Tự chọn ổ trống.", "Trùng ký tự ổ", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            p.DriveLetter = driveChoice;
            p.CacheMode = Convert.ToString(cacheModeCombo.SelectedItem ?? "full");
            p.CacheDir = cacheDirBox.Text.Trim();
            p.LocalWorkDir = NormalizeLocalWorkDir(p);
            p.VfsCacheMaxAge = string.IsNullOrWhiteSpace(cacheMaxAgeBox.Text) ? "72h" : cacheMaxAgeBox.Text.Trim();
            p.VfsWriteBack = string.IsNullOrWhiteSpace(writeBackBox.Text) ? "5s" : writeBackBox.Text.Trim();
            p.ReadOnly = readOnlyBox.Checked;
            p.AutoMount = autoMountBox.Checked;
            p.NetworkMode = networkModeBox.Checked;
            p.Transfers = (int)transfersBox.Value;
            p.BufferSizeMb = (int)bufferBox.Value;
            p.MountPreset = Convert.ToString(mountPresetCombo.SelectedItem ?? mountPresetCombo.Text ?? "Nhanh/RaiDrive");
            p.TunnelEnabled = tunnelEnabledBox.Checked;
            p.TunnelLocalPort = (int)tunnelPortBox.Value;
            p.TunnelCommand = tunnelCommandBox.Text.Trim();
            p.ExtraArgs = extraArgsBox.Text.Trim();
            p.CodeWorkspaceEnabled = codeWorkspaceAutoUploadBox.Checked;
            p.CodeWorkspaceSkipNewerRemote = codeWorkspaceSkipNewerRemoteBox.Checked;
            p.CodeWorkspaceDelaySeconds = (int)codeWorkspaceDelayBox.Value;
            p.CodeWorkspaceIgnores = string.IsNullOrWhiteSpace(codeWorkspaceIgnoreBox.Text) ? DriveProfile.DefaultCodeWorkspaceIgnores : codeWorkspaceIgnoreBox.Text.Trim();
            if (!p.CodeWorkspaceEnabled && IsCodeWorkspaceWatcherRunning(p))
                StopCodeWorkspaceWatcher(p);
            UpdateCodeWorkspaceStatusLabel(p);
            SaveProfiles();
            RenderProfiles();
            SelectProfile(p);
            AddLog("Đã lưu profile: " + p.Name);
        }

        private void ApplyCodeIdePreset()
        {
            SelectComboValue(cacheModeCombo, "full");
            SelectComboValue(mountPresetCombo, "OpenCode");
            cacheMaxAgeBox.Text = "168h";
            writeBackBox.Text = "1s";
            transfersBox.Value = Math.Max(transfersBox.Minimum, Math.Min(transfersBox.Maximum, 1));
            bufferBox.Value = Math.Max(bufferBox.Minimum, Math.Min(bufferBox.Maximum, 16));
            networkModeBox.Checked = true;
            readOnlyBox.Checked = false;

            SaveCurrentProfile();
            AddLog("Đã áp dụng preset OpenCode: cache full, ít kết nối FTP hơn, đọc project ổn định hơn.");
        }

        private void NewProfile()
        {
            StartNewProfileDraft();
        }

        private void StartNewProfileDraft()
        {
            _newProfileDraft = true;
            profileList.SelectedItems.Clear();
            _loadingProfileFields = true;
            try
            {
                nameBox.Text = "";
                remoteCombo.SelectedIndex = -1;
                pathBox.Text = "/";
                SelectComboValue(driveCombo, "Tự chọn ổ trống");
                SelectComboValue(cacheModeCombo, "full");
                cacheDirBox.Text = "%USERPROFILE%\\.cache\\rclone";
                cacheMaxAgeBox.Text = "72h";
                writeBackBox.Text = "5s";
                SelectComboValue(mountPresetCombo, "Nhanh/RaiDrive");
                readOnlyBox.Checked = false;
                autoMountBox.Checked = false;
                networkModeBox.Checked = true;
                transfersBox.Value = Math.Max(transfersBox.Minimum, Math.Min(transfersBox.Maximum, 4));
                bufferBox.Value = Math.Max(bufferBox.Minimum, Math.Min(bufferBox.Maximum, 32));
                tunnelEnabledBox.Checked = false;
                tunnelPortBox.Value = 0;
                tunnelCommandBox.Text = "";
                extraArgsBox.Text = "";
                codeWorkspaceAutoUploadBox.Checked = false;
                codeWorkspaceSkipNewerRemoteBox.Checked = true;
                codeWorkspaceDelayBox.Value = Math.Max(codeWorkspaceDelayBox.Minimum, Math.Min(codeWorkspaceDelayBox.Maximum, 2));
                codeWorkspaceIgnoreBox.Text = DriveProfile.DefaultCodeWorkspaceIgnores;
                codeWorkspaceStatusLabel.Text = "Code Workspace: tắt";
                _profileNameEditedByUser = false;
            }
            finally
            {
                _loadingProfileFields = false;
            }
            statusLabel.Text = "Đang tạo ổ mới: nhập tên và chọn remote";
            AddLog("Đã mở form ổ mới. Hãy nhập tên profile và chọn remote/config trước khi lưu hoặc kết nối.");
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
            if (MessageBox.Show("Xóa profile \"" + p.Name + "\" khỏi app? Rclone config " + p.Remote + " vẫn được giữ lại.", "Xóa profile", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;
            StopCodeWorkspaceWatcher(p);
            _profiles.Remove(p);
            SaveProfiles();
            RenderProfiles();
            SelectFirstProfile();
            AddLog("Đã xóa profile khỏi app: " + p.Name + " (không xóa rclone config).");
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

        private string GetDefaultLocalWorkDir(string profileName)
        {
            return Path.Combine(GetLocalWorkspaceRoot(), SafeWorkspaceName(profileName));
        }

        private string GetLocalWorkspaceRoot()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RcloneWorkspaces");
        }

        private string SafeWorkspaceName(string profileName)
        {
            return string.IsNullOrWhiteSpace(profileName) ? "rclone" : Regex.Replace(profileName.Trim(), @"[^\w\-\. ]+", "_");
        }

        private string NormalizeLocalWorkDir(DriveProfile p)
        {
            if (p == null) return "";
            if (!string.IsNullOrWhiteSpace(p.LocalWorkDir))
            {
                var local = Environment.ExpandEnvironmentVariables(p.LocalWorkDir.Trim());
                if (!IsStaleDefaultWorkspace(p, local))
                    return local;
            }
            return GetDefaultLocalWorkDir(p.Name);
        }

        private bool RepairProfileLocalWorkDir(DriveProfile p)
        {
            if (p == null) return false;
            var normalized = NormalizeLocalWorkDir(p);
            if (string.Equals(p.LocalWorkDir ?? "", normalized, StringComparison.OrdinalIgnoreCase)) return false;
            p.LocalWorkDir = normalized;
            return true;
        }

        private bool IsStaleDefaultWorkspace(DriveProfile p, string localDir)
        {
            if (p == null || string.IsNullOrWhiteSpace(localDir)) return false;
            try
            {
                var full = Path.GetFullPath(localDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var root = Path.GetFullPath(GetLocalWorkspaceRoot()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
                var leaf = Path.GetFileName(full);
                var expected = SafeWorkspaceName(p.Name);
                return !string.Equals(leaf, expected, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private void EnsureLocalWorkspace(DriveProfile p)
        {
            var localDir = NormalizeLocalWorkDir(p);
            if (string.IsNullOrWhiteSpace(localDir)) throw new InvalidOperationException("Không xác định được thư mục local.");
            Directory.CreateDirectory(localDir);
            p.LocalWorkDir = localDir;
            SaveProfiles();
        }

        private void OpenLocalWorkspace()
        {
            var p = SelectedProfile;
            if (p == null)
            {
                MessageBox.Show("Hãy chọn một profile trước.", "RcloneDrive", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            EnsureLocalWorkspace(p);
            Process.Start(new ProcessStartInfo("explorer.exe", QuoteIfNeeded(NormalizeLocalWorkDir(p))) { UseShellExecute = true });
            AddLog("Đã mở thư mục local: " + NormalizeLocalWorkDir(p));
        }

        private void OpenProjectFolder()
        {
            var p = SelectedProfile;
            if (p == null)
            {
                MessageBox.Show("Hãy chọn một profile trước.", "RcloneDrive", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var drive = ActiveDriveForProfile(p);
            if (string.IsNullOrWhiteSpace(drive) || !IsMounted(drive))
            {
                MessageBox.Show("Ổ này chưa mount. Hãy kết nối trước.", "RcloneDrive", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var subPath = Prompt.Show("Nhập thư mục con cần mở trên ổ mount:", "Mở thư mục project", "public_html");
            if (string.IsNullOrWhiteSpace(subPath)) return;
            subPath = subPath.Trim().Trim('\\', '/').Replace('/', '\\');
            var target = OpenCodeProjectRootForProfile(p, drive);
            if (!string.IsNullOrWhiteSpace(subPath))
                target = Path.Combine(target, subPath);
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", QuoteIfNeeded(target)) { UseShellExecute = true });
                AddLog("Đã mở thư mục project: " + target);
            }
            catch (Exception ex)
            {
                AddLog("Không mở được thư mục project: " + ex.Message, "ERROR");
            }
        }

        private void OpenProjectInOpenCode()
        {
            var p = SelectedProfile;
            if (p == null)
            {
                MessageBox.Show("Hãy chọn một profile trước.", "RcloneDrive", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var drive = ActiveDriveForProfile(p);
            if (string.IsNullOrWhiteSpace(drive) || !IsMounted(drive))
            {
                MessageBox.Show("Ổ này chưa mount. Hãy kết nối trước.", "RcloneDrive", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var subPath = Prompt.Show("Nhập thư mục project cần mở trong OpenCode:", "Mở OpenCode", SuggestedOpenCodeProjectSubPath(p));
            if (string.IsNullOrWhiteSpace(subPath)) return;
            subPath = subPath.Trim().Trim('\\', '/').Replace('/', '\\');
            if (string.IsNullOrWhiteSpace(subPath))
            {
                MessageBox.Show("Không mở trực tiếp gốc ổ mount. Hãy nhập thư mục project cụ thể, ví dụ: public_html", "RcloneDrive", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var target = OpenCodeProjectRootForProfile(p, drive);
            if (!string.IsNullOrWhiteSpace(subPath))
                target = Path.Combine(target, subPath);
            target = CanonicalOpenCodeProjectPath(target);

            if (!Directory.Exists(target))
            {
                MessageBox.Show("Thư mục project không tồn tại:\r\n" + target, "RcloneDrive", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var url = "opencode://new-session?directory=" + Uri.EscapeDataString(target);
            try
            {
                EnsureGitProjectForOpenCode(target);
                CloseOpenCodeBeforeStateRepair();
                RepairOpenCodeProjectSession(target);
                var exe = FindOpenCodeExe();
                if (!string.IsNullOrWhiteSpace(exe) && File.Exists(exe))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = exe,
                        Arguments = QuoteIfNeeded(url),
                        UseShellExecute = true
                    });
                }
                else
                {
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                }
                ScheduleOpenCodeStateRepair(target);
                AddLog("Đã mở OpenCode session cho project: " + target);
            }
            catch (Exception ex)
            {
                AddLog("Không mở được OpenCode: " + ex.Message, "ERROR");
                try
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", QuoteIfNeeded(target)) { UseShellExecute = true });
                    AddLog("Đã mở thư mục project bằng Explorer: " + target);
                }
                catch { }
            }
        }

        private void AutoFixOpenCodeForSelectedProject()
        {
            var p = SelectedProfile;
            if (p == null)
            {
                MessageBox.Show("Hãy chọn một profile trước.", "RcloneDrive", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var drive = ActiveDriveForProfile(p);
            if (string.IsNullOrWhiteSpace(drive) || !IsMounted(drive))
            {
                MessageBox.Show("Ổ này chưa mount. Hãy kết nối trước.", "RcloneDrive", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var defaultProject = SuggestedOpenCodeProjectSubPath(p);
            var subPath = Prompt.Show("Nhập thư mục project cần tự sửa OpenCode:", "Auto fix OpenCode", defaultProject);
            if (string.IsNullOrWhiteSpace(subPath)) return;
            subPath = subPath.Trim().Trim('\\', '/').Replace('/', '\\');
            if (string.IsNullOrWhiteSpace(subPath))
            {
                MessageBox.Show("Không sửa trực tiếp gốc ổ mount. Hãy nhập thư mục project cụ thể.", "RcloneDrive", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var projectDir = OpenCodeProjectRootForProfile(p, drive);
            projectDir = Path.Combine(projectDir, subPath);
            var canonicalProjectDir = CanonicalOpenCodeProjectPath(projectDir);
            try
            {
                CloseOpenCodeBeforeStateRepair();
                EnsureGitProjectForOpenCode(canonicalProjectDir);
                RepairOpenCodeProjectSession(canonicalProjectDir);
                EnsureOpenCodeWorkspaceVcs(canonicalProjectDir);
                var exe = FindOpenCodeExe();
                if (!string.IsNullOrWhiteSpace(exe) && File.Exists(exe))
                    Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
                ScheduleOpenCodeStateRepair(canonicalProjectDir);
                AddLog("Đã auto fix OpenCode project: " + canonicalProjectDir);
            }
            catch (Exception ex)
            {
                AddLog("Auto fix OpenCode thất bại: " + ex.Message, "ERROR");
            }
        }

        private void ScheduleOpenCodeStateRepair(string projectDir)
        {
            if (string.IsNullOrWhiteSpace(projectDir)) return;
            Task.Run(() =>
            {
                Thread.Sleep(2600);
                try
                {
                    RepairOpenCodeProjectSession(projectDir);
                    EnsureOpenCodeWorkspaceVcs(projectDir);
                    BeginInvoke(new Action(() => AddLog("OpenCode: đã xác nhận lại project hiện tại: " + projectDir, "INFO")));
                }
                catch (Exception ex)
                {
                    BeginInvoke(new Action(() => AddLog("OpenCode: không xác nhận lại được project: " + ex.Message, "WARN")));
                }
            });
        }

        private string SuggestedOpenCodeProjectSubPath(DriveProfile p)
        {
            var remotePath = DriveProfile.NormalizeRemotePath(p == null ? "" : p.RemotePath, p == null ? "" : p.Remote).Trim('/');
            if (remotePath.IndexOf("www/wwwroot/", StringComparison.OrdinalIgnoreCase) >= 0)
                return remotePath.Replace('/', '\\');
            if (remotePath.IndexOf("public_html", StringComparison.OrdinalIgnoreCase) >= 0)
                return remotePath.Replace('/', '\\');
            return "public_html";
        }

        private string CanonicalOpenCodeProjectPath(string projectDir)
        {
            try
            {
                var full = Path.GetFullPath(projectDir).TrimEnd('\\', '/');
                if (Regex.IsMatch(full, @"^[A-Za-z]:\\", RegexOptions.IgnoreCase))
                {
                    var displayRoot = GetDriveDisplayRoot(full.Substring(0, 2));
                    if (!string.IsNullOrWhiteSpace(displayRoot) && displayRoot.StartsWith(@"\\", StringComparison.Ordinal))
                    {
                        var relative = full.Length > 3 ? full.Substring(3).TrimStart('\\') : "";
                        return string.IsNullOrWhiteSpace(relative)
                            ? displayRoot.TrimEnd('\\')
                            : displayRoot.TrimEnd('\\') + "\\" + relative;
                    }
                }
                return full;
            }
            catch
            {
                return (projectDir ?? "").TrimEnd('\\', '/');
            }
        }

        private void CloseOpenCodeBeforeStateRepair()
        {
            var processes = Process.GetProcessesByName("OpenCode");
            if (processes.Length == 0) return;
            AddLog("Đóng OpenCode để sửa session path trước khi mở lại.");
            foreach (var proc in processes)
            {
                try
                {
                    if (proc.HasExited) continue;
                    if (proc.MainWindowHandle != IntPtr.Zero)
                        proc.CloseMainWindow();
                }
                catch { }
            }
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < deadline)
            {
                if (Process.GetProcessesByName("OpenCode").Length == 0) return;
                Thread.Sleep(200);
            }
            foreach (var proc in Process.GetProcessesByName("OpenCode"))
            {
                try
                {
                    if (!proc.HasExited)
                        proc.Kill();
                }
                catch { }
            }
            Thread.Sleep(700);
        }

        private void EnsureGitProjectForOpenCode(string projectDir)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(projectDir) || !Directory.Exists(projectDir)) return;
                var gitDir = Path.Combine(projectDir, ".git");
                if ((Directory.Exists(gitDir) || File.Exists(gitDir)) && IsValidGitProject(projectDir))
                {
                    EnsureOpenCodeWorkspaceVcs(projectDir);
                    return;
                }
                if (Directory.Exists(gitDir) || File.Exists(gitDir))
                    AddLog("OpenCode: .git đang tồn tại nhưng Git không nhận repo, sẽ thử sửa lại metadata.", "WARN");

                if (!RunGit(projectDir, "init -b main"))
                {
                    AddLog("Không thể sửa Git cho OpenCode. Ổ mount có thể chặn ghi .git/refs; lịch sử OpenCode có thể không ổn định.", "WARN");
                    return;
                }

                RunGit(projectDir, "config user.name \"RcloneDrive\"");
                RunGit(projectDir, "config user.email \"rclonedrive@local\"");

                if (RunGit(projectDir, "commit --allow-empty -m \"Initialize repository\""))
                    AddLog("Đã khởi tạo Git cho OpenCode: " + projectDir);
                else
                    AddLog("Đã git init nhưng chưa tạo được commit đầu tiên: " + projectDir, "WARN");
                EnsureOpenCodeWorkspaceVcs(projectDir);
            }
            catch (Exception ex)
            {
                AddLog("Không tự khởi tạo được Git cho OpenCode: " + ex.Message, "WARN");
            }
        }

        private bool IsValidGitProject(string projectDir)
        {
            return RunGit(projectDir, "rev-parse --is-inside-work-tree", false);
        }

        private bool RunGit(string workingDir, string arguments)
        {
            return RunGit(workingDir, arguments, true);
        }

        private bool RunGit(string workingDir, string arguments, bool logErrors)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "git.exe",
                    Arguments = arguments,
                    WorkingDirectory = workingDir,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using (var proc = Process.Start(psi))
                {
                    if (proc == null) return false;
                    var output = proc.StandardOutput.ReadToEnd();
                    var error = proc.StandardError.ReadToEnd();
                    if (!proc.WaitForExit(20000))
                    {
                        try { proc.Kill(); } catch { }
                        if (logErrors) AddLog("Git timeout: git " + arguments, "WARN");
                        return false;
                    }
                    if (proc.ExitCode != 0)
                    {
                        var detail = string.IsNullOrWhiteSpace(error) ? output : error;
                        if (logErrors) AddLog("Git lỗi: git " + arguments + " | " + detail.Trim(), "WARN");
                        return false;
                    }
                    return true;
                }
            }
            catch (Exception ex)
            {
                if (logErrors) AddLog("Không chạy được git.exe: " + ex.Message, "WARN");
                return false;
            }
        }

        private void EnsureOpenCodeWorkspaceVcs(string projectDir)
        {
            try
            {
                var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ai.opencode.desktop");
                if (!Directory.Exists(appData)) return;
                var prefix = "opencode.workspace." + OpenCodeWorkspaceSlug(projectDir) + ".";
                var workspaceFile = Directory.GetFiles(appData, "opencode.workspace.*.dat")
                    .Where(f => Path.GetFileName(f).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
                if (string.IsNullOrWhiteSpace(workspaceFile)) return;

                var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                var root = serializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(workspaceFile, Encoding.UTF8)) ??
                           new Dictionary<string, object>();
                root["workspace:vcs"] = serializer.Serialize(new Dictionary<string, object>
                {
                    { "value", new Dictionary<string, object> { { "branch", "main" }, { "default_branch", "main" } } }
                });
                File.WriteAllText(workspaceFile, serializer.Serialize(root), new UTF8Encoding(false));
                AddLog("Đã cập nhật OpenCode workspace branch main: " + projectDir);
            }
            catch (Exception ex)
            {
                AddLog("Không cập nhật được OpenCode workspace VCS: " + ex.Message, "WARN");
            }
        }

        private string FindRecentOpenCodeSessionIdForProject(Dictionary<string, object> globalRoot, string projectDir)
        {
            try
            {
                var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                object raw;
                if (globalRoot != null && globalRoot.TryGetValue("notification", out raw))
                {
                    var notification = serializer.Deserialize<Dictionary<string, object>>(Convert.ToString(raw) ?? "{}");
                    object listRaw;
                    var list = notification != null && notification.TryGetValue("list", out listRaw) ? listRaw as ArrayList : null;
                    if (list != null)
                    {
                        foreach (var item in list.Cast<object>().Reverse())
                        {
                            var row = item as Dictionary<string, object>;
                            if (row == null) continue;
                            var directory = row.ContainsKey("directory") ? Convert.ToString(row["directory"]) : "";
                            var session = row.ContainsKey("session") ? Convert.ToString(row["session"]) : "";
                            var type = row.ContainsKey("type") ? Convert.ToString(row["type"]) : "";
                            if (session.StartsWith("ses_", StringComparison.OrdinalIgnoreCase) &&
                                SameMountedProjectPath(directory, projectDir) &&
                                string.Equals(type, "turn-complete", StringComparison.OrdinalIgnoreCase))
                                return session;
                        }
                    }
                }
            }
            catch
            {
            }
            return FindRecentOpenCodeWorkspaceSessionId();
        }

        private string FindRecentOpenCodeWorkspaceSessionId()
        {
            try
            {
                var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ai.opencode.desktop");
                if (!Directory.Exists(appData)) return "";
                var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                foreach (var file in Directory.GetFiles(appData, "opencode.workspace.*.dat").OrderByDescending(File.GetLastWriteTimeUtc).Take(16))
                {
                    var root = serializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(file, Encoding.UTF8));
                    object raw;
                    if (root == null || !root.TryGetValue("workspace:model-selection", out raw)) continue;
                    var model = serializer.Deserialize<Dictionary<string, object>>(Convert.ToString(raw) ?? "{}");
                    object sessionsRaw;
                    if (model == null || !model.TryGetValue("session", out sessionsRaw)) continue;
                    var sessions = sessionsRaw as Dictionary<string, object>;
                    if (sessions == null || sessions.Count == 0) continue;
                    return sessions.Keys.FirstOrDefault(k => k.StartsWith("ses_", StringComparison.OrdinalIgnoreCase)) ?? "";
                }
            }
            catch
            {
            }
            return "";
        }

        private string OpenCodeWorkspaceSlug(string projectDir)
        {
            var normalized = (projectDir ?? "").Replace('/', '\\').TrimEnd('\\');
            normalized = Regex.Replace(normalized, @"[^A-Za-z0-9]", "-").Trim('-');
            return normalized.Length > 12 ? normalized.Substring(0, 12) : normalized;
        }

        private void RepairOpenCodeProjectSession(string projectDir)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(projectDir)) return;
                projectDir = Path.GetFullPath(projectDir).TrimEnd('\\', '/');
                var stateFile = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "ai.opencode.desktop",
                    "opencode.global.dat");
                if (!File.Exists(stateFile)) return;

                var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                var root = serializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(stateFile, Encoding.UTF8));
                if (root == null) return;

                var server = ParseJsonObject(serializer, root, "server");
                var page = ParseJsonObject(serializer, root, "layout.page");
                if (server == null || page == null) return;

                var projects = GetOrCreateObject(server, "projects");
                var local = GetOrCreateList(projects, "local");
                for (var i = local.Count - 1; i >= 0; i--)
                {
                    var item = local[i] as Dictionary<string, object>;
                    var worktree = item != null && item.ContainsKey("worktree") ? Convert.ToString(item["worktree"]) : "";
                    if (SamePathKey(worktree, projectDir) ||
                        SamePathKey(worktree, projectDir.Replace(@"\", @"\\")) ||
                        SameMountedProjectPath(worktree, projectDir))
                        local.RemoveAt(i);
                }
                local.Insert(0, new Dictionary<string, object> { { "worktree", projectDir }, { "expanded", true } });

                var lastProject = GetOrCreateObject(server, "lastProject");
                lastProject["local"] = projectDir;

                var sessions = GetOrCreateObject(page, "lastProjectSession");
                if (!sessions.ContainsKey(projectDir))
                {
                    var match = sessions
                        .Where(kv => kv.Value is Dictionary<string, object>)
                        .Select(kv => new { Key = kv.Key, Value = (Dictionary<string, object>)kv.Value })
                        .FirstOrDefault(x => SameMountedProjectPath(x.Key, projectDir));
                    if (match != null)
                    {
                        var copied = new Dictionary<string, object>(match.Value);
                        copied["directory"] = projectDir;
                        copied["at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        sessions[projectDir] = copied;
                        sessions.Remove(match.Key);
                    }
                    else
                    {
                        var sessionId = FindRecentOpenCodeSessionIdForProject(root, projectDir);
                        if (!string.IsNullOrWhiteSpace(sessionId))
                        {
                            sessions[projectDir] = new Dictionary<string, object>
                            {
                                { "directory", projectDir },
                                { "id", sessionId },
                                { "at", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }
                            };
                            AddLog("Đã gắn session OpenCode hiện có vào project: " + sessionId);
                        }
                    }
                }

                root["server"] = serializer.Serialize(server);
                root["layout.page"] = serializer.Serialize(page);
                File.Copy(stateFile, stateFile + ".bak-rclone-" + DateTime.Now.ToString("yyyyMMddHHmmss"), true);
                File.WriteAllText(stateFile, serializer.Serialize(root), new UTF8Encoding(false));
                AddLog("Đã sửa OpenCode session path: " + projectDir);
            }
            catch (Exception ex)
            {
                AddLog("Không tự sửa được session OpenCode: " + ex.Message, "WARN");
            }
        }

        private Dictionary<string, object> ParseJsonObject(JavaScriptSerializer serializer, Dictionary<string, object> root, string key)
        {
            object raw;
            if (!root.TryGetValue(key, out raw)) return null;
            return serializer.Deserialize<Dictionary<string, object>>(Convert.ToString(raw) ?? "{}");
        }

        private Dictionary<string, object> GetOrCreateObject(Dictionary<string, object> parent, string key)
        {
            object value;
            Dictionary<string, object> dict = null;
            if (parent.TryGetValue(key, out value))
                dict = value as Dictionary<string, object>;
            if (dict == null)
            {
                dict = new Dictionary<string, object>();
                parent[key] = dict;
            }
            return dict;
        }

        private List<object> GetOrCreateList(Dictionary<string, object> parent, string key)
        {
            object value;
            if (!parent.TryGetValue(key, out value))
            {
                var created = new List<object>();
                parent[key] = created;
                return created;
            }
            var array = value as object[];
            if (array != null)
            {
                var result = array.ToList();
                parent[key] = result;
                return result;
            }
            var arrayList = value as ArrayList;
            if (arrayList != null)
            {
                var result = arrayList.Cast<object>().ToList();
                parent[key] = result;
                return result;
            }
            var existing = value as List<object>;
            if (existing != null) return existing;
            existing = new List<object>();
            parent[key] = existing;
            return existing;
        }

        private bool SamePathKey(string left, string right)
        {
            return string.Equals((left ?? "").TrimEnd('\\', '/'), (right ?? "").TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }

        private bool SameProjectLeaf(string left, string right)
        {
            var l = (left ?? "").Replace('/', '\\').TrimEnd('\\');
            var r = (right ?? "").Replace('/', '\\').TrimEnd('\\');
            if (SamePathKey(l, r)) return true;
            var fileName = Path.GetFileName(r);
            return !string.IsNullOrWhiteSpace(fileName) &&
                   l.EndsWith("\\" + fileName, StringComparison.OrdinalIgnoreCase);
        }

        private bool IsMountedStylePath(string path)
        {
            path = (path ?? "").Trim();
            return path.StartsWith(@"\\", StringComparison.Ordinal) ||
                   Regex.IsMatch(path, @"^[A-Za-z]:\\", RegexOptions.IgnoreCase);
        }

        private bool SameMountedProjectPath(string left, string right)
        {
            if (!IsMountedStylePath(left) || !IsMountedStylePath(right)) return false;
            var leftDrive = MountedDriveHint(left);
            var rightDrive = MountedDriveHint(right);
            if (leftDrive.HasValue && rightDrive.HasValue &&
                char.ToUpperInvariant(leftDrive.Value) != char.ToUpperInvariant(rightDrive.Value))
                return false;

            var leftRel = MountedRelativePath(left);
            var rightRel = MountedRelativePath(right);
            return !string.IsNullOrWhiteSpace(leftRel) &&
                   !string.IsNullOrWhiteSpace(rightRel) &&
                   string.Equals(leftRel, rightRel, StringComparison.OrdinalIgnoreCase);
        }

        private char? MountedDriveHint(string path)
        {
            path = (path ?? "").Replace('/', '\\').Trim();
            var drive = Regex.Match(path, @"^([A-Za-z]):\\");
            if (drive.Success) return drive.Groups[1].Value[0];

            if (path.StartsWith(@"\\", StringComparison.Ordinal))
            {
                var parts = path.Trim('\\').Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    var shareMatch = Regex.Match(parts[1], @"\b([A-Za-z])$");
                    if (shareMatch.Success) return shareMatch.Groups[1].Value[0];
                }
            }
            return null;
        }

        private string MountedRelativePath(string path)
        {
            path = (path ?? "").Replace('/', '\\').Trim().TrimEnd('\\');
            if (Regex.IsMatch(path, @"^[A-Za-z]:\\", RegexOptions.IgnoreCase))
                return path.Length > 3 ? path.Substring(3).TrimStart('\\') : "";

            if (path.StartsWith(@"\\", StringComparison.Ordinal))
            {
                var parts = path.Trim('\\').Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 2)
                    return string.Join("\\", parts.Skip(2).ToArray());
            }
            return "";
        }

        private string FindOpenCodeExe()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var candidates = new[]
            {
                Path.Combine(local, "Programs", "@opencode-aidesktop", "OpenCode.exe"),
                Path.Combine(local, "Programs", "OpenCode", "OpenCode.exe")
            };
            return candidates.FirstOrDefault(File.Exists);
        }

        private string OpenCodeProjectRootForProfile(DriveProfile p, string drive)
        {
            drive = NormalizeDriveChoice(drive);
            var displayRoot = GetDriveDisplayRoot(drive);
            if (!string.IsNullOrWhiteSpace(displayRoot) && displayRoot.StartsWith(@"\\", StringComparison.Ordinal))
                return displayRoot.TrimEnd('\\') + "\\";
            return ProjectRootForProfile(p, drive);
        }

        private string ProjectRootForProfile(DriveProfile p, string drive)
        {
            drive = NormalizeDriveChoice(drive);
            if (!string.IsNullOrWhiteSpace(drive) && drive.Length >= 2 && drive[1] == ':')
                return drive.TrimEnd('\\') + "\\";
            if (p != null && p.NetworkMode)
            {
                var displayRoot = GetDriveDisplayRoot(drive);
                if (!string.IsNullOrWhiteSpace(displayRoot) && displayRoot.StartsWith(@"\\", StringComparison.Ordinal))
                    return displayRoot.TrimEnd('\\') + "\\";
                return @"\\server\" + SafeVolName(p, drive).Trim('\\') + "\\";
            }
            return drive + "\\";
        }

        private async Task DownloadRemoteToLocalAsync()
        {
            SaveCurrentProfile();
            var p = SelectedProfile;
            if (p == null)
            {
                MessageBox.Show("Hãy chọn một profile trước.", "RcloneDrive", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (RepairProfileLocalWorkDir(p)) SaveProfiles();
            EnsureLocalWorkspace(p);
            var localDir = NormalizeLocalWorkDir(p);
            AddLog("Tải host về máy [" + p.Name + "]: " + p.Source + " -> " + localDir);
            await RunCaptureAsync("sync", p.Source, localDir, "--progress", "--transfers", "2", "--checkers", "1");
        }

        private async Task UploadLocalChangesAsync()
        {
            SaveCurrentProfile();
            var p = SelectedProfile;
            if (p == null)
            {
                MessageBox.Show("Hãy chọn một profile trước.", "RcloneDrive", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (RepairProfileLocalWorkDir(p)) SaveProfiles();
            EnsureLocalWorkspace(p);
            var localDir = NormalizeLocalWorkDir(p);
            if (!HasUploadableLocalFile(localDir))
            {
                var liveMount = MountedRootForProfile(p);
                var message = "Thư mục local chưa có file để đẩy: " + localDir + "\r\n\r\n" +
                              "Nút Đẩy lên chỉ copy từ thư mục local workspace lên host. Nếu bạn đang thấy file trong ổ mount " + liveMount + " thì đó là ổ live của rclone, file được ghi trực tiếp qua mount và không đi qua nút Đẩy lên.\r\n\r\n" +
                              "Muốn dùng nút Đẩy lên: bấm Tải về trước, sửa file trong thư mục local ở trên, rồi Đẩy lên lại.";
                AddLog(message, "WARN");
                MessageBox.Show(message, "Chưa có file để đẩy", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            AddLog("Đẩy thay đổi lên host [" + p.Name + "]: " + localDir + " -> " + p.Source);
            var args = new List<string>
            {
                "copy",
                localDir,
                p.Source,
                "--progress",
                "--transfers", "2",
                "--checkers", "1",
                "--exclude", ".git",
                "--exclude", ".git/**"
            };
            ApplyHostingSafeTransferArgs(args, p);
            var output = await RunCaptureAsync(args.ToArray());
            if (LooksLikeNoTransfer(output))
                AddLog("Không có file mới cần đẩy cho profile " + p.Name + ".", "WARN");
            else
                AddLog("Đã chạy đẩy lên host cho profile " + p.Name + ".");
        }

        private async Task StartCodeWorkspaceModeAsync()
        {
            SaveCurrentProfile();
            var p = SelectedProfile;
            if (p == null)
            {
                MessageBox.Show("Hãy chọn một profile trước.", "RcloneDrive", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (IsCodeWorkspaceWatcherRunning(p))
            {
                StopCodeWorkspaceWatcher(p);
                p.CodeWorkspaceEnabled = false;
                if (codeWorkspaceAutoUploadBox != null) codeWorkspaceAutoUploadBox.Checked = false;
                SaveProfiles();
                UpdateCodeWorkspaceStatusLabel(p);
                AddLog("Code Workspace đã tắt cho " + p.Name + ".");
                return;
            }

            if (RepairProfileLocalWorkDir(p)) SaveProfiles();
            EnsureLocalWorkspace(p);
            var localDir = NormalizeLocalWorkDir(p);
            if (!HasUploadableLocalFile(localDir))
            {
                AddLog("Code Workspace: local đang trống, tải host về máy trước khi bật watcher.");
                await DownloadRemoteToLocalAsync();
            }

            p.CodeWorkspaceEnabled = true;
            p.CodeWorkspaceDelaySeconds = codeWorkspaceDelayBox == null ? 2 : (int)codeWorkspaceDelayBox.Value;
            p.CodeWorkspaceSkipNewerRemote = codeWorkspaceSkipNewerRemoteBox == null || codeWorkspaceSkipNewerRemoteBox.Checked;
            p.CodeWorkspaceIgnores = codeWorkspaceIgnoreBox == null || string.IsNullOrWhiteSpace(codeWorkspaceIgnoreBox.Text)
                ? DriveProfile.DefaultCodeWorkspaceIgnores
                : codeWorkspaceIgnoreBox.Text.Trim();
            if (codeWorkspaceAutoUploadBox != null) codeWorkspaceAutoUploadBox.Checked = true;
            SaveProfiles();

            EnsureGitSafeSettingsForWorkspace(localDir);
            StartCodeWorkspaceWatcher(p);
            OpenLocalWorkspace();
            AddLog("Code Workspace đã bật cho " + p.Name + ": " + localDir + " -> " + p.Source);
        }

        private bool IsCodeWorkspaceWatcherRunning(DriveProfile p)
        {
            return p != null && _workspaceWatchers.ContainsKey(p.Id ?? "");
        }

        private void StartCodeWorkspaceWatcher(DriveProfile p)
        {
            if (p == null) return;
            StopCodeWorkspaceWatcher(p);
            var localDir = NormalizeLocalWorkDir(p);
            if (string.IsNullOrWhiteSpace(localDir) || !Directory.Exists(localDir))
            {
                AddLog("Code Workspace không thấy thư mục local: " + localDir, "ERROR");
                return;
            }

            var watcher = new FileSystemWatcher(localDir)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                EnableRaisingEvents = true
            };
            var state = new WorkspaceWatchState
            {
                ProfileId = p.Id,
                ProfileName = p.Name,
                LocalDir = localDir,
                Watcher = watcher
            };
            FileSystemEventHandler fileHandler = (s, e) => ScheduleCodeWorkspaceUpload(state, e.FullPath, e.ChangeType);
            RenamedEventHandler renameHandler = (s, e) => ScheduleCodeWorkspaceUpload(state, e.FullPath, e.ChangeType);
            watcher.Changed += fileHandler;
            watcher.Created += fileHandler;
            watcher.Renamed += renameHandler;
            watcher.Deleted += (s, e) => AddLog("Code Workspace [" + p.Name + "]: file đã xóa local, chưa tự xóa trên host: " + SafeRelativePath(localDir, e.FullPath), "WARN");
            _workspaceWatchers[p.Id ?? ""] = state;
            UpdateCodeWorkspaceStatusLabel(p);
        }

        private void StopCodeWorkspaceWatcher(DriveProfile p)
        {
            if (p == null) return;
            WorkspaceWatchState state;
            if (!_workspaceWatchers.TryGetValue(p.Id ?? "", out state)) return;
            _workspaceWatchers.Remove(p.Id ?? "");
            try
            {
                if (state.Watcher != null)
                {
                    state.Watcher.EnableRaisingEvents = false;
                    state.Watcher.Dispose();
                }
            }
            catch { }
            lock (state.SyncRoot)
            {
                foreach (var timer in state.Timers.Values)
                    try { timer.Dispose(); } catch { }
                state.Timers.Clear();
            }
            UpdateCodeWorkspaceStatusLabel(p);
        }

        private void StopAllCodeWorkspaceWatchers()
        {
            foreach (var id in _workspaceWatchers.Keys.ToList())
            {
                var p = _profiles.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
                if (p != null) StopCodeWorkspaceWatcher(p);
            }
        }

        private void StartSavedCodeWorkspaceWatchers()
        {
            foreach (var p in _profiles.Where(x => x.CodeWorkspaceEnabled).ToList())
            {
                try
                {
                    var localDir = NormalizeLocalWorkDir(p);
                    if (!Directory.Exists(localDir))
                    {
                        AddLog("Code Workspace [" + p.Name + "]: bỏ qua auto start vì chưa có local: " + localDir, "WARN");
                        continue;
                    }
                    EnsureGitSafeSettingsForWorkspace(localDir);
                    StartCodeWorkspaceWatcher(p);
                    AddLog("Code Workspace tự bật lại: " + p.Name);
                }
                catch (Exception ex)
                {
                    AddLog("Code Workspace không tự bật lại được cho " + p.Name + ": " + ex.Message, "WARN");
                }
            }
        }

        private void ScheduleCodeWorkspaceUpload(WorkspaceWatchState state, string fullPath, WatcherChangeTypes changeType)
        {
            if (state == null || string.IsNullOrWhiteSpace(fullPath)) return;
            if (changeType == WatcherChangeTypes.Deleted) return;
            var p = _profiles.FirstOrDefault(x => string.Equals(x.Id, state.ProfileId, StringComparison.OrdinalIgnoreCase));
            if (p == null || !p.CodeWorkspaceEnabled) return;
            if (Directory.Exists(fullPath)) return;
            if (IsIgnoredCodeWorkspacePath(p, state.LocalDir, fullPath)) return;

            var delay = Math.Max(1, p.CodeWorkspaceDelaySeconds <= 0 ? 2 : p.CodeWorkspaceDelaySeconds);
            lock (state.SyncRoot)
            {
                System.Threading.Timer oldTimer;
                if (state.Timers.TryGetValue(fullPath, out oldTimer))
                {
                    try { oldTimer.Dispose(); } catch { }
                    state.Timers.Remove(fullPath);
                }

                System.Threading.Timer timer = null;
                timer = new System.Threading.Timer(_ =>
                {
                    lock (state.SyncRoot)
                    {
                        state.Timers.Remove(fullPath);
                    }
                    try { timer.Dispose(); } catch { }
                    Task.Run(async () => await UploadCodeWorkspaceFileAsync(state.ProfileId, fullPath));
                }, null, TimeSpan.FromSeconds(delay), Timeout.InfiniteTimeSpan);
                state.Timers[fullPath] = timer;
            }
        }

        private async Task UploadCodeWorkspaceFileAsync(string profileId, string fullPath)
        {
            var p = _profiles.FirstOrDefault(x => string.Equals(x.Id, profileId, StringComparison.OrdinalIgnoreCase));
            if (p == null || !p.CodeWorkspaceEnabled) return;
            var localDir = NormalizeLocalWorkDir(p);
            if (!File.Exists(fullPath) || IsIgnoredCodeWorkspacePath(p, localDir, fullPath)) return;
            if (!await WaitForStableFileAsync(fullPath)) return;

            var relative = SafeRelativePath(localDir, fullPath);
            if (string.IsNullOrWhiteSpace(relative)) return;
            var remoteTarget = JoinRemoteSource(p.Source, relative.Replace('\\', '/'));

            if (p.CodeWorkspaceSkipNewerRemote && await RemoteFileLooksNewerAsync(remoteTarget, fullPath))
            {
                AddLog("Code Workspace [" + p.Name + "]: bỏ qua upload vì host mới hơn: " + relative, "WARN");
                return;
            }

            AddLog("Code Workspace [" + p.Name + "]: upload " + relative);
            var output = await RunCaptureSensitiveAsync(
                new[] { "copyto", fullPath, remoteTarget, "--transfers", "1", "--checkers", "1" },
                "rclone copyto " + QuoteIfNeeded(fullPath) + " " + QuoteIfNeeded(remoteTarget));
            if (LooksLikeTransferError(output))
                AddLog("Code Workspace [" + p.Name + "]: upload có lỗi, xem log rclone phía trên: " + relative, "ERROR");
            else
                AddLog("Code Workspace [" + p.Name + "]: đã upload " + relative);
        }

        private async Task<bool> WaitForStableFileAsync(string file)
        {
            try
            {
                long lastLength = -1;
                DateTime lastWrite = DateTime.MinValue;
                for (var i = 0; i < 8; i++)
                {
                    if (!File.Exists(file)) return false;
                    var info = new FileInfo(file);
                    if (info.Length == lastLength && info.LastWriteTimeUtc == lastWrite)
                    {
                        using (File.Open(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                            return true;
                    }
                    lastLength = info.Length;
                    lastWrite = info.LastWriteTimeUtc;
                    await Task.Delay(350);
                }
            }
            catch (Exception ex)
            {
                AddLog("Code Workspace: file chưa sẵn sàng để upload: " + file + " | " + ex.Message, "WARN");
            }
            return false;
        }

        private async Task<bool> RemoteFileLooksNewerAsync(string remoteTarget, string localFile)
        {
            try
            {
                var output = await RunRcloneNoLogAsync("lsl", remoteTarget);
                if (string.IsNullOrWhiteSpace(output)) return false;
                var match = Regex.Match(output, @"\s(\d{4}-\d{2}-\d{2})\s+(\d{2}:\d{2}:\d{2})");
                if (!match.Success) return false;
                DateTime remoteLocal;
                if (!DateTime.TryParse(match.Groups[1].Value + " " + match.Groups[2].Value, out remoteLocal)) return false;
                var local = File.GetLastWriteTime(localFile);
                return remoteLocal > local.AddSeconds(2);
            }
            catch
            {
                return false;
            }
        }

        private bool LooksLikeTransferError(string output)
        {
            var text = output ?? "";
            return text.IndexOf("ERROR", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   text.IndexOf("Failed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   text.IndexOf("permission denied", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private bool HasUploadableLocalFile(string localDir)
        {
            if (string.IsNullOrWhiteSpace(localDir) || !Directory.Exists(localDir)) return false;
            try
            {
                var stack = new Stack<string>();
                stack.Push(localDir);
                while (stack.Count > 0)
                {
                    var dir = stack.Pop();
                    foreach (var file in Directory.EnumerateFiles(dir))
                    {
                        if (!IsInsideGitDirectory(localDir, file))
                            return true;
                    }
                    foreach (var child in Directory.EnumerateDirectories(dir))
                    {
                        if (string.Equals(Path.GetFileName(child), ".git", StringComparison.OrdinalIgnoreCase)) continue;
                        stack.Push(child);
                    }
                }
            }
            catch (Exception ex)
            {
                AddLog("Không kiểm tra được thư mục local trước khi đẩy: " + ex.Message, "WARN");
                return true;
            }
            return false;
        }

        private bool IsInsideGitDirectory(string rootDir, string path)
        {
            try
            {
                var relative = Path.GetFullPath(path).Substring(Path.GetFullPath(rootDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(x => string.Equals(x, ".git", StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return false;
            }
        }

        private string SafeRelativePath(string rootDir, string path)
        {
            try
            {
                var root = Path.GetFullPath(rootDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var full = Path.GetFullPath(path);
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return "";
                return full.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return "";
            }
        }

        private bool IsIgnoredCodeWorkspacePath(DriveProfile p, string rootDir, string path)
        {
            var relative = SafeRelativePath(rootDir, path).Replace('\\', '/').TrimStart('/');
            if (string.IsNullOrWhiteSpace(relative)) return true;
            var ignoreText = string.IsNullOrWhiteSpace(p == null ? "" : p.CodeWorkspaceIgnores)
                ? DriveProfile.DefaultCodeWorkspaceIgnores
                : p.CodeWorkspaceIgnores;
            foreach (var raw in Regex.Split(ignoreText ?? "", @"[;\r\n,]+"))
            {
                var pattern = (raw ?? "").Trim().Replace('\\', '/').TrimStart('/');
                if (string.IsNullOrWhiteSpace(pattern)) continue;
                if (pattern.EndsWith("/**", StringComparison.Ordinal))
                {
                    var prefix = pattern.Substring(0, pattern.Length - 3).TrimEnd('/');
                    if (relative.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                        relative.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
                        return true;
                    continue;
                }
                if (pattern.IndexOf('*') >= 0 || pattern.IndexOf('?') >= 0)
                {
                    var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
                    if (Regex.IsMatch(relative, regex, RegexOptions.IgnoreCase) ||
                        Regex.IsMatch(Path.GetFileName(relative), regex, RegexOptions.IgnoreCase))
                        return true;
                    continue;
                }
                if (relative.Equals(pattern, StringComparison.OrdinalIgnoreCase) ||
                    relative.EndsWith("/" + pattern, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private void UpdateCodeWorkspaceStatusLabel(DriveProfile p)
        {
            if (codeWorkspaceStatusLabel == null) return;
            var running = IsCodeWorkspaceWatcherRunning(p);
            var enabled = p != null && p.CodeWorkspaceEnabled;
            codeWorkspaceStatusLabel.Text = running
                ? "Code Workspace: đang theo dõi local và auto upload"
                : (enabled ? "Code Workspace: đã bật trong profile, bấm Code WS để chạy" : "Code Workspace: tắt");
            codeWorkspaceStatusLabel.ForeColor = running ? _success : _muted;
        }

        private void EnsureGitSafeSettingsForWorkspace(string localDir)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(localDir) || !Directory.Exists(Path.Combine(localDir, ".git"))) return;
                RunGit(localDir, "config core.fscache false", false);
                RunGit(localDir, "config core.trustctime false", false);
                RunGit(localDir, "config core.checkstat minimal", false);
                AddLog("Đã áp dụng Git safe settings cho Code Workspace: " + localDir);
            }
            catch (Exception ex)
            {
                AddLog("Không áp dụng được Git safe settings: " + ex.Message, "WARN");
            }
        }

        private bool LooksLikeNoTransfer(string output)
        {
            return Regex.IsMatch(output ?? "", @"Transferred:\s+0\s+B\s*/\s*0\s+B", RegexOptions.IgnoreCase);
        }

        private string MountedRootForProfile(DriveProfile p)
        {
            try
            {
                var drive = ActiveDriveForProfile(p);
                if (string.IsNullOrWhiteSpace(drive) || IsAutoDrive(drive) || !IsMounted(drive))
                    return "(chưa mount)";
                return ProjectRootForProfile(p, drive);
            }
            catch
            {
                return "(không xác định)";
            }
        }

        private void ClearCacheForSelectedProfile()
        {
            SaveCurrentProfile();
            var p = SelectedProfile;
            if (p == null)
            {
                MessageBox.Show("Hãy chọn một profile trước.", "RcloneDrive", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            ClearCacheDirectories(new[] { p.CacheDir }, "profile " + p.Name);
        }

        private void ClearCacheForAllProfiles()
        {
            SaveCurrentProfile();
            var dirs = _profiles.Select(p => p.CacheDir).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (dirs.Count == 0)
            {
                AddLog("Không có thư mục cache nào để dọn.", "WARN");
                return;
            }
            ClearCacheDirectories(dirs, "tất cả profile");
        }

        private void ClearCacheDirectories(IEnumerable<string> cacheDirs, string scope)
        {
            var dirs = cacheDirs
                .Select(ExpandCacheDir)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (dirs.Count == 0)
            {
                AddLog("Chưa đặt thư mục cache.", "WARN");
                return;
            }

            var message = "Dọn cache cho " + scope + "?\r\n\r\n" + string.Join("\r\n", dirs) + "\r\n\r\nHãy ngắt ổ đang mount trước để tránh xóa file cache đang ghi.";
            if (MessageBox.Show(message, "Dọn cache rclone", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            long freedBytes = 0;
            var removed = 0;
            foreach (var dir in dirs)
            {
                if (!Directory.Exists(dir))
                {
                    AddLog("Cache không tồn tại: " + dir, "WARN");
                    continue;
                }
                if (!IsSafeCacheDir(dir))
                {
                    AddLog("Bỏ qua thư mục cache không an toàn: " + dir, "ERROR");
                    continue;
                }

                foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try { freedBytes += new FileInfo(file).Length; } catch { }
                }
                removed += ClearDirectoryContents(dir);
                AddLog("Đã dọn cache: " + dir);
            }
            AddLog("Dọn cache xong. Đã xóa khoảng " + FormatBytes(freedBytes) + ", mục đã xử lý: " + removed);
        }

        private string ExpandCacheDir(string path)
        {
            return string.IsNullOrWhiteSpace(path) ? "" : Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim()));
        }

        private bool IsSafeCacheDir(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return false;
            var full = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var root = Path.GetPathRoot(full).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase)) return false;
            var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(full, user, StringComparison.OrdinalIgnoreCase)) return false;
            return full.IndexOf("cache", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   full.IndexOf("rclone", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private int ClearDirectoryContents(string dir)
        {
            var removed = 0;
            foreach (var file in Directory.GetFiles(dir))
            {
                try
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                    File.Delete(file);
                    removed++;
                }
                catch (Exception ex)
                {
                    AddLog("Không xóa được file cache " + file + ": " + ex.Message, "WARN");
                }
            }
            foreach (var subDir in Directory.GetDirectories(dir))
            {
                try
                {
                    Directory.Delete(subDir, true);
                    removed++;
                }
                catch (Exception ex)
                {
                    AddLog("Không xóa được thư mục cache " + subDir + ": " + ex.Message, "WARN");
                }
            }
            return removed;
        }

        private string FormatBytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double value = bytes;
            var unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }
            return value.ToString(unit == 0 ? "0" : "0.##") + " " + units[unit];
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
                form.BackColor = _surface;

                var contentPanel = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = _surface };
                form.Controls.Add(contentPanel);

                var layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(14), BackColor = _surface };
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
                contentPanel.Controls.Add(layout);

                var dName = DialogText(layout, "Tên profile", p.Name, 0, 0);
                var dRemote = DialogCombo(layout, "Remote", _remotes, p.Remote, 1, 0, true);
                var dPath = DialogText(layout, "Đường dẫn remote", p.RemotePath, 0, 1);
                var dDrive = DialogCombo(layout, "Ký tự ổ đĩa", new[] { "Tự chọn ổ trống" }.Concat(GetDriveLetterChoices(p, p.DriveLetter)), IsAutoDrive(p.DriveLetter) ? "Tự chọn ổ trống" : p.DriveLetter, 1, 1, true);
                var dCacheMode = DialogCombo(layout, "Chế độ VFS cache", new[] { "off", "minimal", "writes", "full" }, p.CacheMode, 0, 2, false);
                var dCacheDir = DialogText(layout, "Thư mục cache", p.CacheDir, 1, 2);
                var dLocalDir = DialogText(layout, "Thư mục local", NormalizeLocalWorkDir(p), 0, 3);
                var cachePicker = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 38, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(14, 0, 14, 0), BackColor = _surface };
                cachePicker.Controls.Add(ActionButton("Browse cache", (s, e) =>
                {
                    var picked = PickCacheDirectory(dCacheDir.Text);
                    if (!string.IsNullOrWhiteSpace(picked)) dCacheDir.Text = picked;
                }, _surface, _text, 126));
                cachePicker.Controls.Add(ActionButton("Browse local", (s, e) =>
                {
                    var picked = PickCacheDirectory(dLocalDir.Text);
                    if (!string.IsNullOrWhiteSpace(picked)) dLocalDir.Text = picked;
                }, _surface, _text, 126));
                contentPanel.Controls.Add(cachePicker);
                cachePicker.BringToFront();
                var dTransfers = DialogNumber(layout, "Transfers", p.Transfers <= 0 ? 4 : p.Transfers, 1, 64, 1, 3);
                var dBuffer = DialogNumber(layout, "Bộ đệm MB", p.BufferSizeMb <= 0 ? 32 : p.BufferSizeMb, 1, 1024, 0, 4);
                var dCacheMaxAge = DialogText(layout, "Giữ cache tối đa", string.IsNullOrWhiteSpace(p.VfsCacheMaxAge) ? "72h" : p.VfsCacheMaxAge, 1, 4);
                var dWriteBack = DialogText(layout, "Upload sau khi sửa", string.IsNullOrWhiteSpace(p.VfsWriteBack) ? "5s" : p.VfsWriteBack, 0, 5);
                var dExtra = DialogText(layout, "Tham số rclone thêm", p.ExtraArgs ?? "", 1, 5);
                layout.SetColumnSpan(dExtra.Parent, 2);

                var checks = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 54, Padding = new Padding(14, 8, 14, 4), BackColor = _surface };
                var dReadOnly = new CheckBox { Text = "Chỉ đọc", Width = 90, Checked = p.ReadOnly };
                var dAuto = new CheckBox { Text = "Tự mount khi mở app", Width = 180, Checked = p.AutoMount };
                var dNetwork = new CheckBox { Text = "Network mode", Width = 130, Checked = p.NetworkMode };
                checks.Controls.Add(dReadOnly);
                checks.Controls.Add(dAuto);
                checks.Controls.Add(dNetwork);
                contentPanel.Controls.Add(checks);
                checks.BringToFront();

                var note = new Label
                {
                    Dock = DockStyle.Top,
                    Height = 58,
                    Padding = new Padding(14, 8, 14, 0),
                    BackColor = _surface,
                    Text = "Cài đặt này áp dụng cho profile đang chọn. Nếu ổ đang mount, hãy Ngắt rồi Kết nối lại để nhận cấu hình mới."
                };
                contentPanel.Controls.Add(note);
                note.BringToFront();

                var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 58, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(14, 10, 14, 10), BackColor = _surface };
                var ok = ActionButton("Lưu", (s, e) => {}, _primary, Color.White, 96);
                ok.DialogResult = DialogResult.OK;
                var cancel = ActionButton("Hủy", (s, e) => {}, _surface, _text, 96);
                cancel.DialogResult = DialogResult.Cancel;
                buttons.Controls.Add(ok);
                buttons.Controls.Add(cancel);
                form.Controls.Add(buttons);
                // WinForms docks in reverse z-order: highest z-order docks last.
                // contentPanel (Fill) must dock last so buttons (Bottom) reserves space first.
                contentPanel.BringToFront();
                form.AcceptButton = ok;
                form.CancelButton = cancel;

                if (form.ShowDialog(this) != DialogResult.OK) return;

                p.Name = string.IsNullOrWhiteSpace(dName.Text) ? "Ổ đĩa" : dName.Text.Trim();
                p.Remote = Convert.ToString(dRemote.SelectedItem ?? dRemote.Text ?? "").Trim();
                p.RemotePath = DriveProfile.NormalizeRemotePath(dPath.Text, p.Remote);
                var driveChoice = NormalizeDriveChoice(Convert.ToString(dDrive.SelectedItem ?? dDrive.Text ?? "AUTO"));
                if (IsDriveReservedByAnotherProfile(driveChoice, p))
                {
                    MessageBox.Show("Ký tự ổ " + driveChoice + " đang được profile khác trong app giữ. Hãy chọn ổ khác hoặc Tự chọn ổ trống.", "Trùng ký tự ổ", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                p.DriveLetter = driveChoice;
                p.CacheMode = Convert.ToString(dCacheMode.SelectedItem ?? dCacheMode.Text ?? "full").Trim();
                p.CacheDir = dCacheDir.Text.Trim();
                p.LocalWorkDir = string.IsNullOrWhiteSpace(dLocalDir.Text) ? GetDefaultLocalWorkDir(p.Name) : dLocalDir.Text.Trim();
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
            if (profile != null) _newProfileDraft = false;
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
                statusLabel.Text = "Không thấy rclone.exe";
                AddLog("Không tìm thấy rclone.exe trong " + _appDir, "ERROR");
                return;
            }
            statusLabel.Text = "Đang làm mới remotes...";
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
                if (combo.SelectedIndex < 0 && combo.Items.Count > 0 && !(combo == remoteCombo && _newProfileDraft)) combo.SelectedIndex = 0;
            }
            LoadSelectedProfileIntoFields();
        }

        private void RefreshDriveLetters()
        {
            var old = Convert.ToString(driveCombo.SelectedItem ?? driveCombo.Text ?? "");
            var selectedProfile = SelectedProfile;
            driveCombo.Items.Clear();
            driveCombo.Items.Add("Tự chọn ổ trống");
            foreach (var d in GetDriveLetterChoices(selectedProfile, old))
                driveCombo.Items.Add(d);
            if (!string.IsNullOrWhiteSpace(old)) SelectComboValue(driveCombo, old);
            if (driveCombo.SelectedIndex < 0 && driveCombo.Items.Count > 0) driveCombo.SelectedIndex = 0;
        }

        private IEnumerable<string> GetDriveLetterChoices(DriveProfile exceptProfile, string current)
        {
            var choices = GetFreeDriveLetters(exceptProfile).ToList();
            var normalizedCurrent = NormalizeDriveChoice(current);
            if (!IsAutoDrive(normalizedCurrent) &&
                !IsDriveReservedByAnotherProfile(normalizedCurrent, exceptProfile) &&
                !choices.Contains(normalizedCurrent, StringComparer.OrdinalIgnoreCase))
                choices.Add(normalizedCurrent);

            foreach (var drive in new[] { "Z:", "Y:", "X:", "W:" })
            {
                if (IsDriveReservedByAnotherProfile(drive, exceptProfile)) continue;
                if (!IsDriveAvailableForMount(drive, exceptProfile)) continue;
                if (!choices.Contains(drive, StringComparer.OrdinalIgnoreCase))
                    choices.Add(drive);
            }
            return choices.Where(d => !string.IsNullOrWhiteSpace(d)).Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private IEnumerable<string> GetFreeDriveLetters()
        {
            return GetFreeDriveLetters(null);
        }

        private IEnumerable<string> GetFreeDriveLetters(DriveProfile exceptProfile)
        {
            var used = DriveInfo.GetDrives().Select(d => d.Name.Substring(0, 2)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var drive in _mounts.Keys)
            {
                if (!string.IsNullOrWhiteSpace(drive))
                    used.Add(NormalizeDriveChoice(drive));
            }
            foreach (var drive in GetReservedProfileDriveLetters(exceptProfile))
                used.Add(drive);
            var letters = "ZYXWVUTSRQPONMLKJIHGFEDC".Select(c => c + ":");
            return letters.Where(d => !used.Contains(d));
        }

        private IEnumerable<string> GetReservedProfileDriveLetters(DriveProfile exceptProfile)
        {
            foreach (var p in _profiles)
            {
                if (ReferenceEquals(p, exceptProfile)) continue;
                if (!IsAutoDrive(p.DriveLetter))
                    yield return NormalizeDriveChoice(p.DriveLetter);
            }
            foreach (var pair in _activeDrives)
            {
                if (ReferenceEquals(pair.Key, exceptProfile)) continue;
                if (!string.IsNullOrWhiteSpace(pair.Value) && !IsAutoDrive(pair.Value))
                    yield return NormalizeDriveChoice(pair.Value);
            }
        }

        private bool IsDriveReservedByAnotherProfile(string drive, DriveProfile exceptProfile)
        {
            if (IsAutoDrive(drive)) return false;
            var normalized = NormalizeDriveChoice(drive);
            return GetReservedProfileDriveLetters(exceptProfile).Any(d => string.Equals(d, normalized, StringComparison.OrdinalIgnoreCase));
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
            var free = GetFreeDriveLetters(profile).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(free))
                throw new InvalidOperationException("Không tìm thấy ký tự ổ đĩa trống.");
            return free;
        }

        private string ActiveDriveForProfile(DriveProfile profile)
        {
            string active;
            if (_activeDrives.TryGetValue(profile, out active) && !string.IsNullOrWhiteSpace(active))
                return active;
            active = DetectedDriveForProfile(profile);
            if (!string.IsNullOrWhiteSpace(active))
            {
                _activeDrives[profile] = active;
                return active;
            }
            return NormalizeDriveChoice(profile.DriveLetter);
        }

        private string ProfileDuration(string value, string fallback)
        {
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }

        private bool IsDriveAvailableForMount(string drive)
        {
            return IsDriveAvailableForMount(drive, null);
        }

        private bool IsDriveAvailableForMount(string drive, DriveProfile exceptProfile)
        {
            if (string.IsNullOrWhiteSpace(drive) || IsAutoDrive(drive)) return false;
            var normalized = NormalizeDriveChoice(drive);
            if (_mounts.ContainsKey(normalized)) return false;
            if (GetReservedProfileDriveLetters(exceptProfile).Any(d => string.Equals(d, normalized, StringComparison.OrdinalIgnoreCase))) return false;
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
            if (IsAutoDrive(profile.DriveLetter))
            {
                drive = DetectedDriveForProfile(profile);
                if (!string.IsNullOrWhiteSpace(drive))
                {
                    _activeDrives[profile] = drive;
                    return true;
                }
                return false;
            }
            var normalized = NormalizeDriveChoice(profile.DriveLetter);
            MountedDriveInfo detected;
            return _detectedRcloneDrives.TryGetValue(normalized, out detected) &&
                   string.Equals(NormalizeSourceForCompare(detected.DisplayRoot), NormalizeSourceForCompare(profile.Source), StringComparison.OrdinalIgnoreCase);
        }

        private string DetectedDriveForProfile(DriveProfile profile)
        {
            if (profile == null || string.IsNullOrWhiteSpace(profile.Source)) return null;
            foreach (var item in _detectedRcloneDrives)
            {
                var detected = item.Value;
                if (detected == null) continue;
                if (string.Equals(NormalizeSourceForCompare(detected.DisplayRoot), NormalizeSourceForCompare(profile.Source), StringComparison.OrdinalIgnoreCase))
                    return item.Key;
            }
            return null;
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

        private void MarkProfileManuallyDisconnected(DriveProfile profile)
        {
            if (profile == null) return;
            profile.RestoreOnStartup = false;
            SaveProfiles();
        }

        private async Task MountSelectedAsync()
        {
            await SaveAndMountCurrentProfileAsync();
        }

        private async Task ToggleSelectedConnectionAsync()
        {
            if (_connectionBusy)
            {
                AddLog("Đang xử lý kết nối/ngắt, vui lòng đợi.", "WARN");
                return;
            }
            var profile = SelectedProfile;
            if (profile != null)
            {
                if (IsMountedProfile(profile))
                {
                    AddLog("Người dùng bấm Ngắt kết nối: " + profile.Name);
                    SetConnectionBusy(true, "Đang ngắt");
                    try { UnmountSelected(); }
                    finally { SetConnectionBusy(false, ""); }
                    return;
                }
                AddLog("Người dùng bấm Kết nối: " + profile.Name);
                SetConnectionBusy(true, "Đang kết nối");
                try { await SaveAndMountCurrentProfileAsync(); }
                finally { SetConnectionBusy(false, ""); }
                return;
            }

            var external = SelectedMountedDrive;
            if (external != null)
            {
                UnmountDriveLetter(external.DriveLetter, external.Name);
                AddLog("Đã gửi lệnh ngắt ổ đang bật sẵn: " + external.DriveLetter);
                RenderProfiles();
            }
        }

        private async Task SaveAndMountCurrentProfileAsync()
        {
            AddLog("Bắt đầu xử lý lệnh kết nối từ UI.");
            var p = SelectedProfile;
            if (p == null)
            {
                var external = SelectedMountedDrive;
                if (external != null)
                {
                    AddLog("Ổ " + external.DriveLetter + " đang kết nối sẵn. Hãy chọn profile cấu hình hoặc bấm Mới để tạo profile khác.", "WARN");
                    OpenDriveInExplorer(external.DriveLetter);
                    return;
                }
                p = CreateProfileFromCurrentFields(null, false, true);
            }
            else
            {
                if (IsMountedProfile(p))
                {
                    var drive = ActiveDriveForProfile(p);
                    AddLog("Profile '" + p.Name + "' đã kết nối tại " + drive + ".", "WARN");
                    if (!IsAutoDrive(drive)) OpenDriveInExplorer(drive);
                    return;
                }
                SaveCurrentProfile();
                p = SelectedProfile ?? p;
            }
            if (p == null) return;
            AddLog("Đã lưu cấu hình, bắt đầu kết nối: " + p.Name);
            await MountProfileAsync(p);
        }

        private async Task RefreshSelectedMountAsync()
        {
            var p = SelectedProfile;
            if (p == null)
            {
                MessageBox.Show("Hãy chọn một profile trước.", "RcloneDrive", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var drive = ActiveDriveForProfile(p);
            if (string.IsNullOrWhiteSpace(drive) || !IsMounted(drive))
            {
                AddLog("Ổ chưa mount, bắt đầu kết nối lại.");
                SaveCurrentProfile();
                await MountProfileAsync(p);
                return;
            }
            AddLog("Làm mới ổ " + drive + " bằng cách ngắt và mount lại.");
            UnmountDriveLetter(drive, p.Name);
            CleanupMountState(p, drive);
            RenderProfiles();
            RefreshDriveLetters();
            await Task.Delay(1200);
            SaveCurrentProfile();
            await MountProfileAsync(p);
        }

        private DriveProfile CreateProfileFromCurrentFields(string forcedName = null, bool preferFreeDrive = false, bool keepTypedName = false)
        {
            var remote = Convert.ToString(remoteCombo.SelectedItem ?? remoteCombo.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(remote))
            {
                AddLog("Hãy chọn remote/config trước khi lưu hoặc kết nối.", "ERROR");
                MessageBox.Show("Hãy chọn remote/config trước khi lưu hoặc kết nối.", "RcloneDrive", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                SelectTab("Ổ đĩa");
                return null;
            }
            var typedName = (nameBox.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(typedName))
            {
                AddLog("Hãy nhập tên profile trước khi lưu hoặc kết nối.", "ERROR");
                MessageBox.Show("Hãy nhập tên profile trước khi lưu hoặc kết nối.", "RcloneDrive", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                SelectTab("Ổ đĩa");
                return null;
            }
            var p = new DriveProfile
            {
                Name = string.IsNullOrWhiteSpace(forcedName)
                    ? (keepTypedName && !_profiles.Any(x => string.Equals(x.Name, typedName, StringComparison.OrdinalIgnoreCase))
                        ? typedName
                        : UniqueProfileName(typedName))
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
                MountPreset = Convert.ToString(mountPresetCombo.SelectedItem ?? mountPresetCombo.Text ?? "Nhanh/RaiDrive"),
                TunnelEnabled = false,
                TunnelHostname = "",
                TunnelLocalPort = (int)tunnelPortBox.Value,
                TunnelCommand = "",
                ExtraArgs = extraArgsBox.Text.Trim(),
                CodeWorkspaceEnabled = codeWorkspaceAutoUploadBox.Checked,
                CodeWorkspaceSkipNewerRemote = codeWorkspaceSkipNewerRemoteBox.Checked,
                CodeWorkspaceDelaySeconds = (int)codeWorkspaceDelayBox.Value,
                CodeWorkspaceIgnores = string.IsNullOrWhiteSpace(codeWorkspaceIgnoreBox.Text) ? DriveProfile.DefaultCodeWorkspaceIgnores : codeWorkspaceIgnoreBox.Text.Trim()
            };
            ApplyTunnelDefaultsForNewProfile(p, "Tạo profile từ form");
            _profiles.Add(p);
            _newProfileDraft = false;
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

        private void ApplyTunnelDefaultsForNewProfile(DriveProfile profile, string context)
        {
            if (profile == null) return;
            var uiPort = tunnelPortBox == null ? 0 : (int)tunnelPortBox.Value;

            profile.TunnelEnabled = false;
            profile.TunnelCommand = "";
            profile.TunnelHostname = "";
            profile.TunnelLocalPort = uiPort;
            AddLog(context + ": Cloudflare tunnel chưa bật cho profile " + profile.Name + ".", "TUNNEL");
        }

        private async Task<bool> EnsureProfileTunnelAsync(DriveProfile p)
        {
            if (p == null) return true;
            if (!p.TunnelEnabled && string.IsNullOrWhiteSpace(p.TunnelCommand))
            {
                var currentHost = GetRemoteConfigValue(p.Remote, "host");
                var currentPort = GetRemoteConfigValue(p.Remote, "port");
                if (IsLocalTunnelHost(currentHost))
                {
                    AddLog("Remote " + p.Remote + " đang trỏ tới " + currentHost + ":" + currentPort + " nhưng Cloudflare tunnel chưa bật. Hãy sửa lại host thật trong config hoặc bật Mount Cloudflare tunnel.", "ERROR");
                    return false;
                }
                AddLog("Cloudflare tunnel chưa bật, bỏ qua bước mở tunnel cho " + p.Name + ".", "TUNNEL");
                return true;
            }
            if (p != null && p.TunnelEnabled && string.IsNullOrWhiteSpace(p.TunnelCommand))
            {
                var originalHost = ResolveTunnelHostnameFromRemote(p);
                if (string.IsNullOrWhiteSpace(originalHost))
                {
                    AddLog("Đã tick Mount Cloudflare tunnel nhưng không lấy được hostname từ rclone config.", "ERROR");
                    return false;
                }
                p.TunnelHostname = originalHost;
            }
            var command = BuildTunnelCommand(p);
            if (string.IsNullOrWhiteSpace(command))
            {
                AddLog("Cloudflare tunnel đã bật nhưng chưa tạo được lệnh tunnel cho " + p.Name + ".", "ERROR");
                return false;
            }

            if (!await EnsureRcloneRemoteUsesTunnelAsync(p))
                return false;

            string host;
            int port;
            var hasEndpoint = TryExtractTunnelEndpoint(command, out host, out port);
            if (hasEndpoint && IsTcpPortOpen(host, port, 700))
            {
                AddLog("Tunnel đã sẵn sàng: " + host + ":" + port);
                return true;
            }

            var key = string.IsNullOrWhiteSpace(p.Id) ? p.Name : p.Id;
            Process existing;
            if (_tunnels.TryGetValue(key, out existing) && existing != null && !existing.HasExited)
            {
                AddLog("Tunnel đang chạy cho profile: " + p.Name);
            }
            else
            {
                try
                {
                    AddLog("[" + p.Name + "] Đang khởi tạo kết nối tunnel...");
                    AddLog("[CMD] [" + p.Name + "] " + command);
                    var psi = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = "/c " + command,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };
                    var proc = Process.Start(psi);
                    if (proc == null)
                    {
                        AddLog("Không start được tunnel command.", "ERROR");
                        return false;
                    }
                    proc.EnableRaisingEvents = true;
                    proc.OutputDataReceived += (s, e) => { if (e.Data != null) AddLog("[" + p.Name + "] " + e.Data, "TUNNEL"); };
                    proc.ErrorDataReceived += (s, e) => { if (e.Data != null) AddLog("[" + p.Name + "] " + e.Data, "TUNNEL"); };
                    proc.Exited += (s, e) => AddLog("Tunnel đã dừng cho " + p.Name + ", code " + SafeExitCode(proc), "WARN");
                    proc.BeginOutputReadLine();
                    proc.BeginErrorReadLine();
                    _tunnels[key] = proc;
                }
                catch (Exception ex)
                {
                    AddLog("Không start được tunnel: " + ex.Message, "ERROR");
                    return false;
                }
            }

            if (!hasEndpoint)
            {
                await Task.Delay(2000);
                return true;
            }

            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                if (IsTcpPortOpen(host, port, 700))
                {
                    AddLog("Tunnel sẵn sàng: " + host + ":" + port);
                    return true;
                }
                await Task.Delay(500);
            }

            AddLog("Tunnel chưa mở port " + host + ":" + port + ". Kiểm tra cloudflared/login Cloudflare Access.", "ERROR");
            return false;
        }

        private string BuildTunnelCommand(DriveProfile p)
        {
            if (p == null) return "";
            var custom = (p.TunnelCommand ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(custom)) return custom;
            if (!p.TunnelEnabled) return "";
            var hostname = ResolveTunnelHostnameFromRemote(p);
            if (string.IsNullOrWhiteSpace(hostname)) return "";
            var port = ResolveTunnelLocalPort(p);
            var exe = FindCloudflaredExe();
            return QuoteIfNeeded(exe) + " access tcp --hostname " + QuoteIfNeeded(hostname) + " --url localhost:" + port;
        }

        private string ResolveTunnelHostnameFromRemote(DriveProfile p)
        {
            if (p == null) return "";
            var remoteHost = GetRemoteConfigValue(p.Remote, "host");
            if (!string.IsNullOrWhiteSpace(remoteHost) &&
                !string.Equals(remoteHost, "localhost", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(remoteHost, "127.0.0.1", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(remoteHost, "::1", StringComparison.OrdinalIgnoreCase))
            {
                p.TunnelHostname = remoteHost;
                SaveProfiles();
                return remoteHost;
            }
            var saved = (p.TunnelHostname ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(saved)) return saved;

            var legacy = ResolveTunnelHostnameFromLegacyProfile(p);
            if (!string.IsNullOrWhiteSpace(legacy))
            {
                p.TunnelHostname = legacy;
                SaveProfiles();
                AddLog("Đã lấy lại Cloudflare hostname từ profile cũ: " + legacy);
                return legacy;
            }
            return "";
        }

        private string ResolveTunnelHostnameFromLegacyProfile(DriveProfile p)
        {
            try
            {
                var candidates = new[]
                {
                    Path.Combine(@"D:\jetide\raidricloen\scrip", "profiles.json"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RcloneDriveManager", "tunnel-profiles.json")
                };
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (!string.IsNullOrWhiteSpace(p.Name)) names.Add(p.Name.Trim());
                if (!string.IsNullOrWhiteSpace(p.Remote)) names.Add(p.Remote.Trim().TrimEnd(':'));

                foreach (var file in candidates)
                {
                    if (!File.Exists(file)) continue;
                    var rows = _json.Deserialize<List<Dictionary<string, object>>>(File.ReadAllText(file, Encoding.UTF8));
                    if (rows == null) continue;
                    foreach (var row in rows)
                    {
                        var name = GetDictString(row, "name");
                        if (string.IsNullOrWhiteSpace(name) || !names.Contains(name.Trim())) continue;
                        var host = GetDictString(row, "hostname");
                        if (!string.IsNullOrWhiteSpace(host) && !IsLocalTunnelHost(host))
                            return host.Trim();
                    }
                }
            }
            catch (Exception ex)
            {
                AddLog("Không đọc được profile tunnel cũ: " + ex.Message, "WARN");
            }
            return "";
        }

        private string GetDictString(Dictionary<string, object> row, string key)
        {
            object value;
            return row != null && row.TryGetValue(key, out value) ? Convert.ToString(value) : "";
        }

        private bool IsLocalTunnelHost(string host)
        {
            return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(host, "::1", StringComparison.OrdinalIgnoreCase);
        }

        private int ResolveTunnelLocalPort(DriveProfile p)
        {
            if (p == null) return 2221;
            if (p.TunnelLocalPort > 0) return p.TunnelLocalPort;
            for (var port = 2221; port <= 2299; port++)
            {
                if (!IsTcpPortOpen("localhost", port, 120))
                {
                    p.TunnelLocalPort = port;
                    try
                    {
                        if (tunnelPortBox != null && tunnelPortBox.Minimum <= port && tunnelPortBox.Maximum >= port)
                            tunnelPortBox.Value = port;
                    }
                    catch { }
                    SaveProfiles();
                    AddLog("Đã tự chọn tunnel port: " + port);
                    return port;
                }
            }
            p.TunnelLocalPort = 2221;
            return p.TunnelLocalPort;
        }

        private Task<bool> EnsureRcloneRemoteUsesTunnelAsync(DriveProfile p)
        {
            try
            {
                if (p == null || string.IsNullOrWhiteSpace(p.Remote)) return Task.FromResult(true);
                var command = BuildTunnelCommand(p);
                string host;
                int port;
                if (!TryExtractTunnelEndpoint(command, out host, out port)) return Task.FromResult(true);
                var remoteName = p.Remote.Trim().TrimEnd(':');
                if (string.IsNullOrWhiteSpace(remoteName)) return Task.FromResult(true);
                AddLog("Remote " + remoteName + " sẽ dùng tunnel tạm thời " + host + ":" + port + " cho process này; rclone.conf được giữ nguyên.", "TUNNEL");
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                AddLog("Không chuẩn bị được remote tunnel: " + ex.Message, "ERROR");
                return Task.FromResult(false);
            }
        }

        private void ApplyTunnelEnvironment(ProcessStartInfo psi, DriveProfile p)
        {
            if (psi == null || p == null || string.IsNullOrWhiteSpace(p.Remote)) return;
            if (!p.TunnelEnabled && string.IsNullOrWhiteSpace(p.TunnelCommand)) return;

            string host;
            int port;
            if (!TryExtractTunnelEndpoint(BuildTunnelCommand(p), out host, out port)) return;

            var remoteName = p.Remote.Trim().TrimEnd(':').ToUpperInvariant();
            remoteName = Regex.Replace(remoteName, @"[^A-Z0-9_]", "_");
            if (string.IsNullOrWhiteSpace(remoteName)) return;

            psi.EnvironmentVariables["RCLONE_CONFIG_" + remoteName + "_HOST"] = host;
            psi.EnvironmentVariables["RCLONE_CONFIG_" + remoteName + "_PORT"] = Convert.ToString(port);
        }

        private string FindCloudflaredExe()
        {
            var candidates = new[]
            {
                Path.Combine(_appDir, "cloudflared.exe"),
                @"C:\Cloudflared\cloudflared.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Cloudflared", "cloudflared.exe"),
                "cloudflared.exe"
            };
            return candidates.FirstOrDefault(File.Exists) ?? "cloudflared.exe";
        }

        private int SafeExitCode(Process proc)
        {
            try { return proc == null ? -1 : proc.ExitCode; }
            catch { return -1; }
        }

        private bool TryExtractTunnelEndpoint(string command, out string host, out int port)
        {
            host = "localhost";
            port = 0;
            if (string.IsNullOrWhiteSpace(command)) return false;
            var match = Regex.Match(command, @"--url\s+(?:""(?<url>[^""]+)""|'(?<url>[^']+)'|(?<url>\S+))", RegexOptions.IgnoreCase);
            if (!match.Success) return false;
            var url = match.Groups["url"].Value.Trim();
            url = Regex.Replace(url, @"^[a-z]+://", "", RegexOptions.IgnoreCase);
            var hostPort = Regex.Match(url, @"^(?<host>\[[^\]]+\]|[^:/]+):(?<port>\d+)");
            if (!hostPort.Success) return false;
            host = hostPort.Groups["host"].Value.Trim('[', ']');
            return int.TryParse(hostPort.Groups["port"].Value, out port) && port > 0;
        }

        private bool IsTcpPortOpen(string host, int port, int timeoutMs)
        {
            try
            {
                using (var client = new TcpClient())
                {
                    var result = client.BeginConnect(host, port, null, null);
                    if (!result.AsyncWaitHandle.WaitOne(timeoutMs, false)) return false;
                    client.EndConnect(result);
                    return client.Connected;
                }
            }
            catch
            {
                return false;
            }
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
                var activeDrive = ActiveDriveForProfile(p);
                AddLog(DriveDisplay(p) + " is already mounted.", "WARN");
                WarnIfMountedWithOldWriteBack(p, activeDrive);
                return;
            }
            if (!IsWinFspInstalled())
            {
                AddLog("Thiếu WinFsp. Bắt đầu luồng tải/cài WinFsp trước khi mount.", "WARN");
                if (!await EnsureWinFspAvailableAsync(false))
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
            if (!IsDriveAvailableForMount(mountDrive, p))
            {
                AddLog("Ổ " + mountDrive + " đang được Windows hoặc profile khác trong app sử dụng. Hãy chọn ký tự khác hoặc dùng Tự chọn ổ trống.", "ERROR");
                RefreshDriveLetters();
                return;
            }
            if (!await EnsureProfileTunnelAsync(p))
                return;

            WarnIfUnsafeRemoteRootMount(p);

            var preflight = await TestRemoteBeforeMountAsync(p);
            if (!preflight)
            {
                CleanupMountState(p, mountDrive);
                RenderProfiles();
                RefreshDriveLetters();
                return;
            }

            var args = BuildMountArgs(p, mountDrive, SafeVolName(p, mountDrive));
            AddLog("Mount " + p.Source + " -> " + mountDrive);
            var proc = StartRclone(args, false, p);
            proc.EnableRaisingEvents = true;
            proc.OutputDataReceived += (s, e) => AddMountedRcloneLog(p, mountDrive, e.Data, "RCLONE");
            proc.ErrorDataReceived += (s, e) => AddMountedRcloneLog(p, mountDrive, e.Data, "RCLONE");
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
            if (IsAutoDrive(p.DriveLetter))
            {
                p.DriveLetter = mountDrive;
                SaveProfiles();
            }
            RenderProfiles();
            if (await WaitForDriveReadyAsync(mountDrive, 8000))
            {
                p.RestoreOnStartup = true;
                SaveProfiles();
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

        private void AddMountedRcloneLog(DriveProfile p, string drive, string line, string level)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            var name = p == null || string.IsNullOrWhiteSpace(p.Name) ? "profile" : p.Name.Trim();
            var driveText = string.IsNullOrWhiteSpace(drive) ? "" : " " + drive.Trim();
            AddLog("[" + name + driveText + "] " + line, level);
        }

        private List<string> BuildMountArgs(DriveProfile p, string mountDrive, string volumeName)
        {
            var args = new List<string> { "mount", p.Source, mountDrive };
            var remoteType = GetRemoteType(p.Remote);
            if (!string.IsNullOrWhiteSpace(p.CacheMode) && !string.Equals(p.CacheMode, "off", StringComparison.OrdinalIgnoreCase))
            {
                args.Add("--vfs-cache-mode");
                args.Add(p.CacheMode);
                args.Add("--vfs-cache-max-age");
                args.Add(ProfileDuration(p.VfsCacheMaxAge, "72h"));
                args.Add("--vfs-write-back");
                args.Add(GetWriteBackDelay(p, remoteType));
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
            args.Add(GetDirCacheTime(p, remoteType));
            args.Add("--attr-timeout");
            args.Add(GetAttrTimeout(p, remoteType));
            args.Add("--buffer-size");
            args.Add(GetBufferSizeMb(p, remoteType) + "M");
            args.Add("--vfs-read-ahead");
            args.Add(GetReadAhead(p, remoteType));
            args.Add("--transfers");
            args.Add(GetTransferCount(p, remoteType).ToString());
            ApplyRaiDriveLikeArgs(args, p, remoteType);
            ApplyOpenCodeMountArgs(args, p, remoteType);
            ApplyHostingSafeMountArgs(args, p, remoteType);
            AddExtraArgs(args, p.ExtraArgs);
            args.Add("-v");
            return args;
        }

        private void WarnIfMountedWithOldWriteBack(DriveProfile p, string drive)
        {
            try
            {
                if (p == null || string.IsNullOrWhiteSpace(drive) || IsAutoDrive(drive)) return;
                var mount = GetRcloneMountProcesses()
                    .FirstOrDefault(x => string.Equals(NormalizeDriveChoice(x.DriveLetter), NormalizeDriveChoice(drive), StringComparison.OrdinalIgnoreCase));
                if (mount == null || string.IsNullOrWhiteSpace(mount.CommandLine)) return;
                var remoteType = GetRemoteType(p.Remote);
                var expected = GetWriteBackDelay(p, remoteType);
                var actual = ExtractArgValue(mount.CommandLine, "--vfs-write-back");
                if (!string.IsNullOrWhiteSpace(actual) &&
                    !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                {
                    AddLog("Ổ " + drive + " đang mount bằng --vfs-write-back " + actual + ", cấu hình hiện tại cần " + expected + ". Hãy bấm Làm mới ổ hoặc Ngắt/Kết nối lại để sửa code xong upload nhanh hơn.", "WARN");
                }
            }
            catch
            {
            }
        }

        private string ExtractArgValue(string commandLine, string argName)
        {
            if (string.IsNullOrWhiteSpace(commandLine) || string.IsNullOrWhiteSpace(argName)) return "";
            var pattern = Regex.Escape(argName) + @"\s+(""(?<v>[^""]+)""|'(?<v>[^']+)'|(?<v>\S+))";
            var match = Regex.Match(commandLine, pattern, RegexOptions.IgnoreCase);
            return match.Success ? match.Groups["v"].Value.Trim() : "";
        }

        private bool IsLivePreset(DriveProfile p)
        {
            return string.Equals(p == null ? "" : p.MountPreset, "Live", StringComparison.OrdinalIgnoreCase);
        }

        private bool IsOpenCodePreset(DriveProfile p)
        {
            return string.Equals(p == null ? "" : p.MountPreset, "OpenCode", StringComparison.OrdinalIgnoreCase);
        }

        private string GetDirCacheTime(DriveProfile p, string remoteType)
        {
            if (IsOpenCodePreset(p))
            {
                if (string.Equals(remoteType, "ftp", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(remoteType, "sftp", StringComparison.OrdinalIgnoreCase))
                    return "30m";
                return "10m";
            }
            if (IsLivePreset(p))
            {
                if (string.Equals(remoteType, "ftp", StringComparison.OrdinalIgnoreCase)) return "10s";
                if (string.Equals(remoteType, "sftp", StringComparison.OrdinalIgnoreCase)) return "15s";
            }
            if (string.Equals(remoteType, "ftp", StringComparison.OrdinalIgnoreCase))
                return "5m";
            if (string.Equals(remoteType, "sftp", StringComparison.OrdinalIgnoreCase))
                return "5m";
            return "30s";
        }

        private string GetAttrTimeout(DriveProfile p, string remoteType)
        {
            if (IsOpenCodePreset(p))
            {
                if (string.Equals(remoteType, "ftp", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(remoteType, "sftp", StringComparison.OrdinalIgnoreCase))
                    return "1m";
                return "30s";
            }
            if (IsLivePreset(p))
            {
                if (string.Equals(remoteType, "ftp", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(remoteType, "sftp", StringComparison.OrdinalIgnoreCase))
                    return "1s";
            }
            if (string.Equals(remoteType, "ftp", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(remoteType, "sftp", StringComparison.OrdinalIgnoreCase))
                return "30s";
            return "5s";
        }

        private string GetWriteBackDelay(DriveProfile p, string remoteType)
        {
            if ((IsOpenCodePreset(p) || IsLivePreset(p)) &&
                (string.Equals(remoteType, "ftp", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(remoteType, "sftp", StringComparison.OrdinalIgnoreCase)))
                return "1s";
            return ProfileDuration(p.VfsWriteBack, "5s");
        }

        private int GetBufferSizeMb(DriveProfile p, string remoteType)
        {
            var requested = Math.Max(1, p.BufferSizeMb);
            if (IsOpenCodePreset(p) &&
                (string.Equals(remoteType, "ftp", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(remoteType, "sftp", StringComparison.OrdinalIgnoreCase)))
                return Math.Min(16, requested);
            if (string.Equals(remoteType, "ftp", StringComparison.OrdinalIgnoreCase))
                return Math.Min(32, requested);
            if (string.Equals(remoteType, "sftp", StringComparison.OrdinalIgnoreCase))
                return Math.Min(32, requested);
            return requested;
        }

        private string GetReadAhead(DriveProfile p, string remoteType)
        {
            if (IsOpenCodePreset(p)) return "4M";
            if (IsLivePreset(p)) return "8M";
            if (string.Equals(remoteType, "ftp", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(remoteType, "sftp", StringComparison.OrdinalIgnoreCase))
                return "32M";
            return "16M";
        }

        private int GetTransferCount(DriveProfile p, string remoteType)
        {
            var requested = Math.Max(1, p.Transfers);
            if (IsOpenCodePreset(p) &&
                (string.Equals(remoteType, "ftp", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(remoteType, "sftp", StringComparison.OrdinalIgnoreCase)))
                return 1;
            if (string.Equals(remoteType, "ftp", StringComparison.OrdinalIgnoreCase))
                return 1;
            if (string.Equals(remoteType, "sftp", StringComparison.OrdinalIgnoreCase))
                return Math.Min(2, requested);
            return requested;
        }

        private void ApplyRaiDriveLikeArgs(List<string> args, DriveProfile p, string remoteType)
        {
            if (!(string.Equals(remoteType, "ftp", StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(remoteType, "sftp", StringComparison.OrdinalIgnoreCase)))
                return;

            AddArgIfMissing(args, p.ExtraArgs, "--use-server-modtime", null);
            if (!IsLivePreset(p))
            {
                AddArgIfMissing(args, p.ExtraArgs, "--vfs-fast-fingerprint", null);
                AddArgIfMissing(args, p.ExtraArgs, "--poll-interval", "0");
                if (!IsOpenCodePreset(p))
                    AddArgIfMissing(args, p.ExtraArgs, "--vfs-cache-poll-interval", "5m");
            }
            AddArgIfMissing(args, p.ExtraArgs, "--daemon-timeout", "15m");
        }

        private void ApplyOpenCodeMountArgs(List<string> args, DriveProfile p, string remoteType)
        {
            if (!IsOpenCodePreset(p)) return;

            AddLog("Áp dụng preset OpenCode: ưu tiên cache metadata/file, giảm tải kết nối khi agent đọc project.");
            AddArgIfMissing(args, p.ExtraArgs, "--vfs-cache-max-size", "20G");
            AddArgIfMissing(args, p.ExtraArgs, "--vfs-read-chunk-size", "4M");
            AddArgIfMissing(args, p.ExtraArgs, "--vfs-read-chunk-size-limit", "64M");
            AddArgIfMissing(args, p.ExtraArgs, "--vfs-cache-poll-interval", "30s");

            if (string.Equals(remoteType, "ftp", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(remoteType, "sftp", StringComparison.OrdinalIgnoreCase))
            {
                AddArgIfMissing(args, p.ExtraArgs, "--checkers", "1");
                AddArgIfMissing(args, p.ExtraArgs, "--retries", "8");
                AddArgIfMissing(args, p.ExtraArgs, "--low-level-retries", "30");
                AddArgIfMissing(args, p.ExtraArgs, "--timeout", "3m");
                AddArgIfMissing(args, p.ExtraArgs, "--contimeout", "20s");
            }
        }

        private void ApplyHostingSafeMountArgs(List<string> args, DriveProfile p, string remoteType)
        {
            if (!(string.Equals(remoteType, "ftp", StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(remoteType, "sftp", StringComparison.OrdinalIgnoreCase)))
                return;

            AddLog("Áp dụng preset hosting an toàn: giảm retry, cache ổn định, bỏ qua file hệ thống không được phép ghi.");
            AddArgIfMissing(args, p.ExtraArgs, "--checkers", "1");
            AddArgIfMissing(args, p.ExtraArgs, "--retries", "3");
            AddArgIfMissing(args, p.ExtraArgs, "--low-level-retries", "10");
            AddArgIfMissing(args, p.ExtraArgs, "--timeout", "2m");
            AddArgIfMissing(args, p.ExtraArgs, "--contimeout", "15s");

            AddHostingSystemExcludes(args);

            if (IsRemoteRootPath(p))
            {
                AddExcludePattern(args, "www/server/panel/vhost/**");
                AddExcludePattern(args, "www/wwwlogs/**");
                AddExcludePattern(args, "proc/**");
                AddExcludePattern(args, "sys/**");
                AddExcludePattern(args, "dev/**");
                AddExcludePattern(args, "run/**");
            }
        }

        private void ApplyHostingSafeTransferArgs(List<string> args, DriveProfile p)
        {
            var remoteType = GetRemoteType(p == null ? "" : p.Remote);
            if (!(string.Equals(remoteType, "ftp", StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(remoteType, "sftp", StringComparison.OrdinalIgnoreCase)))
                return;

            AddHostingSystemExcludes(args);
        }

        private void AddHostingSystemExcludes(List<string> args)
        {
            AddExcludePattern(args, ".ftpquota");
            AddExcludePattern(args, "**/.ftpquota");
            AddExcludePattern(args, ".user.ini");
            AddExcludePattern(args, "**/.user.ini");
            AddExcludePattern(args, ".well-known/acme-challenge/**");
            AddExcludePattern(args, "**/.well-known/acme-challenge/**");
        }

        private bool IsRemoteRootPath(DriveProfile p)
        {
            return string.Equals(DriveProfile.NormalizeRemotePath(p == null ? "" : p.RemotePath, p == null ? "" : p.Remote), "/", StringComparison.Ordinal);
        }

        private void WarnIfUnsafeRemoteRootMount(DriveProfile p)
        {
            var remoteType = GetRemoteType(p.Remote);
            if (!(string.Equals(remoteType, "ftp", StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(remoteType, "sftp", StringComparison.OrdinalIgnoreCase)))
                return;
            if (!IsRemoteRootPath(p))
                return;

            AddLog("Profile " + p.Name + " đang mount root /. Với FTP/SFTP để code ổn định hơn, nên đổi Đường dẫn remote sang thư mục site cụ thể như /www/wwwroot/ten-domain thay vì /.", "WARN");
            AddLog("Mount root / có thể gây lỗi permission denied ở www/server, www/wwwlogs, .git, .user.ini hoặc lỗi FTP 421 Too many connections khi IDE quét nhiều file.", "WARN");
        }

        private void AddExcludePattern(List<string> args, string pattern)
        {
            args.Add("--exclude");
            args.Add(pattern);
        }

        private void AddArgIfMissing(List<string> args, string extra, string name, string value)
        {
            if (args.Any(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase))) return;
            if (HasExtraArg(extra, name)) return;
            args.Add(name);
            if (!string.IsNullOrWhiteSpace(value))
                args.Add(value);
        }

        private async Task<bool> TestRemoteBeforeMountAsync(DriveProfile profile)
        {
            var source = profile == null ? "" : profile.Source;
            AddLog("Kiểm tra remote trước khi mount: " + source);
            var result = await RunRcloneResultAsync(profile, 45000, "rclone lsf " + source + " --max-depth 1", "lsf", source, "--max-depth", "1");
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
            if (text.IndexOf("Ftp Init Failed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("\"WinErrorCode\":1237", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "FTP khởi tạo thất bại (Ftp Init Failed / 1237). Hãy kiểm tra host, port, user/pass, passive mode, firewall/SSL và số kết nối FTP của host. Không mount. Exit code: " + exitCode;
            }
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
            MarkProfileManuallyDisconnected(p);
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

        private async Task MountAutoProfilesAsync(bool includeAlwaysAutoMount)
        {
            var pending = _profiles
                .Where(x => !string.IsNullOrWhiteSpace(x.Remote) &&
                            (x.RestoreOnStartup || (includeAlwaysAutoMount && x.AutoMount)))
                .ToList();
            if (pending.Count == 0)
            {
                AddLog(includeAlwaysAutoMount
                    ? "Không có profile cần tự mount khi khởi động."
                    : "Không có ổ đã lưu để tự mount lại.");
                return;
            }
            AddLog((includeAlwaysAutoMount ? "Tự mount " : "Mount lại ") + pending.Count + " profile đã lưu...");
            foreach (var p in pending)
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
                if (string.IsNullOrWhiteSpace(drive) || !Directory.Exists(root))
                {
                    AddLog("Không mở Explorer vì ổ không còn sẵn sàng: " + root, "WARN");
                    return;
                }
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
            var output = await RunCaptureAsync("lsjson", target, "--no-mimetype");
            List<RcloneFileItem> items = null;
            try
            {
                items = _json.Deserialize<List<RcloneFileItem>>(output);
            }
            catch (Exception ex)
            {
                AddLog("Không đọc được danh sách file JSON: " + ex.Message, "ERROR");
            }
            if (items == null) return;

            foreach (var item in items.OrderByDescending(x => x.IsDir).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            {
                var row = new ListViewItem(item.IsDir ? "Thư mục" : "File");
                row.SubItems.Add(string.IsNullOrWhiteSpace(item.Name) ? item.Path : item.Name);
                row.SubItems.Add(item.IsDir ? "" : FormatBytes(item.Size));
                row.SubItems.Add(FormatRemoteTime(item.ModTime));
                row.SubItems.Add(item.Path ?? "");
                row.Tag = item;
                browserList.Items.Add(row);
            }
        }

        private async Task BrowseOpenSelectedAsync()
        {
            if (browserList.SelectedItems.Count == 0) return;
            var item = browserList.SelectedItems[0].Tag as RcloneFileItem;
            if (item == null || !item.IsDir) return;
            browsePathBox.Text = JoinRemotePath(browsePathBox.Text, item.Name ?? item.Path);
            await BrowseListAsync();
        }

        private async Task BrowseMkdirAsync()
        {
            var name = Prompt.Show("Folder name", "Create folder");
            if (string.IsNullOrWhiteSpace(name)) return;
            await RunCaptureAsync("mkdir", JoinRemoteSource(RemotePath(browseRemoteCombo, browsePathBox), name.Trim()));
            await BrowseListAsync();
        }

        private async Task BrowseDeleteAsync()
        {
            if (browserList.SelectedItems.Count == 0) return;
            var item = browserList.SelectedItems[0].Tag as RcloneFileItem;
            var name = item == null ? browserList.SelectedItems[0].Text : (item.Name ?? item.Path);
            if (MessageBox.Show("Xóa " + name + " ?", "Xác nhận", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            var target = JoinRemoteSource(RemotePath(browseRemoteCombo, browsePathBox), name.TrimEnd('/'));
            await RunCaptureAsync(item != null && item.IsDir ? "purge" : "deletefile", target);
            await BrowseListAsync();
        }

        private string JoinRemotePath(string current, string child)
        {
            child = (child ?? "").Trim().Trim('/');
            if (string.IsNullOrWhiteSpace(child)) return string.IsNullOrWhiteSpace(current) ? "/" : current.Trim();
            var basePath = string.IsNullOrWhiteSpace(current) ? "/" : current.Trim().Replace('\\', '/').TrimEnd('/');
            if (string.Equals(basePath, "/", StringComparison.Ordinal)) return "/" + child;
            return basePath + "/" + child;
        }

        private string JoinRemoteSource(string source, string child)
        {
            child = (child ?? "").Trim().Trim('/');
            if (string.IsNullOrWhiteSpace(child)) return source;
            return source.TrimEnd('/') + "/" + child;
        }

        private string FormatRemoteTime(string value)
        {
            DateTime parsed;
            if (DateTime.TryParse(value, out parsed))
                return parsed.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            return value ?? "";
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

        private string GetRcloneConfigPathSync()
        {
            var output = RunCommandCapture(_rcloneExe, "config file");
            foreach (var line in output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (trimmed.EndsWith(".conf", StringComparison.OrdinalIgnoreCase) && File.Exists(trimmed))
                    return trimmed;
            }
            return "";
        }

        private string GetRemoteType(string remote)
        {
            return GetRemoteConfigValue(remote, "type");
        }

        private string GetRemoteConfigValue(string remote, string key)
        {
            var name = (remote ?? "").Trim().TrimEnd(':');
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(key)) return "";
            try
            {
                var configPath = GetRcloneConfigPathSync();
                if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath)) return "";
                string currentSection = "";
                foreach (var rawLine in File.ReadLines(configPath))
                {
                    var line = rawLine.Trim();
                    if (line.StartsWith("[") && line.EndsWith("]"))
                    {
                        currentSection = line.Substring(1, line.Length - 2).Trim();
                        continue;
                    }
                    if (!string.Equals(currentSection, name, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!line.StartsWith(key, StringComparison.OrdinalIgnoreCase)) continue;
                    var idx = line.IndexOf('=');
                    if (idx < 0) continue;
                    return line.Substring(idx + 1).Trim();
                }
            }
            catch (Exception ex)
            {
                AddLog("Không đọc được loại remote " + name + ": " + ex.Message, "WARN");
            }
            return "";
        }

        private string GetConfigNameFromUiOrSelection()
        {
            var name = (configNameBox != null ? configNameBox.Text : "").Trim();
            if (!string.IsNullOrWhiteSpace(name))
                return name.TrimEnd(':');
            var profile = SelectedProfile;
            if (profile != null && !string.IsNullOrWhiteSpace(profile.Remote))
                return profile.Remote.Trim().TrimEnd(':');
            var selectedRemote = Convert.ToString(remoteCombo != null ? (remoteCombo.SelectedItem ?? remoteCombo.Text) : "");
            if (!string.IsNullOrWhiteSpace(selectedRemote))
                return selectedRemote.Trim().TrimEnd(':');
            return "";
        }

        private string ReadRemoteConfigSection(string remoteName)
        {
            remoteName = (remoteName ?? "").Trim().TrimEnd(':');
            if (string.IsNullOrWhiteSpace(remoteName)) return "";
            var configPath = GetRcloneConfigPathSync();
            if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath)) return "";

            var lines = new List<string>();
            var inSection = false;
            foreach (var rawLine in File.ReadLines(configPath))
            {
                var line = rawLine.Trim();
                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    if (inSection) break;
                    inSection = string.Equals(line.Substring(1, line.Length - 2).Trim(), remoteName, StringComparison.OrdinalIgnoreCase);
                    if (inSection) lines.Add(rawLine);
                    continue;
                }
                if (inSection) lines.Add(MaskConfigLine(rawLine));
            }
            return string.Join(Environment.NewLine, lines);
        }

        private string MaskConfigLine(string rawLine)
        {
            var idx = rawLine.IndexOf('=');
            if (idx < 0) return rawLine;
            var key = rawLine.Substring(0, idx).Trim();
            if (IsSensitiveConfigKey(key))
                return rawLine.Substring(0, idx + 1) + " <ẩn>";
            return rawLine;
        }

        private bool IsSensitiveConfigKey(string key)
        {
            key = (key ?? "").ToLowerInvariant();
            return key.Contains("pass") ||
                   key.Contains("token") ||
                   key.Contains("secret") ||
                   key.Contains("key") ||
                   key.Contains("client_id");
        }

        private void ShowConfigFromUi()
        {
            var name = GetConfigNameFromUiOrSelection();
            if (!IsValidRemoteName(name))
            {
                MessageBox.Show("Hãy nhập tên remote hoặc chọn một profile trước.", "Trình quản lý ổ Rclone", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var section = ReadRemoteConfigSection(name);
            if (string.IsNullOrWhiteSpace(section))
            {
                MessageBox.Show("Không tìm thấy config: " + name + ":", "Trình quản lý ổ Rclone", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            ShowTextDialog("Config " + name + ":", section);
            AddLog("Đã mở xem config: " + name + ":");
        }

        private void ShowTextDialog(string title, string text)
        {
            using (var form = new Form())
            using (var box = new TextBox())
            using (var close = new Button())
            {
                form.Text = title;
                form.Width = 760;
                form.Height = 520;
                form.StartPosition = FormStartPosition.CenterParent;
                form.MinimizeBox = false;
                form.MaximizeBox = true;
                box.Multiline = true;
                box.ReadOnly = true;
                box.ScrollBars = ScrollBars.Both;
                box.WordWrap = false;
                box.Dock = DockStyle.Fill;
                box.Font = new Font("Consolas", 9.5F);
                box.Text = text;
                close.Text = "Đóng";
                close.Dock = DockStyle.Bottom;
                close.Height = 38;
                close.DialogResult = DialogResult.OK;
                form.Controls.Add(box);
                form.Controls.Add(close);
                form.AcceptButton = close;
                form.ShowDialog(this);
            }
        }

        private async Task DeleteConfigFromUiAsync()
        {
            var name = GetConfigNameFromUiOrSelection();
            if (!IsValidRemoteName(name))
            {
                MessageBox.Show("Hãy nhập tên remote hoặc chọn một profile trước.", "Trình quản lý ổ Rclone", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var relatedProfiles = _profiles.Where(p => string.Equals((p.Remote ?? "").Trim().TrimEnd(':'), name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (relatedProfiles.Any(IsMountedProfile))
            {
                MessageBox.Show("Remote này đang có ổ mount. Hãy ngắt kết nối trước khi xóa config.", "Trình quản lý ổ Rclone", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            var message = "Xóa rclone config " + name + ": ?";
            if (relatedProfiles.Count > 0)
                message += Environment.NewLine + "Đồng thời xóa " + relatedProfiles.Count + " profile đang dùng remote này.";
            if (MessageBox.Show(message, "Xóa config", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            var output = await RunCaptureAsync("config", "delete", name);
            await RefreshRemotesAsync();
            if (_remotes.Any(r => string.Equals(r.TrimEnd(':'), name, StringComparison.OrdinalIgnoreCase)))
            {
                configCheckLabel.Text = "Delete failed";
                AddLog("Xóa config thất bại: " + name + ": " + output, "ERROR");
                return;
            }

            foreach (var p in relatedProfiles)
                _profiles.Remove(p);
            SaveProfiles();
            RenderProfiles();
            SelectFirstProfile();
            if (configNameBox != null && string.Equals((configNameBox.Text ?? "").Trim().TrimEnd(':'), name, StringComparison.OrdinalIgnoreCase))
                configNameBox.Clear();
            if (configCheckLabel != null)
                configCheckLabel.Text = "Deleted";
            AddLog("Đã xóa config và profile liên quan: " + name + ":");
        }

        private async Task<bool> CheckConfigConnectionAsync(bool showMessage)
        {
            var name = GetConfigNameFromUiOrSelection();
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
            if (RemoteExists(name))
            {
                var testPath = DriveProfile.NormalizeRemotePath(configTestPathBox.Text, name + ":");
                AddLog("Kiểm tra config đã có: " + name + ":" + testPath);
                var result = await RunRcloneResultAsync(20000, "rclone lsd " + name + ":" + testPath, "lsd", name + ":" + testPath);
                if (result.ExitCode != 0)
                    result = await RunRcloneResultAsync(20000, "rclone lsf " + name + ":" + testPath + " --max-depth 1", "lsf", name + ":" + testPath, "--max-depth", "1");
                if (result.ExitCode == 0)
                {
                    configCheckLabel.Text = "Config OK";
                    if (showMessage) AddLog("Config kết nối OK: " + name + ":");
                    return true;
                }
                configCheckLabel.Text = "Config lỗi";
                AddLog("Config chưa kết nối được: " + name + ": " + result.Output, "ERROR");
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

        private bool RemoteExists(string name)
        {
            name = (name ?? "").Trim().TrimEnd(':');
            return _remotes.Any(r => string.Equals(r.TrimEnd(':'), name, StringComparison.OrdinalIgnoreCase));
        }

        private async Task AddConfigFromUiAsync()
        {
            var name = (configNameBox.Text ?? "").Trim();
            var type = Convert.ToString(configTypeCombo.SelectedItem ?? "").Trim();
            if (RemoteExists(name))
            {
                configCheckLabel.Text = "Remote already exists";
                AddLog("Remote đã tồn tại. Dùng Lưu config để cập nhật: " + name + ":", "ERROR");
                return;
            }
            if (!await CheckConfigConnectionAsync(false)) return;

            var args = new List<string> { "--non-interactive", "config", "create", name, type };
            var parameters = await BuildConfigParametersFromUiAsync();

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
                Name = UniqueProfileName(name),
                Remote = name + ":",
                RemotePath = testPath,
                DriveLetter = GetFreeDriveLetters().FirstOrDefault() ?? "Z:",
                CacheMode = "full",
                CacheDir = "%USERPROFILE%\\.cache\\rclone",
                LocalWorkDir = GetDefaultLocalWorkDir(name),
                VfsCacheMaxAge = "72h",
                VfsWriteBack = "5s",
                NetworkMode = true,
                Transfers = 4,
                BufferSizeMb = 32,
                MountPreset = "Nhanh/RaiDrive"
            };
            ApplyTunnelDefaultsForNewProfile(profile, "Thêm config");
            _profiles.Add(profile);
            SaveProfiles();
            RenderProfiles();
            SelectProfile(profile);
            AddLog("Added config and profile: " + name + ":");
        }

        private async Task SaveConfigFromUiAsync()
        {
            var name = (configNameBox.Text ?? "").Trim();
            var type = Convert.ToString(configTypeCombo.SelectedItem ?? "").Trim();
            if (!IsValidRemoteName(name))
            {
                configCheckLabel.Text = "Invalid remote name";
                AddLog("Remote name must use letters, numbers, dash, underscore or dot only.", "ERROR");
                return;
            }
            if (string.IsNullOrWhiteSpace(type))
            {
                configCheckLabel.Text = "Missing type";
                AddLog("Select a storage type first.", "ERROR");
                return;
            }

            var exists = RemoteExists(name);
            var parameters = await BuildConfigParametersFromUiAsync();
            if (exists)
                parameters["type"] = type;
            var args = new List<string> { "--non-interactive", "config", exists ? "update" : "create", name };
            if (!exists)
                args.Add(type);
            foreach (var pair in parameters)
            {
                if (!string.IsNullOrWhiteSpace(pair.Value))
                {
                    args.Add(pair.Key);
                    args.Add(pair.Value);
                }
            }

            AddLog((exists ? "Updating config " : "Creating config ") + name + ": type " + type);
            var safe = "rclone --non-interactive config " + (exists ? "update " : "create ") + name + (exists ? "" : " " + type) + " ... pass=<ẩn>";
            var output = await RunCaptureSensitiveAsync(args.ToArray(), safe);
            await RefreshRemotesAsync();

            if (!RemoteExists(name))
            {
                configCheckLabel.Text = "Save failed";
                AddLog("Không lưu được config. rclone output: " + output, "ERROR");
                return;
            }

            EnsureProfileForConfig(name);
            configCheckLabel.Text = exists ? "Saved" : "Created";
            AddLog("Đã lưu config: " + name + ":");
            await CheckConfigConnectionAsync(false);
        }

        private async Task<Dictionary<string, string>> BuildConfigParametersFromUiAsync()
        {
            var parameters = ParseConfigParameters();
            if (!string.IsNullOrWhiteSpace(configUserBox.Text))
                parameters["user"] = configUserBox.Text.Trim();
            if (!string.IsNullOrEmpty(configPassBox.Text))
            {
                parameters["pass"] = configObscurePassBox.Checked
                    ? await ObscurePasswordAsync(configPassBox.Text)
                    : configPassBox.Text;
            }
            return parameters;
        }

        private void EnsureProfileForConfig(string name)
        {
            var remote = name.TrimEnd(':') + ":";
            var profile = _profiles.FirstOrDefault(p => string.Equals((p.Remote ?? "").Trim(), remote, StringComparison.OrdinalIgnoreCase));
            var testPath = DriveProfile.NormalizeRemotePath(configTestPathBox.Text, remote);
            if (profile == null)
            {
                profile = new DriveProfile
                {
                    Name = UniqueProfileName(name),
                    Remote = remote,
                    DriveLetter = GetFreeDriveLetters().FirstOrDefault() ?? "Z:",
                    CacheMode = "full",
                    CacheDir = "%USERPROFILE%\\.cache\\rclone",
                    LocalWorkDir = GetDefaultLocalWorkDir(name),
                    VfsCacheMaxAge = "72h",
                    VfsWriteBack = "5s",
                    NetworkMode = true,
                    Transfers = 4,
                    BufferSizeMb = 32,
                    MountPreset = "Nhanh/RaiDrive"
                };
                ApplyTunnelDefaultsForNewProfile(profile, "Lưu config");
                _profiles.Add(profile);
            }
            profile.RemotePath = testPath;
            SaveProfiles();
            RenderProfiles();
            SelectProfile(profile);
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
            return RunRcloneResultAsync(null, timeoutMs, safeLogLine, args);
        }

        private Task<RcloneResult> RunRcloneResultAsync(DriveProfile profile, int timeoutMs, string safeLogLine, params string[] args)
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
                    ApplyTunnelEnvironment(psi, profile);

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

        private Process StartRclone(List<string> args, bool shell, DriveProfile profile = null)
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
            ApplyTunnelEnvironment(psi, profile);
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
                    "echo Không tìm thấy ký tự ổ đĩa trống.",
                    "pause",
                    "exit /b 1",
                    ":found"
                };
                var args = BuildMountArgs(p, "%DRIVE%", SafeVolName(p) + " %DRIVE:~0,1%");
                lines.Add("\"%~dp0rclone.exe\" " + string.Join(" ", args.Select(QuoteIfNeeded)));
                lines.Add("pause");
                File.WriteAllText(file, string.Join("\r\n", lines) + "\r\n", new UTF8Encoding(false));
            }
            else
            {
                var drive = NormalizeDriveChoice(p.DriveLetter);
                var args = BuildMountArgs(p, drive, SafeVolName(p, drive));
                File.WriteAllText(file, "@echo off\r\ncd /d \"%~dp0\"\r\n\"%~dp0rclone.exe\" " + string.Join(" ", args.Select(QuoteIfNeeded)) + "\r\npause\r\n", Encoding.ASCII);
            }
            AddLog("Đã tạo " + file);
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
                lines.Add("set /p DRIVE=Nhập ký tự ổ cần ngắt, ví dụ X: ");
            }
            else
            {
                lines.Add("set \"DRIVE=" + NormalizeDriveChoice(p.DriveLetter) + "\"");
            }
            lines.Add("if \"%DRIVE:~-1%\" NEQ \":\" set \"DRIVE=%DRIVE%:\"");
            lines.Add("echo Đang ngắt %DRIVE% ...");
            lines.Add("powershell -NoProfile -ExecutionPolicy Bypass -Command \"$d=$env:DRIVE; Get-CimInstance Win32_Process -Filter \\\"Name='rclone.exe'\\\" | Where-Object { $_.CommandLine -match ' mount ' -and $_.CommandLine -like ('* ' + $d + '*') } | ForEach-Object { Write-Host ('Stop rclone PID ' + $_.ProcessId); Stop-Process -Id $_.ProcessId -Force }\"");
            lines.Add("net use %DRIVE% /delete /y");
            lines.Add("timeout /t 2 /nobreak >nul");
            lines.Add("if exist %DRIVE%\\ (");
            lines.Add("  echo Vẫn còn thấy ổ %DRIVE%. Hãy chạy file này cùng quyền với lúc mount hoặc mở Task Manager kill rclone.exe mount.");
            lines.Add(") else (");
            lines.Add("  echo Đã ngắt %DRIVE%.");
            lines.Add(")");
            lines.Add("pause");
            File.WriteAllText(file, string.Join("\r\n", lines) + "\r\n", new UTF8Encoding(false));
            AddLog("Đã tạo " + file);
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

    public class RoundedButton : Button
    {
        public int BorderRadius { get; set; } = 6;
        public Color BorderColor { get; set; } = Color.Transparent;
        public int BorderSize { get; set; } = 1;

        public Color HoverBackColor { get; set; }
        public Color HoverBorderColor { get; set; }
        public Color HoverForeColor { get; set; }
        public Color PressedBackColor { get; set; }
        public Color PressedBorderColor { get; set; }
        public Color PressedForeColor { get; set; }

        private bool _isHovered = false;
        private bool _isPressed = false;

        public RoundedButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Cursor = Cursors.Hand;
            DoubleBuffered = true;
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _isHovered = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _isHovered = false;
            _isPressed = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs mevent)
        {
            if (mevent.Button == MouseButtons.Left)
            {
                _isPressed = true;
                Invalidate();
            }
            base.OnMouseDown(mevent);
        }

        protected override void OnMouseUp(MouseEventArgs mevent)
        {
            _isPressed = false;
            Invalidate();
            base.OnMouseUp(mevent);
        }

        private System.Drawing.Drawing2D.GraphicsPath GetRoundedPath(Rectangle rect, float radius)
        {
            var path = new System.Drawing.Drawing2D.GraphicsPath();
            float r2 = radius * 2f;
            path.StartFigure();
            path.AddArc(rect.X, rect.Y, r2, r2, 180, 90);
            path.AddArc(rect.Right - r2, rect.Y, r2, r2, 270, 90);
            path.AddArc(rect.Right - r2, rect.Bottom - r2, r2, r2, 0, 90);
            path.AddArc(rect.X, rect.Bottom - r2, r2, r2, 90, 90);
            path.CloseFigure();
            return path;
        }

        protected override void OnPaint(PaintEventArgs pevent)
        {
            var g = pevent.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;

            var rect = ClientRectangle;

            var backColor = BackColor;
            var borderColor = BorderColor;
            var foreColor = ForeColor;

            if (!Enabled)
            {
                backColor = Color.FromArgb(204, 209, 216);
                borderColor = Color.FromArgb(226, 232, 240);
                foreColor = Color.FromArgb(156, 163, 175);
            }
            else
            {
                if (_isPressed)
                {
                    backColor = PressedBackColor != Color.Empty ? PressedBackColor : BackColor;
                    borderColor = PressedBorderColor != Color.Empty ? PressedBorderColor : (BorderColor != Color.Transparent ? BorderColor : backColor);
                    foreColor = PressedForeColor != Color.Empty ? PressedForeColor : ForeColor;
                }
                else if (_isHovered)
                {
                    backColor = HoverBackColor != Color.Empty ? HoverBackColor : BackColor;
                    borderColor = HoverBorderColor != Color.Empty ? HoverBorderColor : (BorderColor != Color.Transparent ? BorderColor : backColor);
                    foreColor = HoverForeColor != Color.Empty ? HoverForeColor : ForeColor;
                }
                else
                {
                    if (borderColor == Color.Transparent)
                        borderColor = backColor;
                }
            }

            var clearColor = SystemColors.Control;
            var p = Parent;
            while (p != null)
            {
                if (p.BackColor != Color.Transparent && p.BackColor != Color.Empty) { clearColor = p.BackColor; break; }
                p = p.Parent;
            }
            using (var clearBrush = new SolidBrush(clearColor))
            {
                g.FillRectangle(clearBrush, rect);
            }

            if (rect.Width > BorderRadius * 2 && rect.Height > BorderRadius * 2)
            {
                var drawRect = new Rectangle(rect.X, rect.Y, rect.Width - 1, rect.Height - 1);
                using (var path = GetRoundedPath(drawRect, BorderRadius))
                {
                    using (var brush = new SolidBrush(backColor))
                    {
                        g.FillPath(brush, path);
                    }

                    if (BorderSize > 0 && borderColor != Color.Transparent)
                    {
                        using (var pen = new Pen(borderColor, BorderSize))
                        {
                            pen.Alignment = System.Drawing.Drawing2D.PenAlignment.Inset;
                            g.DrawPath(pen, path);
                        }
                    }
                }
            }
            else
            {
                using (var brush = new SolidBrush(backColor))
                {
                    g.FillRectangle(brush, rect);
                }
            }

            var textFlags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            var textRect = new Rectangle(rect.X + 4, rect.Y + 2, rect.Width - 8, rect.Height - 4);
            TextRenderer.DrawText(g, Text, Font, textRect, foreColor, textFlags);
        }
    }
}

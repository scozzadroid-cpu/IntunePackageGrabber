// Intune Package Grabber
// Captures the Win32 packages that Intune extracts to C:\Windows\IMECache during an installation
// (e.g. from Company Portal), saves their content and rebuilds the .intunewin file with IntuneWinAppUtil.
// Diagnostics (Diagnostics.cs): Win32 apps and remediations known by IME on this device, log collection for support.
// About & credits: downloads and launches IntuneWin Downloader by Rudy Ooms (call4cloud).
//
// Command line options (for testing): --cache <folder> --dest <folder> --log <file> --imelogs <folder> --imereg <HKLM|HKCU\key>
// --listen --exit-after <seconds>

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using System.Xml;

[assembly: AssemblyTitle("Intune Package Grabber")]
[assembly: AssemblyProduct("Intune Package Grabber")]
[assembly: AssemblyVersion("2.2.0.0")]
[assembly: AssemblyFileVersion("2.2.0.0")]
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]

namespace IntunePackageGrabber
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            // Paths over 260 characters (long destinations, e.g. OneDrive): disable .NET legacy path handling
            AppContext.SetSwitch("Switch.System.IO.UseLegacyPathHandling", false);
            AppContext.SetSwitch("Switch.System.IO.BlockLongPaths", false);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(args));
        }
    }

    // Authenticode signature check of a file (WinVerifyTrust)
    static class Authenticode
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WinTrustFileInfo
        {
            public uint cbStruct;
            public string pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct WinTrustData
        {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public IntPtr pFile;
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
            public IntPtr pSignatureSettings;
        }

        [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
        static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid action, ref WinTrustData data);

        static readonly Guid GenericVerifyV2 = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        public static bool IsValid(string path)
        {
            var file = new WinTrustFileInfo { cbStruct = (uint)Marshal.SizeOf(typeof(WinTrustFileInfo)), pcwszFilePath = path };
            IntPtr pFile = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WinTrustFileInfo)));
            try
            {
                Marshal.StructureToPtr(file, pFile, false);
                // No UI, file, verify
                var data = new WinTrustData { cbStruct = (uint)Marshal.SizeOf(typeof(WinTrustData)), dwUIChoice = 2, dwUnionChoice = 1, pFile = pFile, dwStateAction = 1 };
                int result = WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, ref data);
                data.dwStateAction = 2;
                WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, ref data);
                return result == 0;
            }
            finally
            {
                Marshal.DestroyStructure(pFile, typeof(WinTrustFileInfo));
                Marshal.FreeHGlobal(pFile);
            }
        }
    }

    class PendingFolder
    {
        public string Snapshot;
        public DateTime Since;
    }

    class AppPolicy
    {
        public string Id;
        public string Name;
        public string Version;
        public string Intent;
        public string SetupFile;
        public string InstallCommand;
        public string UninstallCommand;
        public string DetectionRule;
        public string RequirementRules;
        public string ReturnCodes;
        public string RawJson;
    }

    partial class MainForm : Form
    {
        static string ImeLogFolder = @"C:\ProgramData\Microsoft\IntuneManagementExtension\Logs";
        const int StableSeconds = 3;

        // List columns
        const int ColTime = 0, ColApp = 1, ColVersion = 2, ColStatus = 3, ColSize = 4, ColPath = 5;

        readonly string cacheRoot = @"C:\Windows\IMECache";
        static readonly string appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IntunePackageGrabber");
        readonly string settingsFile = Path.Combine(appDataDir, "settings.txt");
        string logFile = Path.Combine(appDataDir, "grabber.log");
        readonly Dictionary<string, PendingFolder> pending = new Dictionary<string, PendingFolder>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> handled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        int activeJobs;
        bool listening, scanning;
        Color dotColor = IdleColor;

        TextBox txtDest;
        TextBox txtLog;
        Button btnBrowse, btnListen, btnStop, btnOpen, btnDownload;
        CheckBox chkIntunewin;
        Label lblStatus;
        Panel dot, settingsWrap;
        ListView list;
        TabStrip tabs;
        int newCaptures;

        public MainForm(string[] args)
        {
            string argDest = null;
            bool autoListen = false;
            int exitAfter = 0;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--cache" && i + 1 < args.Length) cacheRoot = args[++i];
                else if (args[i] == "--dest" && i + 1 < args.Length) argDest = args[++i];
                else if (args[i] == "--log" && i + 1 < args.Length) logFile = args[++i];
                else if (args[i] == "--imelogs" && i + 1 < args.Length) ImeLogFolder = args[++i];
                else if (args[i] == "--imereg" && i + 1 < args.Length) ImeRegistry = args[++i];
                else if (args[i] == "--exit-after" && i + 1 < args.Length) int.TryParse(args[++i], out exitAfter);
                else if (args[i] == "--listen") autoListen = true;
            }
            try { Directory.CreateDirectory(Path.GetDirectoryName(logFile)); } catch { }

            BuildUi();

            // Automated tests only: closes after N seconds, waiting for running captures
            if (exitAfter > 0)
            {
                var exitTimer = new System.Windows.Forms.Timer { Interval = exitAfter * 1000 };
                exitTimer.Tick += delegate
                {
                    if (activeJobs > 0) return;
                    exitTimer.Stop();
                    Log("Automatic exit (--exit-after).");
                    Close();
                };
                exitTimer.Start();
            }

            timer.Interval = 500;
            timer.Tick += OnTick;

            txtDest.Text = argDest ?? LoadSettings() ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "IntunePackages");
            SetListening(false);
            Log("Choose the archive folder and press Listen before starting the installation.");
            if (!HasIntuneWinAppUtil()) Log("IntuneWinAppUtil.exe not found: press \"Download IntuneWinAppUtil\" to also create the .intunewin, otherwise only the content will be saved.");
            UpdateDownloadButton();
            if (autoListen) Shown += delegate { StartListening(); };
        }

        #region UI

        void BuildUi()
        {
            // Layout designed at 96 DPI: WinForms scales it automatically to the screen DPI
            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = "Intune Package Grabber";
            Font = new Font(UiFont, 9.5f);
            BackColor = Background;
            ForeColor = TextPrimary;
            ClientSize = new Size(960, 640);
            MinimumSize = new Size(760, 520);
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            UseDarkTitleBar(this);

            // Header
            var header = new Panel { Dock = DockStyle.Top, Size = new Size(960, 72), BackColor = Background };
            header.Paint += (s, e) =>
            {
                using (var brush = new LinearGradientBrush(header.ClientRectangle, HeaderStart, Background, LinearGradientMode.Horizontal))
                    e.Graphics.FillRectangle(brush, header.ClientRectangle);
            };
            var title = new Label { Text = "Intune Package Grabber", ForeColor = TextPrimary, BackColor = Color.Transparent, Font = new Font(DisplaySemibold, 15f), AutoSize = true, Location = new Point(20, 8) };
            var subtitle = new Label { Text = "Captures Win32 packages during Company Portal installations and diagnoses Intune apps and remediations", ForeColor = TextMuted, BackColor = Color.Transparent, AutoSize = true, Location = new Point(22, 46) };
            var about = HeaderButton("About & credits", 140, delegate { ShowAbout(); });
            about.Location = new Point(header.Width - 20 - about.Width, 14);
            var tutorial = HeaderButton("Tutorial", 100, delegate { ShowTutorial(); });
            tutorial.Location = new Point(about.Left - 8 - tutorial.Width, 14);
            header.Controls.AddRange(new Control[] { title, subtitle, tutorial, about });

            // Settings: the archive folder is used by captures, exports and log collection
            var settings = new Panel { Dock = DockStyle.Top, Height = 104, Padding = new Padding(20, 0, 20, 0) };
            // Initial size = design size, so the anchors keep the margins
            var card = MakeCard();
            card.Size = new Size(920, 104);
            var lblDest = new Label { Text = "Archive folder  (captures, exports and logs)", Font = new Font(UiSemibold, 9.5f), AutoSize = true, Location = new Point(14, 10) };
            txtDest = new TextBox { Location = new Point(16, 36), Size = new Size(638, 25), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, Font = new Font(UiFont, 10f), BackColor = ControlBack, ForeColor = TextPrimary, BorderStyle = BorderStyle.FixedSingle };
            btnBrowse = MakeButton("Browse...", ControlBack, TextPrimary);
            btnBrowse.Location = new Point(664, 34);
            btnBrowse.Size = new Size(120, 30);
            btnBrowse.Font = new Font(UiFont, 9.5f);
            btnBrowse.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnBrowse.Click += OnBrowse;
            btnOpen = MakeButton("Open folder", ControlBack, TextPrimary);
            btnOpen.Location = new Point(794, 34);
            btnOpen.Size = new Size(110, 30);
            btnOpen.Font = new Font(UiFont, 9.5f);
            btnOpen.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnOpen.Click += delegate { OpenFolder(txtDest.Text); };
            chkIntunewin = new CheckBox { Text = "Also create the .intunewin file (IntuneWinAppUtil, same setup file as the Intune policy)", Checked = true, AutoSize = true, Location = new Point(16, 72), ForeColor = TextMuted, FlatStyle = FlatStyle.Flat };
            chkIntunewin.FlatAppearance.BorderColor = IdleColor;
            chkIntunewin.FlatAppearance.CheckedBackColor = ControlBack;
            chkIntunewin.CheckedChanged += delegate { UpdateDownloadButton(); };
            btnDownload = MakeButton("↓  Download IntuneWinAppUtil", Accent, Color.Black);
            btnDownload.Location = new Point(704, 68);
            btnDownload.Size = new Size(200, 28);
            btnDownload.Font = new Font(UiSemibold, 9f);
            btnDownload.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnDownload.Visible = false;
            btnDownload.Click += OnDownloadIntuneWinAppUtil;
            card.Controls.AddRange(new Control[] { lblDest, txtDest, btnBrowse, btnOpen, chkIntunewin, btnDownload });
            card.Dock = DockStyle.Fill;
            settings.Controls.Add(card);
            settingsWrap = new Panel { Dock = DockStyle.Top, Height = 120, Padding = new Padding(0, 16, 0, 0) };
            settingsWrap.Controls.Add(settings);
            settings.Dock = DockStyle.Fill;

            // Capture tab: Listen / Stop and status above the captured packages
            var actions = new Panel { Dock = DockStyle.Top, Size = new Size(920, 50) };
            btnListen = MakeButton("\u25B6  Listen", GreenFill, Color.White);
            btnListen.Location = new Point(0, 2);
            btnListen.Click += delegate { StartListening(); };
            btnStop = MakeButton("\u25A0  Stop", RedFill, Color.White);
            btnStop.Location = new Point(136, 2);
            btnStop.Click += delegate { StopListening(); };
            dot = new Panel { Size = new Size(14, 14), Location = new Point(282, 14) };
            dot.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var b = new SolidBrush(dotColor)) e.Graphics.FillEllipse(b, 0, 0, dot.Width - 1, dot.Height - 1);
            };
            lblStatus = new Label
            {
                AutoSize = false,
                AutoEllipsis = true,
                Location = new Point(302, 10),
                Size = new Size(618, 24),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Font = new Font(UiSemibold, 10f)
            };
            actions.Controls.AddRange(new Control[] { btnListen, btnStop, dot, lblStatus });

            // Global action, next to the tabs
            btnCollectLogs = MakeButton("Collect logs", ControlBack, TextPrimary);
            btnCollectLogs.Size = new Size(130, 32);
            btnCollectLogs.Font = new Font(UiSemibold, 9.5f);
            btnCollectLogs.Click += OnCollectLogs;
            new ToolTip().SetToolTip(btnCollectLogs, "Creates a zip in the archive folder with the IME and Company Portal logs, the IME registry,\r\nthe MDM event log, dsregcmd /status and the state of the Win32 apps and remediations.");

            // Package list + log
            list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                BorderStyle = BorderStyle.None,
                Font = new Font(UiFont, 9.5f)
            };
            UseDarkListView(list);
            // ListView columns are not scaled by AutoScale: scale them manually
            float scale;
            using (var g = CreateGraphics()) scale = g.DpiX / 96f;
            list.Columns.Add("Time", (int)(70 * scale));
            list.Columns.Add("Application", (int)(250 * scale));
            list.Columns.Add("Ver.", (int)(45 * scale));
            list.Columns.Add("Status", (int)(190 * scale));
            list.Columns.Add("Size", (int)(90 * scale));
            list.Columns.Add("Path", (int)(500 * scale));
            list.DoubleClick += delegate
            {
                if (list.SelectedItems.Count > 0) OpenFolder(list.SelectedItems[0].SubItems[ColPath].Text);
            };

            txtLog = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                BorderStyle = BorderStyle.None,
                BackColor = ConsoleBack,
                ForeColor = ConsoleText,
                Font = new Font(MonoFont, 9f)
            };
            UseDarkScrollbars(txtLog);

            var capturePage = new Panel();
            capturePage.Controls.Add(WrapInCard(list, "Captured packages  (double-click to open)"));
            capturePage.Controls.Add(actions);

            tabs = new TabStrip { Dock = DockStyle.Fill };
            tabs.AddPage("Capture", MakeTab(capturePage));
            tabs.AddPage("Win32 apps", MakeTab(BuildAppsTab()));
            tabs.AddPage("Remediations", MakeTab(BuildRemediationsTab()));
            tabs.AddTool(btnCollectLogs);
            tabs.SelectedIndexChanged += delegate { UpdateContext(); };

            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, BackColor = Background, SplitterWidth = 12 };
            split.Panel1.Controls.Add(tabs);
            split.Panel2.Controls.Add(WrapInCard(txtLog, "Log"));
            var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20, 0, 20, 20) };
            body.Controls.Add(split);
            Shown += delegate { split.SplitterDistance = (int)(split.Height * 0.6); };

            Controls.Add(body);
            Controls.Add(settingsWrap);
            Controls.Add(header);

            FormClosing += OnFormClosing;
            ResumeLayout(false);
            PerformLayout();
        }

        Panel MakeCard()
        {
            var card = new Panel { BackColor = CardBack };
            card.Paint += (s, e) =>
            {
                var p = (Panel)s;
                using (var pen = new Pen(Border)) e.Graphics.DrawRectangle(pen, 0, 0, p.Width - 1, p.Height - 1);
            };
            return card;
        }

        Control WrapInCard(Control inner, string caption)
        {
            var card = MakeCard();
            card.Dock = DockStyle.Fill;
            card.Padding = new Padding(1, 32, 1, 1);
            var lbl = new Label { Text = caption, Font = new Font(UiSemibold, 9.5f), AutoSize = true, Location = new Point(12, 8) };
            card.Controls.Add(inner);
            card.Controls.Add(lbl);
            return card;
        }

        Button MakeButton(string text, Color back, Color fore)
        {
            var b = new Button
            {
                Text = text,
                FlatStyle = FlatStyle.Flat,
                BackColor = back,
                ForeColor = fore,
                Size = new Size(126, 38),
                Font = new Font(UiSemibold, 10f),
                Cursor = Cursors.Hand,
                Tag = back
            };
            b.FlatAppearance.BorderSize = back == ControlBack ? 1 : 0;
            b.FlatAppearance.BorderColor = Border;
            b.EnabledChanged += delegate
            {
                b.BackColor = b.Enabled ? (Color)b.Tag : DisabledBack;
            };
            // Windows draws disabled text in a dark gray that is not readable on the dark theme
            b.Paint += (s, e) =>
            {
                if (b.Enabled) return;
                using (var fill = new SolidBrush(DisabledBack)) e.Graphics.FillRectangle(fill, b.ClientRectangle);
                if (b.FlatAppearance.BorderSize > 0)
                    using (var pen = new Pen(Border)) e.Graphics.DrawRectangle(pen, 0, 0, b.Width - 1, b.Height - 1);
                TextRenderer.DrawText(e.Graphics, b.Text, b.Font, b.ClientRectangle, DisabledText, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.HidePrefix);
            };
            return b;
        }

        void ShowAbout()
        {
            using (var about = new Form
            {
                Text = "About " + Text,
                AutoScaleDimensions = new SizeF(96F, 96F),
                AutoScaleMode = AutoScaleMode.Dpi,
                Font = Font,
                BackColor = Background,
                ForeColor = TextPrimary,
                ClientSize = new Size(560, 380),
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.CenterParent
            })
            {
                about.SuspendLayout();
                UseDarkTitleBar(about);
                var title = new Label { Text = "Intune Package Grabber", Font = new Font(DisplaySemibold, 13f), AutoSize = true, Location = new Point(20, 16) };
                var version = new Label { Text = "Version " + Assembly.GetExecutingAssembly().GetName().Version.ToString(3) + "  •  open source, MIT License", ForeColor = TextMuted, AutoSize = true, Location = new Point(22, 46) };
                var description = new Label { Text = "Captures the Win32 packages installed from Company Portal and shows the state of the Win32 apps and remediations of this device, for technicians and Intune engineers.", ForeColor = TextMuted, AutoSize = false, Size = new Size(520, 40), Location = new Point(22, 72) };
                var credits = new Label { Text = "Credits", Font = new Font(UiSemibold, 10f), AutoSize = true, Location = new Point(20, 126) };
                var ok = MakeButton("OK", Accent, Color.Black);
                ok.Location = new Point(452, 330);
                ok.Size = new Size(88, 32);
                ok.DialogResult = DialogResult.OK;
                var downloader = MakeButton(FindDownloader() != null ? DownloaderOpenText : DownloaderDownloadText, Accent, Color.Black);
                downloader.Location = new Point(20, 206);
                downloader.Size = new Size(260, 32);
                downloader.Font = new Font(UiSemibold, 9.5f);
                downloader.Click += OnIntuneWinDownloader;
                new ToolTip().SetToolTip(downloader, "Downloads and decrypts the Win32 apps assigned to this device directly from Intune,\r\nwithout installing them. Downloaded from GitHub on first use (about 85 MB).");
                about.Controls.AddRange(new Control[]
                {
                    title, version, description, credits,
                    new Label { Text = "IntuneWin Downloader  •  created by Rudy Ooms (call4cloud), MIT License", AutoSize = true, Location = new Point(20, 156) },
                    MakeLink(DownloaderRepo, new Point(20, 178)),
                    downloader,
                    new Label { Text = "IntuneWinAppUtil (Microsoft Win32 Content Prep Tool)  •  Microsoft Corporation", AutoSize = true, Location = new Point(20, 260) },
                    MakeLink("https://github.com/microsoft/Microsoft-Win32-Content-Prep-Tool", new Point(20, 282)),
                    ok
                });
                about.AcceptButton = about.CancelButton = ok;
                about.ResumeLayout(false);
                about.ShowDialog(this);
            }
        }

        static LinkLabel MakeLink(string url, Point location)
        {
            var link = new LinkLabel { Text = url, AutoSize = true, Location = location, LinkColor = Accent, VisitedLinkColor = Accent, ActiveLinkColor = TextPrimary, LinkBehavior = LinkBehavior.HoverUnderline };
            link.LinkClicked += delegate { Process.Start(url); };
            return link;
        }

        void SetListening(bool value)
        {
            listening = value;
            btnListen.Enabled = !value;
            btnStop.Enabled = value;
            // Locked instead of disabled: disabled text would not be readable on the dark theme
            txtDest.ReadOnly = value;
            txtDest.ForeColor = value ? TextMuted : TextPrimary;
            btnBrowse.Enabled = !value;
            chkIntunewin.AutoCheck = !value;
            chkIntunewin.ForeColor = value ? IdleColor : TextMuted;
            dotColor = value ? GreenColor : IdleColor;
            lblStatus.Text = value ? "Listening  •  " + cacheRoot : "Stopped";
            lblStatus.ForeColor = value ? GreenColor : TextMuted;
            dot.Invalidate();
            UpdateCaptureTitle();
        }

        void Log(string message)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string>(Log), message); return; }
            string line = string.Format("[{0:HH:mm:ss}] {1}\r\n", DateTime.Now, message);
            txtLog.AppendText(line);
            try { File.AppendAllText(logFile, DateTime.Now.ToString("yyyy-MM-dd ") + line); } catch { }
        }

        void UpdateItem(ListViewItem item, int column, string text, Color? color = null)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => UpdateItem(item, column, text, color))); return; }
            item.SubItems[column].Text = text;
            if (color.HasValue) item.SubItems[column].ForeColor = color.Value;
        }

        #endregion

        #region Events

        void OnBrowse(object sender, EventArgs e)
        {
            using (var dialog = new FolderBrowserDialog { Description = "Choose where to archive the packages", ShowNewFolderButton = true })
            {
                if (Directory.Exists(txtDest.Text)) dialog.SelectedPath = txtDest.Text;
                if (dialog.ShowDialog(this) == DialogResult.OK) txtDest.Text = dialog.SelectedPath;
            }
        }

        void UpdateDownloadButton()
        {
            // The button is needed only in the Capture tab, when the .intunewin is wanted and IntuneWinAppUtil is not available
            btnDownload.Visible = (tabs == null || tabs.SelectedIndex == 0) && chkIntunewin.Checked && !HasIntuneWinAppUtil();
        }

        // Shows only the controls that are useful in the selected tab
        void UpdateContext()
        {
            bool capture = tabs.SelectedIndex == 0;
            chkIntunewin.Visible = capture;
            // Without the .intunewin row the settings card is one row shorter
            settingsWrap.Height = (int)((capture ? 120 : 88) * AutoScaleFactor.Height);
            UpdateDownloadButton();
            if (capture) newCaptures = 0;
            UpdateCaptureTitle();
            // Loaded on first view: reading the IME logs can take a few seconds
            if (tabs.SelectedIndex == 1 && !appsLoaded) RefreshApps();
            if (tabs.SelectedIndex == 2 && !remediationsLoaded) RefreshRemediations();
        }

        // From the other tabs the Capture title tells that Listen is active and how many packages arrived
        void UpdateCaptureTitle()
        {
            string title = "Capture";
            if (listening) title += "  •  listening";
            if (newCaptures > 0) title += "  (" + newCaptures + " new)";
            tabs.SetTitle(0, title);
        }

        async void OnDownloadIntuneWinAppUtil(object sender, EventArgs e)
        {
            btnDownload.Enabled = false;
            btnDownload.Text = "Downloading...";
            string temp = Path.Combine(Path.GetTempPath(), "IntunePackageGrabber", Guid.NewGuid().ToString("N") + ".exe");
            try
            {
                Log("Downloading IntuneWinAppUtil.exe from " + IntuneWinAppUtilUrl);
                Directory.CreateDirectory(Path.GetDirectoryName(temp));
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                using (var client = new WebClient()) await client.DownloadFileTaskAsync(IntuneWinAppUtilUrl, temp);

                // Accept only a file with a valid Microsoft Authenticode signature
                if (!Authenticode.IsValid(temp)) throw new Exception("invalid digital signature");
                string subject = X509Certificate.CreateFromSignedFile(temp).Subject;
                if (subject.IndexOf("O=Microsoft Corporation", StringComparison.OrdinalIgnoreCase) < 0) throw new Exception("file not signed by Microsoft (" + subject + ")");

                Directory.CreateDirectory(appDataDir);
                string target = Path.Combine(appDataDir, "IntuneWinAppUtil.exe");
                if (File.Exists(target)) File.Delete(target);
                File.Move(temp, target);
                Log("IntuneWinAppUtil.exe downloaded and verified (Microsoft signature): " + target);
            }
            catch (Exception ex)
            {
                Log("ERROR downloading IntuneWinAppUtil.exe: " + ex.Message);
                MessageBox.Show(this, "IntuneWinAppUtil.exe download failed:\r\n" + ex.Message + "\r\n\r\nAlternatively, copy it next to IntunePackageGrabber.exe.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                btnDownload.Text = "↓  Download IntuneWinAppUtil";
                btnDownload.Enabled = true;
                UpdateDownloadButton();
            }
        }

        // Creates the archive folder (also used by the exports and the log collection) and remembers it
        bool PrepareDest()
        {
            if (string.IsNullOrWhiteSpace(txtDest.Text))
            {
                MessageBox.Show(this, "Choose an archive folder.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            try
            {
                Directory.CreateDirectory(txtDest.Text);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Unable to create the folder:\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
            SaveSettings(txtDest.Text);
            return true;
        }

        void StartListening()
        {
            if (!PrepareDest()) return;
            if (!Directory.Exists(cacheRoot)) Log(cacheRoot + " does not exist yet: it will be watched as soon as IME creates it.");

            // Folders already present are treated as new (they usually belong to an installation in progress),
            // except those already captured in this session: they leave 'handled' only when IME removes them
            lock (pending) pending.Clear();
            timer.Start();
            SetListening(true);
            Log("Listen started. Now start the installation from Company Portal.");
        }

        void StopListening()
        {
            timer.Stop();
            lock (pending) pending.Clear();
            SetListening(false);
            Log("Stop.");
            if (activeJobs > 0) Log("Warning: " + activeJobs + " capture(s) still running in the background.");
        }

        void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (activeJobs > 0 && MessageBox.Show(this, "Captures are still running. Close anyway?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.No)
            {
                e.Cancel = true;
                return;
            }
            timer.Stop();
        }

        // The scan runs in the background so large packages do not freeze the UI
        async void OnTick(object sender, EventArgs e)
        {
            if (scanning) return;
            scanning = true;
            try
            {
                var ready = await Task.Run(() => Scan());
                foreach (var dir in ready)
                {
                    if (listening) StartCapture(dir);
                    else lock (pending) handled.Remove(dir);
                }
            }
            catch (Exception ex)
            {
                Log("Unable to read " + cacheRoot + ": " + ex.Message);
                if (listening) StopListening();
            }
            finally
            {
                scanning = false;
            }
        }

        // Returns the folders ready to be captured
        List<string> Scan()
        {
            var ready = new List<string>();
            string[] dirs = Directory.Exists(cacheRoot) ? Directory.GetDirectories(cacheRoot) : new string[0];
            lock (pending)
            {
                // A captured folder that IME removed can be captured again if it reappears (retry/reinstall)
                handled.RemoveWhere(d => !Directory.Exists(d));

                // App folders only (<GUID>_<version>), excludes HealthScripts etc.
                foreach (var dir in dirs)
                {
                    if (!Regex.IsMatch(Path.GetFileName(dir), @"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")) continue;
                    if (handled.Contains(dir) || pending.ContainsKey(dir)) continue;
                    pending[dir] = new PendingFolder { Snapshot = null, Since = DateTime.Now };
                    Log("Package detected: " + Path.GetFileName(dir) + ", waiting for extraction to finish...");
                }

                // Capture once the content stays unchanged for StableSeconds
                foreach (var dir in pending.Keys.ToList())
                {
                    var entry = pending[dir];
                    if (!Directory.Exists(dir))
                    {
                        Log("Folder removed by IME before the copy: " + Path.GetFileName(dir));
                        pending.Remove(dir);
                        continue;
                    }
                    string snapshot = GetSnapshot(dir);
                    if (snapshot != entry.Snapshot)
                    {
                        entry.Snapshot = snapshot;
                        entry.Since = DateTime.Now;
                        continue;
                    }
                    if (snapshot.Length > 0 && (DateTime.Now - entry.Since).TotalSeconds >= StableSeconds)
                    {
                        // Unchanged sizes are not enough: NTFS can report stale sizes for a file still being written
                        if (!AllFilesReadable(dir))
                        {
                            entry.Since = DateTime.Now;
                            continue;
                        }
                        pending.Remove(dir);
                        handled.Add(dir);
                        ready.Add(dir);
                    }
                }
            }
            return ready;
        }

        #endregion

        #region Capture

        void StartCapture(string source)
        {
            string folderName = Path.GetFileName(source);
            string appId = folderName.Split('_')[0];
            string version = folderName.Contains("_") ? folderName.Substring(folderName.IndexOf('_') + 1) : "";
            var item = new ListViewItem(new[] { DateTime.Now.ToString("HH:mm:ss"), appId, version, "Copying...", "", "" });
            item.UseItemStyleForSubItems = false;
            item.SubItems[ColStatus].ForeColor = Accent;
            list.Items.Insert(0, item);
            if (tabs.SelectedIndex != 0)
            {
                newCaptures++;
                UpdateCaptureTitle();
            }

            string destRoot = txtDest.Text;
            bool makeIntunewin = chkIntunewin.Checked;
            Interlocked.Increment(ref activeJobs);
            Task.Run(() => CapturePackage(source, appId, version, destRoot, makeIntunewin, item));
        }

        static readonly object destLock = new object();

        // Unique destination folder, also for parallel captures of the same app
        static string CreateUniqueDest(string destRoot, string baseName)
        {
            lock (destLock)
            {
                string dest = Path.Combine(destRoot, baseName);
                for (int n = 2; Directory.Exists(LongPath(dest)); n++) dest = Path.Combine(destRoot, baseName + " (" + n + ")");
                Directory.CreateDirectory(LongPath(dest));
                return dest;
            }
        }

        void CapturePackage(string source, string appId, string version, string destRoot, bool makeIntunewin, ListViewItem item)
        {
            // Copy to a short temporary path first: IntuneWinAppUtil does not handle paths over 260 characters
            string staging = Path.Combine(Path.GetTempPath(), "IPG", Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                AppPolicy policy = FindPolicy(appId);
                string label = policy != null && !string.IsNullOrEmpty(policy.Name) ? SafeName(policy.Name) : appId;
                UpdateItem(item, ColApp, policy != null ? policy.Name : appId + " (name not found in IME logs)");

                string baseName = label + (version.Length > 0 ? "_v" + version : "") + "_" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                string dest = CreateUniqueDest(destRoot, baseName);
                UpdateItem(item, ColPath, dest);
                Log("Capturing " + label + " to " + dest);

                // Copy and verify the content
                string stagedContent = Path.Combine(staging, "Content");
                long sourceBytes; int sourceFiles;
                Measure(source, out sourceFiles, out sourceBytes);
                CopyDirectory(source, stagedContent);
                long copiedBytes; int copiedFiles;
                Measure(stagedContent, out copiedFiles, out copiedBytes);
                if (copiedFiles != sourceFiles || copiedBytes != sourceBytes)
                    throw new Exception(string.Format("incomplete content: {0} files / {1} bytes copied of {2} / {3}", copiedFiles, copiedBytes, sourceFiles, sourceBytes));
                UpdateItem(item, ColSize, FormatSize(copiedBytes));

                string setup = null, intunewin = null, intunewinError = null;
                if (makeIntunewin)
                {
                    UpdateItem(item, ColStatus, "Creating .intunewin...");
                    setup = policy != null && !string.IsNullOrEmpty(policy.SetupFile) ? policy.SetupFile : GuessSetupFile(stagedContent);
                    if (setup != null)
                    {
                        // A failed .intunewin must not lose the content: it is saved anyway
                        try
                        {
                            intunewin = BuildIntunewin(stagedContent, setup, staging, label);
                            VerifyIntunewin(intunewin, setup);
                        }
                        catch (Exception ex)
                        {
                            intunewin = null;
                            intunewinError = ex.Message;
                        }
                    }
                }

                // Move everything to the final destination and verify again
                string content = Path.Combine(dest, "Content");
                MoveDirectory(stagedContent, content);
                Measure(content, out copiedFiles, out copiedBytes);
                if (copiedFiles != sourceFiles || copiedBytes != sourceBytes)
                    throw new Exception(string.Format("incomplete content at destination: {0} files / {1} bytes of {2} / {3}", copiedFiles, copiedBytes, sourceFiles, sourceBytes));
                Log(string.Format("Content copied and verified: {0} files, {1}", copiedFiles, FormatSize(copiedBytes)));
                WriteInfo(dest, policy, appId, source, copiedFiles, copiedBytes);

                if (intunewin != null)
                {
                    string finalIntunewin = Path.Combine(dest, Path.GetFileName(intunewin));
                    File.Move(LongPath(intunewin), LongPath(finalIntunewin));
                    Log(".intunewin created and verified: " + finalIntunewin + " (setup: " + setup + ")");
                    UpdateItem(item, ColStatus, "\u2714 Content + .intunewin", GreenColor);
                }
                else if (intunewinError != null)
                {
                    Log("WARNING (" + appId + "): .intunewin not created (" + intunewinError.Trim() + "). The content has been saved.");
                    UpdateItem(item, ColStatus, "⚠ Content only (.intunewin failed)", OrangeColor);
                }
                else if (makeIntunewin)
                {
                    Log("WARNING (" + appId + "): setup file not determined, .intunewin not created. The content has been saved.");
                    UpdateItem(item, ColStatus, "\u26A0 Content only (unknown setup)", OrangeColor);
                }
                else
                {
                    UpdateItem(item, ColStatus, "\u2714 Content", GreenColor);
                }
            }
            catch (Exception ex)
            {
                UpdateItem(item, ColStatus, "\u2716 Error", RedColor);
                Log("ERROR (" + appId + "): " + ex.Message);
            }
            finally
            {
                try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
                Interlocked.Decrement(ref activeJobs);
            }
        }

        // \\?\ prefix to bypass the 260-character limit without relying on LongPathsEnabled
        static string LongPath(string path)
        {
            if (path.StartsWith(@"\\?\")) return path;
            path = Path.GetFullPath(path);
            return path.StartsWith(@"\\") ? @"\\?\UNC\" + path.Substring(2) : @"\\?\" + path;
        }

        static void CopyDirectory(string source, string target)
        {
            source = LongPath(source);
            target = LongPath(target);
            Directory.CreateDirectory(target);
            foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(target + dir.Substring(source.Length));
            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
                File.Copy(file, target + file.Substring(source.Length), true);
        }

        static void MoveDirectory(string source, string target)
        {
            if (string.Equals(Path.GetPathRoot(Path.GetFullPath(source)), Path.GetPathRoot(Path.GetFullPath(target)), StringComparison.OrdinalIgnoreCase))
            {
                Directory.Move(LongPath(source), LongPath(target));
            }
            else
            {
                CopyDirectory(source, target);
                Directory.Delete(LongPath(source), true);
            }
        }

        static void Measure(string path, out int files, out long bytes)
        {
            var all = new DirectoryInfo(LongPath(path)).GetFiles("*", SearchOption.AllDirectories);
            files = all.Length;
            bytes = all.Sum(f => f.Length);
        }

        // False if a file is still open for writing (FileShare.Read denies the open while a writer exists)
        static bool AllFilesReadable(string path)
        {
            try
            {
                foreach (var f in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                    using (new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete)) { }
                return true;
            }
            catch
            {
                return false;
            }
        }

        static string GetSnapshot(string path)
        {
            try
            {
                var sb = new StringBuilder();
                foreach (var f in new DirectoryInfo(path).GetFiles("*", SearchOption.AllDirectories))
                    sb.Append(f.FullName).Append('|').Append(f.Length).Append('|').Append(f.LastWriteTimeUtc.Ticks).Append(';');
                return sb.ToString();
            }
            catch
            {
                return "";
            }
        }

        static string GuessSetupFile(string content)
        {
            foreach (var name in new[] { "install.ps1", "Deploy-Application.exe", "Invoke-AppDeployToolkit.exe", "setup.exe", "install.cmd", "install.bat" })
                if (File.Exists(Path.Combine(content, name))) return name;
            var candidate = Directory.GetFiles(content, "*.msi").Concat(Directory.GetFiles(content, "*.exe")).FirstOrDefault();
            return candidate != null ? Path.GetFileName(candidate) : null;
        }

        #endregion

        #region .intunewin

        static string BesideExe(string name)
        {
            return Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), name);
        }

        const string IntuneWinAppUtilUrl = "https://raw.githubusercontent.com/microsoft/Microsoft-Win32-Content-Prep-Tool/master/IntuneWinAppUtil.exe";

        static string DownloadedTool()
        {
            return Path.Combine(appDataDir, "IntuneWinAppUtil.exe");
        }

        static bool HasIntuneWinAppUtil()
        {
            return File.Exists(BesideExe("IntuneWinAppUtil.exe")) || File.Exists(DownloadedTool()) || Assembly.GetExecutingAssembly().GetManifestResourceInfo("IntuneWinAppUtil.exe") != null;
        }

        static string GetIntuneWinAppUtilPath()
        {
            // 1) next to the exe, 2) downloaded to %APPDATA%, 3) embedded as a resource and extracted to %TEMP%
            string beside = BesideExe("IntuneWinAppUtil.exe");
            if (File.Exists(beside)) return beside;
            if (File.Exists(DownloadedTool())) return DownloadedTool();

            string temp = Path.Combine(Path.GetTempPath(), "IntunePackageGrabber", "IntuneWinAppUtil.exe");
            using (var res = Assembly.GetExecutingAssembly().GetManifestResourceStream("IntuneWinAppUtil.exe"))
            {
                if (res == null) return beside;
                if (!File.Exists(temp) || new FileInfo(temp).Length != res.Length)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(temp));
                    using (var fs = File.Create(temp)) res.CopyTo(fs);
                }
            }
            return temp;
        }

        string BuildIntunewin(string content, string setupFile, string dest, string label)
        {
            string tool = GetIntuneWinAppUtilPath();
            if (!File.Exists(tool)) throw new Exception("IntuneWinAppUtil.exe not available");

            string buildDir = Path.Combine(dest, "_build");
            Directory.CreateDirectory(buildDir);
            var psi = new ProcessStartInfo(tool, string.Format("-c \"{0}\" -s \"{1}\" -o \"{2}\" -q", content, Path.Combine(content, setupFile), buildDir))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            string output;
            using (var p = Process.Start(psi))
            {
                var errTask = p.StandardError.ReadToEndAsync();
                output = p.StandardOutput.ReadToEnd() + errTask.Result;
                p.WaitForExit();
            }

            string built = Directory.GetFiles(buildDir, "*.intunewin").FirstOrDefault();
            if (built == null) throw new Exception("IntuneWinAppUtil did not produce the .intunewin file:\r\n" + output.Trim());

            string final = Path.Combine(dest, label + ".intunewin");
            if (File.Exists(final)) File.Delete(final);
            File.Move(built, final);
            Directory.Delete(buildDir, true);
            return final;
        }

        static void VerifyIntunewin(string path, string expectedSetup)
        {
            using (var zip = ZipFile.OpenRead(path))
            {
                var detection = zip.GetEntry("IntuneWinPackage/Metadata/Detection.xml");
                var payload = zip.GetEntry("IntuneWinPackage/Contents/IntunePackage.intunewin");
                if (detection == null || payload == null) throw new Exception("invalid .intunewin: internal structure missing");
                if (payload.Length == 0) throw new Exception("invalid .intunewin: empty content");

                var xml = new XmlDocument();
                using (var s = detection.Open()) xml.Load(s);
                var setupNode = xml.SelectSingleNode("//*[local-name()='SetupFile']");
                if (setupNode == null || !string.Equals(setupNode.InnerText, expectedSetup, StringComparison.OrdinalIgnoreCase))
                    throw new Exception("invalid .intunewin: SetupFile = " + (setupNode == null ? "(missing)" : setupNode.InnerText) + ", expected " + expectedSetup);
            }
        }

        #endregion

        #region IntuneWin Downloader

        // IntuneWin Downloader by Rudy Ooms (call4cloud), MIT License. Its binaries are not signed:
        // the zip is pinned to a commit and accepted only with the expected SHA-256
        const string DownloaderRepo = "https://github.com/call4cloud-code/Intunewin-Downloader-Public";
        const string DownloaderVersion = "0.6.127";
        const string DownloaderUrl = "https://raw.githubusercontent.com/call4cloud-code/Intunewin-Downloader-Public/cec58379cba47cabaf7251d96f2dba635e8f0f5a/0.6.127.zip";
        const string DownloaderSha256 = "F7C05C224769C6B48DF0F2A34B7AB942F042F22170D5BC11B5B7E4C4CE9998E2";
        const string DownloaderExe = "IntuneWinDownloader.WinUI.exe";
        const string DownloaderOpenText = "Open IntuneWin Downloader";
        const string DownloaderDownloadText = "↓  Download IntuneWin Downloader";

        // %LOCALAPPDATA%: about 200 MB once extracted, must not roam with the profile
        static readonly string downloaderRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IntunePackageGrabber", "IntuneWinDownloader");

        static string FindDownloader()
        {
            // 1) IntuneWinDownloader folder next to the exe, 2) downloaded to %LOCALAPPDATA%
            foreach (var path in new[] { BesideExe(Path.Combine("IntuneWinDownloader", DownloaderExe)), Path.Combine(downloaderRoot, DownloaderVersion, DownloaderExe) })
                if (File.Exists(path)) return path;
            return null;
        }

        // Button in About & credits: the dialog can be closed while the download is still running
        async void OnIntuneWinDownloader(object sender, EventArgs e)
        {
            var button = (Button)sender;
            string exe = FindDownloader();
            if (exe == null)
            {
                if (MessageBox.Show(button.FindForm(), "IntuneWin Downloader " + DownloaderVersion + ", created by Rudy Ooms (call4cloud), downloads and decrypts the Win32 apps assigned to this device directly from Intune, without installing them.\r\n\r\nDownload it now from GitHub (about 85 MB)?\r\n" + DownloaderRepo, Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                    return;

                button.Enabled = false;
                string temp = Path.Combine(Path.GetTempPath(), "IntunePackageGrabber", Guid.NewGuid().ToString("N") + ".zip");
                string staging = Path.Combine(downloaderRoot, "_" + Guid.NewGuid().ToString("N").Substring(0, 8));
                try
                {
                    Log("Downloading IntuneWin Downloader " + DownloaderVersion + " from " + DownloaderUrl);
                    Directory.CreateDirectory(Path.GetDirectoryName(temp));
                    ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                    using (var client = new WebClient())
                    {
                        client.DownloadProgressChanged += (s, a) => { if (!button.IsDisposed) button.Text = "Downloading " + a.ProgressPercentage + "%"; };
                        await client.DownloadFileTaskAsync(DownloaderUrl, temp);
                    }

                    if (!button.IsDisposed) button.Text = "Extracting...";
                    await Task.Run(() =>
                    {
                        string hash;
                        using (var sha = SHA256.Create())
                        using (var fs = File.OpenRead(temp)) hash = BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "");
                        if (!string.Equals(hash, DownloaderSha256, StringComparison.OrdinalIgnoreCase)) throw new Exception("unexpected SHA-256 " + hash);

                        // Extracted to a staging folder first: an interrupted extraction never leaves a partial version
                        ZipFile.ExtractToDirectory(temp, staging);
                        string target = Path.Combine(downloaderRoot, DownloaderVersion);
                        if (Directory.Exists(target)) Directory.Delete(target, true);
                        Directory.Move(Path.Combine(staging, DownloaderVersion), target);
                    });
                    exe = FindDownloader();
                    if (exe == null) throw new Exception(DownloaderExe + " not found in the package");
                    Log("IntuneWin Downloader downloaded and verified (SHA-256): " + Path.GetDirectoryName(exe));
                }
                catch (Exception ex)
                {
                    exe = null;
                    Log("ERROR downloading IntuneWin Downloader: " + ex.Message);
                    MessageBox.Show(button.IsDisposed ? this : button.FindForm(), "IntuneWin Downloader download failed:\r\n" + ex.Message + "\r\n\r\nAlternatively, extract it from " + DownloaderRepo + " into an IntuneWinDownloader folder next to IntunePackageGrabber.exe.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                finally
                {
                    try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                    try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
                    if (!button.IsDisposed)
                    {
                        button.Text = exe != null ? DownloaderOpenText : DownloaderDownloadText;
                        button.Enabled = true;
                    }
                }
                if (exe == null) return;
            }

            try
            {
                // Inherits the elevation of the Grabber (IntuneWin Downloader must run as administrator)
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) });
                Log("IntuneWin Downloader started: " + exe);
            }
            catch (Exception ex)
            {
                Log("ERROR starting IntuneWin Downloader: " + ex.Message);
            }
        }

        #endregion

        #region Policy from IME logs

        static AppPolicy FindPolicy(string appId)
        {
            foreach (var d in LoggedPolicies(appId))
                if (string.Equals(Str(d, "Id"), appId, StringComparison.OrdinalIgnoreCase)) return ToPolicy(d);
            return null;
        }

        // All the app policies in the IME logs, the newest version of each
        static Dictionary<string, AppPolicy> LoadLoggedPolicies()
        {
            var result = new Dictionary<string, AppPolicy>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in LoggedPolicies(null))
            {
                string id = Str(d, "Id");
                if (id != null && !result.ContainsKey(id)) result[id] = ToPolicy(d);
            }
            return result;
        }

        // App policies of the "Get policies" lines in AppWorkload*.log, from the newest to the oldest.
        // With 'filter', only the lines that contain it are deserialized.
        static IEnumerable<Dictionary<string, object>> LoggedPolicies(string filter)
        {
            if (!Directory.Exists(ImeLogFolder)) yield break;
            var logs = new DirectoryInfo(ImeLogFolder).GetFiles("AppWorkload*.log").OrderByDescending(f => f.LastWriteTimeUtc);
            const string marker = "Get policies = ";

            foreach (var log in logs)
            {
                string text = ReadShared(log.FullName);
                if (text == null) continue;

                int idx = text.LastIndexOf(marker, StringComparison.Ordinal);
                while (idx >= 0)
                {
                    int start = idx + marker.Length;
                    int end = text.IndexOf("]LOG]!>", start, StringComparison.Ordinal);
                    if (end > start)
                    {
                        string json = text.Substring(start, end - start);
                        if (filter == null || json.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                            foreach (var d in DeserializeArray(json)) yield return d;
                    }
                    idx = idx > 0 ? text.LastIndexOf(marker, idx - 1, StringComparison.Ordinal) : -1;
                }
            }
        }

        // Reads a log that IME keeps open for writing
        static string ReadShared(string path)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var sr = new StreamReader(fs)) return sr.ReadToEnd();
            }
            catch
            {
                return null;
            }
        }

        static List<Dictionary<string, object>> DeserializeArray(string json)
        {
            try
            {
                var arr = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 256 }.DeserializeObject(json) as object[];
                if (arr != null) return arr.OfType<Dictionary<string, object>>().ToList();
            }
            catch
            {
            }
            return new List<Dictionary<string, object>>();
        }

        static AppPolicy ToPolicy(Dictionary<string, object> d)
        {
            return new AppPolicy
            {
                Id = Str(d, "Id"),
                Name = Str(d, "Name"),
                Version = Str(d, "Version"),
                Intent = Str(d, "Intent"),
                SetupFile = Str(d, "SetUpFilePath"),
                InstallCommand = Str(d, "InstallCommandLine"),
                UninstallCommand = Str(d, "UninstallCommandLine"),
                DetectionRule = Str(d, "DetectionRule"),
                RequirementRules = Str(d, "RequirementRules"),
                ReturnCodes = Str(d, "ReturnCodes"),
                RawJson = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 256 }.Serialize(d)
            };
        }

        static string Str(Dictionary<string, object> d, string key)
        {
            object v;
            return d.TryGetValue(key, out v) && v != null ? Convert.ToString(v) : null;
        }

        static int Int(Dictionary<string, object> d, string key)
        {
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return 0;
            if (v is bool) return (bool)v ? 1 : 0;
            int n;
            return int.TryParse(Convert.ToString(v), out n) ? n : 0;
        }

        // Enum values as in Microsoft Graph (win32LobAppRuleOperator, ...RegistryRuleOperationType, ...FileSystemOperationType)
        static string Operator(int op)
        {
            switch (op)
            {
                case 1: return "=";
                case 2: return "!=";
                case 4: return ">";
                case 5: return ">=";
                case 8: return "<";
                case 9: return "<=";
                default: return "";
            }
        }

        static readonly string[] RegistryChecks = { "", "exists", "does not exist", "string", "integer", "version" };
        static readonly string[] FileChecks = { "", "exists", "modified date", "created date", "version", "size in MB", "does not exist" };

        static string Check(string[] names, Dictionary<string, object> t)
        {
            int type = Int(t, "DetectionType");
            string name = type < names.Length ? names[type] : "type " + type;
            if (name == "exists" || name == "does not exist" || name == "") return name;
            return name + " " + Operator(Int(t, "Operator")) + " " + Str(t, "DetectionValue");
        }

        // Readable detection rules; a script rule is returned decoded in 'script'
        static List<string> DescribeDetection(string json, out byte[] script)
        {
            script = null;
            var result = new List<string>();
            if (string.IsNullOrEmpty(json)) return result;
            try
            {
                var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                foreach (var r in ((object[])serializer.DeserializeObject(json)).OfType<Dictionary<string, object>>())
                {
                    var t = serializer.DeserializeObject(Str(r, "DetectionText") ?? "{}") as Dictionary<string, object> ?? new Dictionary<string, object>();
                    string bits = Int(t, "Check32BitOn64System") == 1 ? " (32-bit on 64-bit)" : "";
                    switch (Int(r, "DetectionType"))
                    {
                        case 0:
                            string value = string.IsNullOrEmpty(Str(t, "KeyName")) ? "" : ", value " + Str(t, "KeyName");
                            result.Add("Registry " + Str(t, "Path") + value + " " + Check(RegistryChecks, t) + bits);
                            break;
                        case 1:
                            string version = Int(t, "ProductVersionOperator") > 0 ? " version " + Operator(Int(t, "ProductVersionOperator")) + " " + Str(t, "ProductVersion") : "";
                            result.Add("MSI " + Str(t, "ProductCode") + version);
                            break;
                        case 2:
                            result.Add("File " + Path.Combine(Str(t, "Path") ?? "", Str(t, "FileOrFolderName") ?? "") + " " + Check(FileChecks, t) + bits);
                            break;
                        case 3:
                            script = Convert.FromBase64String(Str(t, "ScriptBody") ?? "");
                            result.Add("Script, saved as detection.ps1" + (Int(t, "RunAs32Bit") == 1 ? " (32-bit)" : ""));
                            break;
                        default:
                            result.Add(Str(r, "DetectionText"));
                            break;
                    }
                }
            }
            catch
            {
                result.Add("(not decoded, see raw rule)");
            }
            return result;
        }

        static readonly string[] ReturnCodeTypes = { "failed", "success", "soft reboot", "hard reboot", "retry" };

        static string DescribeReturnCodes(string json)
        {
            if (string.IsNullOrEmpty(json)) return "";
            try
            {
                var codes = ((object[])new JavaScriptSerializer().DeserializeObject(json)).OfType<Dictionary<string, object>>();
                return string.Join(", ", codes.Select(c =>
                {
                    int type = Int(c, "Type");
                    return Str(c, "ReturnCode") + " " + (type < ReturnCodeTypes.Length ? ReturnCodeTypes[type] : "type " + type);
                }));
            }
            catch
            {
                return json;
            }
        }

        static void WriteInfo(string dest, AppPolicy policy, string appId, string source, int files, long bytes)
        {
            var sb = new StringBuilder();
            sb.AppendLine("App name            : " + (policy != null ? policy.Name : "(not found in IME logs)"));
            sb.AppendLine("App ID              : " + appId);
            sb.AppendLine("Content version     : " + (policy != null ? policy.Version : ""));
            sb.AppendLine("Source              : " + source);
            sb.AppendLine("Captured            : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " on " + Environment.MachineName);
            sb.AppendLine("Content             : " + files + " files, " + FormatSize(bytes));
            if (policy != null) AppendPolicyInfo(sb, policy, dest);
            File.WriteAllText(LongPath(Path.Combine(dest, "app-info.txt")), sb.ToString(), Encoding.UTF8);
        }

        // Commands, detection rules, return codes and requirements; with 'dest' also saves detection.ps1 and policy.json
        static void AppendPolicyInfo(StringBuilder sb, AppPolicy policy, string dest)
        {
            sb.AppendLine();
            sb.AppendLine("Setup file          : " + policy.SetupFile);
            sb.AppendLine("Install command     : " + policy.InstallCommand);
            sb.AppendLine("Uninstall command   : " + policy.UninstallCommand);
            sb.AppendLine();
            byte[] script;
            foreach (var rule in DescribeDetection(policy.DetectionRule, out script))
                sb.AppendLine("Detection rule      : " + rule);
            if (script != null && dest != null) File.WriteAllBytes(LongPath(Path.Combine(dest, "detection.ps1")), script);
            sb.AppendLine("Return codes        : " + DescribeReturnCodes(policy.ReturnCodes));
            sb.AppendLine("Requirements        : " + policy.RequirementRules);
            sb.AppendLine();
            sb.AppendLine("Raw detection rule  : " + policy.DetectionRule);
            if (dest != null) File.WriteAllText(LongPath(Path.Combine(dest, "policy.json")), policy.RawJson, Encoding.UTF8);
        }

        #endregion

        #region Utilities

        static string SafeName(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name.Trim();
        }

        static string FormatSize(long bytes)
        {
            var c = System.Globalization.CultureInfo.InvariantCulture;
            if (bytes >= 1L << 30) return (bytes / (double)(1L << 30)).ToString("N2", c) + " GB";
            if (bytes >= 1L << 20) return (bytes / (double)(1L << 20)).ToString("N1", c) + " MB";
            return (bytes / 1024.0).ToString("N0", c) + " KB";
        }

        static void OpenFolder(string path)
        {
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path)) Process.Start("explorer.exe", "\"" + path + "\"");
        }

        string LoadSettings()
        {
            try { return File.Exists(settingsFile) ? File.ReadAllText(settingsFile).Trim() : null; }
            catch { return null; }
        }

        void SaveSettings(string dest)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(settingsFile));
                File.WriteAllText(settingsFile, dest);
            }
            catch { }
        }

        #endregion
    }
}

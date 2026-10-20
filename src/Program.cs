// Intune Package Grabber
// Captures the Win32 packages that Intune extracts to C:\Windows\IMECache during an installation
// (e.g. from Company Portal), saves their content and rebuilds the .intunewin file with IntuneWinAppUtil.
//
// Command line options (for testing): --cache <folder> --dest <folder> --log <file> --imelogs <folder> --listen --exit-after <seconds>

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
[assembly: AssemblyVersion("2.0.0.0")]
[assembly: AssemblyFileVersion("2.0.0.0")]
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
        public string SetupFile;
        public string InstallCommand;
        public string UninstallCommand;
        public string DetectionRule;
        public string RequirementRules;
        public string ReturnCodes;
        public string RawJson;
    }

    class MainForm : Form
    {
        static string ImeLogFolder = @"C:\ProgramData\Microsoft\IntuneManagementExtension\Logs";
        const int StableSeconds = 3;

        // List columns
        const int ColTime = 0, ColApp = 1, ColVersion = 2, ColStatus = 3, ColSize = 4, ColPath = 5;

        static readonly Color Accent = Color.FromArgb(15, 108, 189);
        static readonly Color AccentDark = Color.FromArgb(17, 94, 163);
        static readonly Color GreenColor = Color.FromArgb(16, 124, 16);
        static readonly Color RedColor = Color.FromArgb(196, 43, 28);
        static readonly Color OrangeColor = Color.FromArgb(202, 80, 16);
        static readonly Color Surface = Color.FromArgb(245, 246, 248);
        static readonly Color Border = Color.FromArgb(222, 225, 230);
        static readonly Color TextMuted = Color.FromArgb(96, 96, 96);
        static readonly Color DisabledBack = Color.FromArgb(225, 227, 230);

        readonly string cacheRoot = @"C:\Windows\IMECache";
        static readonly string appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IntunePackageGrabber");
        readonly string settingsFile = Path.Combine(appDataDir, "settings.txt");
        string logFile = Path.Combine(appDataDir, "grabber.log");
        readonly Dictionary<string, PendingFolder> pending = new Dictionary<string, PendingFolder>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> handled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        int activeJobs;
        bool listening;
        Color dotColor = Color.Gray;

        TextBox txtDest;
        TextBox txtLog;
        Button btnBrowse, btnListen, btnStop, btnOpen, btnDownload;
        CheckBox chkIntunewin;
        Label lblStatus;
        Panel dot;
        ListView list;

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
            Font = new Font("Segoe UI", 9.5f);
            BackColor = Surface;
            ClientSize = new Size(960, 640);
            MinimumSize = new Size(760, 520);
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            // Header
            var header = new Panel { Dock = DockStyle.Top, Height = 72, BackColor = Accent };
            header.Paint += (s, e) =>
            {
                using (var brush = new LinearGradientBrush(header.ClientRectangle, Accent, AccentDark, LinearGradientMode.Horizontal))
                    e.Graphics.FillRectangle(brush, header.ClientRectangle);
            };
            var title = new Label { Text = "Intune Package Grabber", ForeColor = Color.White, BackColor = Color.Transparent, Font = new Font("Segoe UI Semibold", 15f), AutoSize = true, Location = new Point(20, 8) };
            var subtitle = new Label { Text = "Captures content and .intunewin of Win32 packages while they install from Company Portal", ForeColor = Color.FromArgb(220, 235, 250), BackColor = Color.Transparent, AutoSize = true, Location = new Point(22, 46) };
            header.Controls.Add(title);
            header.Controls.Add(subtitle);

            // Settings
            var settings = new Panel { Dock = DockStyle.Top, Height = 104, Padding = new Padding(20, 0, 20, 0) };
            // Initial size = design size, so the anchors keep the margins
            var card = MakeCard();
            card.Size = new Size(920, 104);
            var lblDest = new Label { Text = "Archive folder", Font = new Font("Segoe UI Semibold", 9.5f), AutoSize = true, Location = new Point(14, 10) };
            txtDest = new TextBox { Location = new Point(16, 36), Size = new Size(758, 25), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, Font = new Font("Segoe UI", 10f) };
            btnBrowse = MakeButton("Browse...", Color.White, Color.Black);
            btnBrowse.Location = new Point(784, 34);
            btnBrowse.Size = new Size(120, 30);
            btnBrowse.Font = new Font("Segoe UI", 9.5f);
            btnBrowse.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnBrowse.Click += OnBrowse;
            chkIntunewin = new CheckBox { Text = "Also create the .intunewin file (IntuneWinAppUtil, same setup file as the Intune policy)", Checked = true, AutoSize = true, Location = new Point(16, 72), ForeColor = TextMuted };
            chkIntunewin.CheckedChanged += delegate { UpdateDownloadButton(); };
            btnDownload = MakeButton("↓  Download IntuneWinAppUtil", Accent, Color.White);
            btnDownload.Location = new Point(704, 68);
            btnDownload.Size = new Size(200, 28);
            btnDownload.Font = new Font("Segoe UI Semibold", 9f);
            btnDownload.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnDownload.Visible = false;
            btnDownload.Click += OnDownloadIntuneWinAppUtil;
            card.Controls.AddRange(new Control[] { lblDest, txtDest, btnBrowse, chkIntunewin, btnDownload });
            card.Dock = DockStyle.Fill;
            settings.Controls.Add(card);
            var settingsWrap = new Panel { Dock = DockStyle.Top, Height = 120, Padding = new Padding(0, 16, 0, 0) };
            settingsWrap.Controls.Add(settings);
            settings.Dock = DockStyle.Fill;

            // Actions
            var actions = new Panel { Dock = DockStyle.Top, Size = new Size(960, 64) };
            btnListen = MakeButton("\u25B6  Listen", GreenColor, Color.White);
            btnListen.Location = new Point(20, 14);
            btnListen.Click += delegate { StartListening(); };
            btnStop = MakeButton("\u25A0  Stop", RedColor, Color.White);
            btnStop.Location = new Point(156, 14);
            btnStop.Click += delegate { StopListening(); };
            btnOpen = MakeButton("Open folder", Color.White, Color.Black);
            btnOpen.Location = new Point(292, 14);
            btnOpen.Size = new Size(140, 38);
            btnOpen.Click += delegate { OpenFolder(txtDest.Text); };
            dot = new Panel { Size = new Size(14, 14), Location = new Point(452, 26) };
            dot.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var b = new SolidBrush(dotColor)) e.Graphics.FillEllipse(b, 0, 0, dot.Width - 1, dot.Height - 1);
            };
            lblStatus = new Label
            {
                AutoSize = false,
                AutoEllipsis = true,
                Location = new Point(472, 22),
                Size = new Size(468, 24),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Font = new Font("Segoe UI Semibold", 10f)
            };
            actions.Controls.AddRange(new Control[] { btnListen, btnStop, btnOpen, dot, lblStatus });

            // Package list + log
            list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                BorderStyle = BorderStyle.None,
                Font = new Font("Segoe UI", 9.5f)
            };
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
                BackColor = Color.FromArgb(250, 250, 251),
                Font = new Font("Consolas", 9f)
            };

            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, BackColor = Surface, SplitterWidth = 12 };
            split.Panel1.Controls.Add(WrapInCard(list, "Captured packages  (double-click to open)"));
            split.Panel2.Controls.Add(WrapInCard(txtLog, "Log"));
            var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20, 0, 20, 20) };
            body.Controls.Add(split);
            Shown += delegate { split.SplitterDistance = (int)(split.Height * 0.6); };

            Controls.Add(body);
            Controls.Add(actions);
            Controls.Add(settingsWrap);
            Controls.Add(header);

            FormClosing += OnFormClosing;
            ResumeLayout(false);
            PerformLayout();
        }

        Panel MakeCard()
        {
            var card = new Panel { BackColor = Color.White };
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
            var lbl = new Label { Text = caption, Font = new Font("Segoe UI Semibold", 9.5f), AutoSize = true, Location = new Point(12, 8) };
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
                Font = new Font("Segoe UI Semibold", 10f),
                Cursor = Cursors.Hand,
                Tag = back
            };
            b.FlatAppearance.BorderSize = back == Color.White ? 1 : 0;
            b.FlatAppearance.BorderColor = Border;
            b.EnabledChanged += delegate
            {
                b.BackColor = b.Enabled ? (Color)b.Tag : DisabledBack;
            };
            return b;
        }

        void SetListening(bool value)
        {
            listening = value;
            btnListen.Enabled = !value;
            btnStop.Enabled = value;
            txtDest.Enabled = !value;
            btnBrowse.Enabled = !value;
            chkIntunewin.Enabled = !value;
            dotColor = value ? GreenColor : Color.Gray;
            lblStatus.Text = value ? "Listening  •  " + cacheRoot : "Stopped";
            lblStatus.ForeColor = value ? GreenColor : TextMuted;
            dot.Invalidate();
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
            // The button is needed only when the .intunewin is wanted and IntuneWinAppUtil is not available
            btnDownload.Visible = chkIntunewin.Checked && !HasIntuneWinAppUtil();
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

        void StartListening()
        {
            if (string.IsNullOrWhiteSpace(txtDest.Text))
            {
                MessageBox.Show(this, "Choose an archive folder.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            try
            {
                Directory.CreateDirectory(txtDest.Text);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Unable to create the folder:\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            SaveSettings(txtDest.Text);
            if (!Directory.Exists(cacheRoot)) Log(cacheRoot + " does not exist yet: it will be watched as soon as IME creates it.");

            // Folders already present are treated as new (they usually belong to an installation in progress),
            // except those already captured in this session: they leave 'handled' only when IME removes them
            pending.Clear();
            timer.Start();
            SetListening(true);
            Log("Listen started. Now start the installation from Company Portal.");
        }

        void StopListening()
        {
            timer.Stop();
            pending.Clear();
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

        void OnTick(object sender, EventArgs e)
        {
            string[] dirs;
            try
            {
                dirs = Directory.Exists(cacheRoot) ? Directory.GetDirectories(cacheRoot) : new string[0];
            }
            catch (Exception ex)
            {
                Log("Unable to read " + cacheRoot + ": " + ex.Message);
                StopListening();
                return;
            }

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
                    StartCapture(dir);
                }
            }
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

                string setup = null, intunewin = null;
                if (makeIntunewin)
                {
                    UpdateItem(item, ColStatus, "Creating .intunewin...");
                    setup = policy != null && !string.IsNullOrEmpty(policy.SetupFile) ? policy.SetupFile : GuessSetupFile(stagedContent);
                    if (setup != null)
                    {
                        intunewin = BuildIntunewin(stagedContent, setup, staging, label);
                        VerifyIntunewin(intunewin, setup);
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

        #region Policy from IME logs

        static AppPolicy FindPolicy(string appId)
        {
            if (!Directory.Exists(ImeLogFolder)) return null;
            var logs = new DirectoryInfo(ImeLogFolder).GetFiles("AppWorkload*.log").OrderByDescending(f => f.LastWriteTimeUtc);
            var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 256 };
            const string marker = "Get policies = ";

            foreach (var log in logs)
            {
                string text;
                try
                {
                    using (var fs = new FileStream(log.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var sr = new StreamReader(fs)) text = sr.ReadToEnd();
                }
                catch
                {
                    continue;
                }

                // From the newest policy to the oldest
                int idx = text.LastIndexOf(marker, StringComparison.Ordinal);
                while (idx >= 0)
                {
                    int start = idx + marker.Length;
                    int end = text.IndexOf("]LOG]!>", start, StringComparison.Ordinal);
                    if (end > start)
                    {
                        string json = text.Substring(start, end - start);
                        if (json.IndexOf(appId, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            try
                            {
                                var arr = serializer.DeserializeObject(json) as object[];
                                if (arr != null)
                                {
                                    foreach (var o in arr)
                                    {
                                        var d = o as Dictionary<string, object>;
                                        if (d != null && string.Equals(Str(d, "Id"), appId, StringComparison.OrdinalIgnoreCase))
                                        {
                                            return new AppPolicy
                                            {
                                                Id = appId,
                                                Name = Str(d, "Name"),
                                                Version = Str(d, "Version"),
                                                SetupFile = Str(d, "SetUpFilePath"),
                                                InstallCommand = Str(d, "InstallCommandLine"),
                                                UninstallCommand = Str(d, "UninstallCommandLine"),
                                                DetectionRule = Str(d, "DetectionRule"),
                                                RequirementRules = Str(d, "RequirementRules"),
                                                ReturnCodes = Str(d, "ReturnCodes"),
                                                RawJson = serializer.Serialize(d)
                                            };
                                        }
                                    }
                                }
                            }
                            catch
                            {
                            }
                        }
                    }
                    idx = idx > 0 ? text.LastIndexOf(marker, idx - 1, StringComparison.Ordinal) : -1;
                }
            }
            return null;
        }

        static string Str(Dictionary<string, object> d, string key)
        {
            object v;
            return d.TryGetValue(key, out v) && v != null ? Convert.ToString(v) : null;
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
            if (policy != null)
            {
                sb.AppendLine();
                sb.AppendLine("Setup file          : " + policy.SetupFile);
                sb.AppendLine("Install command     : " + policy.InstallCommand);
                sb.AppendLine("Uninstall command   : " + policy.UninstallCommand);
                sb.AppendLine();
                sb.AppendLine("Detection rule      : " + policy.DetectionRule);
                sb.AppendLine("Requirements        : " + policy.RequirementRules);
                sb.AppendLine("Return codes        : " + policy.ReturnCodes);
                File.WriteAllText(LongPath(Path.Combine(dest, "policy.json")), policy.RawJson, Encoding.UTF8);
            }
            File.WriteAllText(LongPath(Path.Combine(dest, "app-info.txt")), sb.ToString(), Encoding.UTF8);
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

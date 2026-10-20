// Intune Package Grabber
// Cattura i pacchetti Win32 che Intune estrae in C:\Windows\IMECache durante un'installazione
// (es. da Company Portal), ne salva il contenuto e rigenera il file .intunewin con IntuneWinAppUtil.
//
// Opzioni da riga di comando (per test): --cache <cartella> --dest <cartella> --log <file> --imelogs <cartella> --listen --exit-after <secondi>

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
            // Percorsi oltre 260 caratteri (destinazioni lunghe, es. OneDrive): disattiva la gestione legacy dei path di .NET
            AppContext.SetSwitch("Switch.System.IO.UseLegacyPathHandling", false);
            AppContext.SetSwitch("Switch.System.IO.BlockLongPaths", false);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(args));
        }
    }

    // Verifica della firma Authenticode di un file (WinVerifyTrust)
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
                // UI nessuna, file, verifica
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

        // Colonne della lista
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

            // Solo per test automatici: chiude dopo N secondi, attendendo le catture in corso
            if (exitAfter > 0)
            {
                var exitTimer = new System.Windows.Forms.Timer { Interval = exitAfter * 1000 };
                exitTimer.Tick += delegate
                {
                    if (activeJobs > 0) return;
                    exitTimer.Stop();
                    Log("Uscita automatica (--exit-after).");
                    Close();
                };
                exitTimer.Start();
            }

            timer.Interval = 500;
            timer.Tick += OnTick;

            txtDest.Text = argDest ?? LoadSettings() ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "IntunePackages");
            SetListening(false);
            Log("Scegli la cartella di archiviazione e premi Listen prima di avviare l'installazione.");
            if (!HasIntuneWinAppUtil()) Log("IntuneWinAppUtil.exe non presente: premi \"Scarica IntuneWinAppUtil\" per generare anche il .intunewin, altrimenti verra' salvato solo il contenuto.");
            UpdateDownloadButton();
            if (autoListen) Shown += delegate { StartListening(); };
        }

        #region Interfaccia

        void BuildUi()
        {
            // Layout progettato a 96 DPI: WinForms lo scala automaticamente al DPI dello schermo
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

            // Intestazione
            var header = new Panel { Dock = DockStyle.Top, Height = 72, BackColor = Accent };
            header.Paint += (s, e) =>
            {
                using (var brush = new LinearGradientBrush(header.ClientRectangle, Accent, AccentDark, LinearGradientMode.Horizontal))
                    e.Graphics.FillRectangle(brush, header.ClientRectangle);
            };
            var title = new Label { Text = "Intune Package Grabber", ForeColor = Color.White, BackColor = Color.Transparent, Font = new Font("Segoe UI Semibold", 15f), AutoSize = true, Location = new Point(20, 8) };
            var subtitle = new Label { Text = "Cattura contenuto e .intunewin dei pacchetti Win32 durante l'installazione da Company Portal", ForeColor = Color.FromArgb(220, 235, 250), BackColor = Color.Transparent, AutoSize = true, Location = new Point(22, 46) };
            header.Controls.Add(title);
            header.Controls.Add(subtitle);

            // Impostazioni
            var settings = new Panel { Dock = DockStyle.Top, Height = 104, Padding = new Padding(20, 0, 20, 0) };
            // Dimensioni iniziali = dimensioni di progetto, cosi' gli Anchor mantengono i margini
            var card = MakeCard();
            card.Size = new Size(920, 104);
            var lblDest = new Label { Text = "Cartella di archiviazione", Font = new Font("Segoe UI Semibold", 9.5f), AutoSize = true, Location = new Point(14, 10) };
            txtDest = new TextBox { Location = new Point(16, 36), Size = new Size(758, 25), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, Font = new Font("Segoe UI", 10f) };
            btnBrowse = MakeButton("Sfoglia...", Color.White, Color.Black);
            btnBrowse.Location = new Point(784, 34);
            btnBrowse.Size = new Size(120, 30);
            btnBrowse.Font = new Font("Segoe UI", 9.5f);
            btnBrowse.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnBrowse.Click += OnBrowse;
            chkIntunewin = new CheckBox { Text = "Genera anche il file .intunewin (IntuneWinAppUtil, stesso file di setup della policy Intune)", Checked = true, AutoSize = true, Location = new Point(16, 72), ForeColor = TextMuted };
            chkIntunewin.CheckedChanged += delegate { UpdateDownloadButton(); };
            btnDownload = MakeButton("↓  Scarica IntuneWinAppUtil", Accent, Color.White);
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

            // Azioni
            var actions = new Panel { Dock = DockStyle.Top, Size = new Size(960, 64) };
            btnListen = MakeButton("\u25B6  Listen", GreenColor, Color.White);
            btnListen.Location = new Point(20, 14);
            btnListen.Click += delegate { StartListening(); };
            btnStop = MakeButton("\u25A0  Stop", RedColor, Color.White);
            btnStop.Location = new Point(156, 14);
            btnStop.Click += delegate { StopListening(); };
            btnOpen = MakeButton("Apri cartella", Color.White, Color.Black);
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

            // Lista pacchetti + log
            list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                BorderStyle = BorderStyle.None,
                Font = new Font("Segoe UI", 9.5f)
            };
            // Le colonne della ListView non vengono scalate da AutoScale: le scalo a mano
            float scale;
            using (var g = CreateGraphics()) scale = g.DpiX / 96f;
            list.Columns.Add("Ora", (int)(70 * scale));
            list.Columns.Add("Applicazione", (int)(250 * scale));
            list.Columns.Add("Ver.", (int)(45 * scale));
            list.Columns.Add("Stato", (int)(190 * scale));
            list.Columns.Add("Dimensione", (int)(90 * scale));
            list.Columns.Add("Percorso", (int)(500 * scale));
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
            split.Panel1.Controls.Add(WrapInCard(list, "Pacchetti catturati  (doppio clic per aprire)"));
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
            lblStatus.Text = value ? "In ascolto  •  " + cacheRoot : "Fermo";
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

        #region Eventi

        void OnBrowse(object sender, EventArgs e)
        {
            using (var dialog = new FolderBrowserDialog { Description = "Scegli dove archiviare i pacchetti", ShowNewFolderButton = true })
            {
                if (Directory.Exists(txtDest.Text)) dialog.SelectedPath = txtDest.Text;
                if (dialog.ShowDialog(this) == DialogResult.OK) txtDest.Text = dialog.SelectedPath;
            }
        }

        void UpdateDownloadButton()
        {
            // Il pulsante serve solo se si vuole il .intunewin e IntuneWinAppUtil non e' disponibile
            btnDownload.Visible = chkIntunewin.Checked && !HasIntuneWinAppUtil();
        }

        async void OnDownloadIntuneWinAppUtil(object sender, EventArgs e)
        {
            btnDownload.Enabled = false;
            btnDownload.Text = "Download in corso...";
            string temp = Path.Combine(Path.GetTempPath(), "IntunePackageGrabber", Guid.NewGuid().ToString("N") + ".exe");
            try
            {
                Log("Download di IntuneWinAppUtil.exe da " + IntuneWinAppUtilUrl);
                Directory.CreateDirectory(Path.GetDirectoryName(temp));
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                using (var client = new WebClient()) await client.DownloadFileTaskAsync(IntuneWinAppUtilUrl, temp);

                // Accetta solo un file con firma Authenticode valida di Microsoft
                if (!Authenticode.IsValid(temp)) throw new Exception("firma digitale non valida");
                string subject = X509Certificate.CreateFromSignedFile(temp).Subject;
                if (subject.IndexOf("O=Microsoft Corporation", StringComparison.OrdinalIgnoreCase) < 0) throw new Exception("file non firmato da Microsoft (" + subject + ")");

                Directory.CreateDirectory(appDataDir);
                string target = Path.Combine(appDataDir, "IntuneWinAppUtil.exe");
                if (File.Exists(target)) File.Delete(target);
                File.Move(temp, target);
                Log("IntuneWinAppUtil.exe scaricato e verificato (firma Microsoft): " + target);
            }
            catch (Exception ex)
            {
                Log("ERRORE download IntuneWinAppUtil.exe: " + ex.Message);
                MessageBox.Show(this, "Download di IntuneWinAppUtil.exe non riuscito:\r\n" + ex.Message + "\r\n\r\nIn alternativa copialo accanto a IntunePackageGrabber.exe.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                btnDownload.Text = "↓  Scarica IntuneWinAppUtil";
                btnDownload.Enabled = true;
                UpdateDownloadButton();
            }
        }

        void StartListening()
        {
            if (string.IsNullOrWhiteSpace(txtDest.Text))
            {
                MessageBox.Show(this, "Scegli una cartella di archiviazione.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            try
            {
                Directory.CreateDirectory(txtDest.Text);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Impossibile creare la cartella:\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            SaveSettings(txtDest.Text);
            if (!Directory.Exists(cacheRoot)) Log(cacheRoot + " non esiste ancora: verra' monitorata appena IME la crea.");

            // Le cartelle gia' presenti vengono trattate come nuove (di solito appartengono a un'installazione in corso),
            // tranne quelle gia' catturate in questa sessione: escono da 'handled' solo quando IME le rimuove
            pending.Clear();
            timer.Start();
            SetListening(true);
            Log("Listen avviato. Avvia ora l'installazione dal Company Portal.");
        }

        void StopListening()
        {
            timer.Stop();
            pending.Clear();
            SetListening(false);
            Log("Stop.");
            if (activeJobs > 0) Log("Attenzione: " + activeJobs + " cattura/e ancora in corso in background.");
        }

        void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (activeJobs > 0 && MessageBox.Show(this, "Ci sono catture in corso. Chiudere comunque?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.No)
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
                Log("Impossibile leggere " + cacheRoot + ": " + ex.Message);
                StopListening();
                return;
            }

            // Una cartella gia' catturata che IME ha rimosso puo' essere ricatturata se ricompare (retry/reinstallazione)
            handled.RemoveWhere(d => !Directory.Exists(d));

            // Solo cartelle delle app (<GUID>_<versione>), esclude HealthScripts ecc.
            foreach (var dir in dirs)
            {
                if (!Regex.IsMatch(Path.GetFileName(dir), @"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")) continue;
                if (handled.Contains(dir) || pending.ContainsKey(dir)) continue;
                pending[dir] = new PendingFolder { Snapshot = null, Since = DateTime.Now };
                Log("Rilevato pacchetto " + Path.GetFileName(dir) + ", attendo la fine dell'estrazione...");
            }

            // Cattura quando il contenuto resta invariato per StableSeconds
            foreach (var dir in pending.Keys.ToList())
            {
                var entry = pending[dir];
                if (!Directory.Exists(dir))
                {
                    Log("Cartella rimossa da IME prima della copia: " + Path.GetFileName(dir));
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
                    // Dimensioni invariate non bastano: NTFS puo' riportare dimensioni vecchie per un file ancora in scrittura
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

        #region Cattura

        void StartCapture(string source)
        {
            string folderName = Path.GetFileName(source);
            string appId = folderName.Split('_')[0];
            string version = folderName.Contains("_") ? folderName.Substring(folderName.IndexOf('_') + 1) : "";
            var item = new ListViewItem(new[] { DateTime.Now.ToString("HH:mm:ss"), appId, version, "Copia in corso...", "", "" });
            item.UseItemStyleForSubItems = false;
            item.SubItems[ColStatus].ForeColor = Accent;
            list.Items.Insert(0, item);

            string destRoot = txtDest.Text;
            bool makeIntunewin = chkIntunewin.Checked;
            Interlocked.Increment(ref activeJobs);
            Task.Run(() => CapturePackage(source, appId, version, destRoot, makeIntunewin, item));
        }

        static readonly object destLock = new object();

        // Cartella di destinazione univoca anche per catture parallele della stessa app
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
            // Copia prima in una cartella temporanea dal percorso corto: IntuneWinAppUtil non gestisce percorsi oltre 260 caratteri
            string staging = Path.Combine(Path.GetTempPath(), "IPG", Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                AppPolicy policy = FindPolicy(appId);
                string label = policy != null && !string.IsNullOrEmpty(policy.Name) ? SafeName(policy.Name) : appId;
                UpdateItem(item, ColApp, policy != null ? policy.Name : appId + " (nome non trovato nei log IME)");

                string baseName = label + (version.Length > 0 ? "_v" + version : "") + "_" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                string dest = CreateUniqueDest(destRoot, baseName);
                UpdateItem(item, ColPath, dest);
                Log("Cattura di " + label + " in " + dest);

                // Copia e verifica del contenuto
                string stagedContent = Path.Combine(staging, "Content");
                long sourceBytes; int sourceFiles;
                Measure(source, out sourceFiles, out sourceBytes);
                CopyDirectory(source, stagedContent);
                long copiedBytes; int copiedFiles;
                Measure(stagedContent, out copiedFiles, out copiedBytes);
                if (copiedFiles != sourceFiles || copiedBytes != sourceBytes)
                    throw new Exception(string.Format("contenuto incompleto: {0} file / {1} byte copiati su {2} / {3}", copiedFiles, copiedBytes, sourceFiles, sourceBytes));
                UpdateItem(item, ColSize, FormatSize(copiedBytes));

                string setup = null, intunewin = null;
                if (makeIntunewin)
                {
                    UpdateItem(item, ColStatus, "Creazione .intunewin...");
                    setup = policy != null && !string.IsNullOrEmpty(policy.SetupFile) ? policy.SetupFile : GuessSetupFile(stagedContent);
                    if (setup != null)
                    {
                        intunewin = BuildIntunewin(stagedContent, setup, staging, label);
                        VerifyIntunewin(intunewin, setup);
                    }
                }

                // Sposta tutto nella destinazione finale e riverifica
                string content = Path.Combine(dest, "Content");
                MoveDirectory(stagedContent, content);
                Measure(content, out copiedFiles, out copiedBytes);
                if (copiedFiles != sourceFiles || copiedBytes != sourceBytes)
                    throw new Exception(string.Format("contenuto incompleto nella destinazione: {0} file / {1} byte su {2} / {3}", copiedFiles, copiedBytes, sourceFiles, sourceBytes));
                Log(string.Format("Contenuto copiato e verificato: {0} file, {1}", copiedFiles, FormatSize(copiedBytes)));
                WriteInfo(dest, policy, appId, source, copiedFiles, copiedBytes);

                if (intunewin != null)
                {
                    string finalIntunewin = Path.Combine(dest, Path.GetFileName(intunewin));
                    File.Move(LongPath(intunewin), LongPath(finalIntunewin));
                    Log(".intunewin creato e verificato: " + finalIntunewin + " (setup: " + setup + ")");
                    UpdateItem(item, ColStatus, "\u2714 Contenuto + .intunewin", GreenColor);
                }
                else if (makeIntunewin)
                {
                    Log("ATTENZIONE (" + appId + "): file di setup non determinato, .intunewin non creato. Il contenuto e' stato salvato.");
                    UpdateItem(item, ColStatus, "\u26A0 Solo contenuto (setup ignoto)", OrangeColor);
                }
                else
                {
                    UpdateItem(item, ColStatus, "\u2714 Contenuto", GreenColor);
                }
            }
            catch (Exception ex)
            {
                UpdateItem(item, ColStatus, "\u2716 Errore", RedColor);
                Log("ERRORE (" + appId + "): " + ex.Message);
            }
            finally
            {
                try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
                Interlocked.Decrement(ref activeJobs);
            }
        }

        // Prefisso \\?\ per superare il limite di 260 caratteri senza dipendere da LongPathsEnabled
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

        // False se un file e' ancora aperto in scrittura (FileShare.Read nega l'apertura in presenza di un writer)
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
            // 1) accanto all'exe, 2) scaricato in %APPDATA%, 3) incorporato come risorsa ed estratto in %TEMP%
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
            if (!File.Exists(tool)) throw new Exception("IntuneWinAppUtil.exe non disponibile");

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
            if (built == null) throw new Exception("IntuneWinAppUtil non ha prodotto il file .intunewin:\r\n" + output.Trim());

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
                if (detection == null || payload == null) throw new Exception(".intunewin non valido: struttura interna mancante");
                if (payload.Length == 0) throw new Exception(".intunewin non valido: contenuto vuoto");

                var xml = new XmlDocument();
                using (var s = detection.Open()) xml.Load(s);
                var setupNode = xml.SelectSingleNode("//*[local-name()='SetupFile']");
                if (setupNode == null || !string.Equals(setupNode.InnerText, expectedSetup, StringComparison.OrdinalIgnoreCase))
                    throw new Exception(".intunewin non valido: SetupFile = " + (setupNode == null ? "(assente)" : setupNode.InnerText) + ", atteso " + expectedSetup);
            }
        }

        #endregion

        #region Policy dai log IME

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

                // Dalla policy piu' recente alla piu' vecchia
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
            sb.AppendLine("Nome app            : " + (policy != null ? policy.Name : "(non trovato nei log IME)"));
            sb.AppendLine("App ID              : " + appId);
            sb.AppendLine("Versione contenuto  : " + (policy != null ? policy.Version : ""));
            sb.AppendLine("Sorgente            : " + source);
            sb.AppendLine("Catturato           : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " su " + Environment.MachineName);
            sb.AppendLine("Contenuto           : " + files + " file, " + FormatSize(bytes));
            if (policy != null)
            {
                sb.AppendLine();
                sb.AppendLine("File di setup       : " + policy.SetupFile);
                sb.AppendLine("Comando install     : " + policy.InstallCommand);
                sb.AppendLine("Comando uninstall   : " + policy.UninstallCommand);
                sb.AppendLine();
                sb.AppendLine("Regola rilevamento  : " + policy.DetectionRule);
                sb.AppendLine("Requisiti           : " + policy.RequirementRules);
                sb.AppendLine("Codici di ritorno   : " + policy.ReturnCodes);
                File.WriteAllText(LongPath(Path.Combine(dest, "policy.json")), policy.RawJson, Encoding.UTF8);
            }
            File.WriteAllText(LongPath(Path.Combine(dest, "app-info.txt")), sb.ToString(), Encoding.UTF8);
        }

        #endregion

        #region Utilita'

        static string SafeName(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name.Trim();
        }

        static string FormatSize(long bytes)
        {
            if (bytes >= 1L << 30) return (bytes / (double)(1L << 30)).ToString("N2") + " GB";
            if (bytes >= 1L << 20) return (bytes / (double)(1L << 20)).ToString("N1") + " MB";
            return (bytes / 1024.0).ToString("N0") + " KB";
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

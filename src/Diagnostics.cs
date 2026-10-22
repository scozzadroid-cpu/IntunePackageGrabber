// Diagnostics for technicians and Intune engineers, read-only and local: the Win32 apps and the remediations that
// the Intune Management Extension (IME) knows on this device (IME registry, IMECache, AppWorkload logs) and the
// collection of the logs for support tickets. Inspired by IntuneWin Downloader by Rudy Ooms (call4cloud), MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace IntunePackageGrabber
{
    class Win32AppState
    {
        public string AppId;
        public int Revision;
        public string UserId;
        public string Name;
        public string Intent;
        public string Detected;
        public string Enforcement;
        public string ErrorCode;
        public bool Failed;
        public string LastUpdate;
        public string ComplianceJson;
        public string EnforcementJson;
        public AppPolicy Policy;
    }

    class RemediationState
    {
        public string PolicyId;
        public string Version;
        public string UserId;
        public string LastRun;
        public string Detection;
        public string Remediation;
        public string PostDetection;
        public string ErrorCode;
        public bool Failed;
        public string Folder;
        public string ResultJson;
        public Dictionary<string, object> Result;
    }

    // Click on a column header sorts by that column, a second click reverses the order
    class ColumnSorter : System.Collections.IComparer
    {
        public int Column;
        public bool Descending;

        public int Compare(object x, object y)
        {
            int r = string.Compare(((ListViewItem)x).SubItems[Column].Text, ((ListViewItem)y).SubItems[Column].Text, StringComparison.CurrentCultureIgnoreCase);
            return Descending ? -r : r;
        }
    }

    partial class MainForm
    {
        // Overridable with --imereg for the automated tests
        static string ImeRegistry = @"HKLM\SOFTWARE\Microsoft\IntuneManagementExtension";
        const string DeviceUser = "00000000-0000-0000-0000-000000000000";
        static readonly Regex GuidName = new Regex(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$");
        // <policy or app id>_<revision>
        static readonly Regex VersionedName = new Regex(@"^([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})_(\d+)$");

        ListView appList, remediationList;
        Label lblApps, lblRemediations;
        Button btnCollectLogs;
        bool appsLoaded, remediationsLoaded, appsLoading, remediationsLoading;

        #region UI

        static Control MakeTab(Control content)
        {
            var page = new Panel { Padding = new Padding(0, 6, 0, 0) };
            content.Dock = DockStyle.Fill;
            page.Controls.Add(content);
            return page;
        }

        ListView MakeList(string[] columns, int[] widths)
        {
            var lv = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                HideSelection = false,
                BorderStyle = BorderStyle.None,
                Font = new Font(UiFont, 9.5f)
            };
            UseDarkListView(lv);
            // ListView columns are not scaled by AutoScale: scale them manually
            float scale;
            using (var g = CreateGraphics()) scale = g.DpiX / 96f;
            for (int i = 0; i < columns.Length; i++) lv.Columns.Add(columns[i], (int)(widths[i] * scale));
            lv.ColumnClick += (s, e) =>
            {
                var current = lv.ListViewItemSorter as ColumnSorter;
                lv.ListViewItemSorter = new ColumnSorter { Column = e.Column, Descending = current != null && current.Column == e.Column && !current.Descending };
            };
            return lv;
        }

        Button MakeSmallButton(string text, int width, EventHandler onClick)
        {
            var b = MakeButton(text, ControlBack, TextPrimary);
            b.Font = new Font(UiSemibold, 9f);
            b.Size = new Size(width, 26);
            b.Click += onClick;
            return b;
        }

        // Small buttons right-aligned in the header of a card
        static void AddCardButtons(Control card, params Button[] buttons)
        {
            int x = card.Width - 6;
            foreach (var b in buttons.Reverse())
            {
                x -= b.Width;
                b.Location = new Point(x, 3);
                b.Anchor = AnchorStyles.Top | AnchorStyles.Right;
                card.Controls.Add(b);
                x -= 6;
            }
        }

        Control BuildAppsTab()
        {
            appList = MakeList(
                new[] { "Application", "Context", "Intent", "Detected", "Last enforcement", "Error", "Last update (UTC)", "App ID" },
                new[] { 250, 65, 120, 105, 150, 95, 135, 260 });
            appList.DoubleClick += delegate { ShowSelectedApp(); };
            var card = WrapInCard(appList, "Win32 apps known by IME on this device");
            lblApps = card.Controls.OfType<Label>().First();
            AddCardButtons(card, MakeSmallButton("Refresh", 80, delegate { RefreshApps(); }), MakeSmallButton("Export selected", 120, delegate { ExportApps(); }));
            return card;
        }

        Control BuildRemediationsTab()
        {
            remediationList = MakeList(
                new[] { "Policy ID", "Ver.", "Context", "Last run", "Detection", "Remediation", "Post-detection", "Error" },
                new[] { 260, 45, 65, 135, 130, 120, 150, 95 });
            remediationList.DoubleClick += delegate { ShowSelectedRemediation(); };
            var card = WrapInCard(remediationList, "Remediations on this device");
            lblRemediations = card.Controls.OfType<Label>().First();
            AddCardButtons(card, MakeSmallButton("Refresh", 80, delegate { RefreshRemediations(); }), MakeSmallButton("Export selected", 120, delegate { ExportRemediations(); }));
            return card;
        }

        // Read-only text pages: details, scripts and JSON in a monospaced font, or prose (tutorial)
        void ShowTextViewer(string title, List<KeyValuePair<string, string>> pages, bool prose = false)
        {
            using (var viewer = new Form
            {
                Text = title,
                AutoScaleDimensions = new SizeF(96F, 96F),
                AutoScaleMode = AutoScaleMode.Dpi,
                Font = Font,
                BackColor = Background,
                ForeColor = TextPrimary,
                ClientSize = prose ? new Size(760, 520) : new Size(900, 600),
                Padding = new Padding(16, 8, 16, 16),
                MinimizeBox = false,
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.CenterParent
            })
            {
                viewer.SuspendLayout();
                UseDarkTitleBar(viewer);
                var tabs = new TabStrip { Dock = DockStyle.Fill };
                foreach (var p in pages)
                {
                    var box = new TextBox
                    {
                        Dock = DockStyle.Fill,
                        Multiline = true,
                        ReadOnly = true,
                        MaxLength = 0,
                        ScrollBars = prose ? ScrollBars.Vertical : ScrollBars.Both,
                        WordWrap = prose,
                        BorderStyle = BorderStyle.None,
                        BackColor = prose ? CardBack : ConsoleBack,
                        ForeColor = prose ? TextPrimary : ConsoleText,
                        Font = prose ? new Font(UiFont, 10.5f) : new Font(MonoFont, 9.5f),
                        Text = (p.Value ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n")
                    };
                    // An explicit selection prevents the select-all when the box gets the focus
                    box.Select(0, 0);
                    UseDarkScrollbars(box);
                    // TextBox has no padding: the panel around it gives the margins
                    var frame = new Panel { BackColor = box.BackColor, Padding = prose ? new Padding(18, 14, 8, 8) : new Padding(8, 6, 0, 0) };
                    frame.Controls.Add(box);
                    tabs.AddPage(p.Key, frame);
                }
                viewer.Controls.Add(tabs);
                viewer.ResumeLayout(false);
                viewer.ShowDialog(this);
            }
        }

        static string Context(string userId)
        {
            if (userId == null) return "";
            return string.Equals(userId, DeviceUser, StringComparison.OrdinalIgnoreCase) ? "Device" : "User";
        }

        static string HexCode(long code)
        {
            return "0x" + ((uint)code).ToString("X8");
        }

        #endregion

        #region Win32 apps

        async void RefreshApps()
        {
            if (appsLoading) return;
            appsLoading = true;
            lblApps.Text = "Win32 apps known by IME on this device  •  loading...";
            try
            {
                var apps = await Task.Run(() => LoadWin32Apps());
                appsLoaded = true;
                appList.BeginUpdate();
                appList.Items.Clear();
                foreach (var a in apps)
                {
                    var item = new ListViewItem(new[] { a.Name, Context(a.UserId), a.Intent, a.Detected, a.Enforcement, a.ErrorCode, a.LastUpdate, a.AppId }) { Tag = a };
                    if (a.Failed) item.ForeColor = RedColor;
                    appList.Items.Add(item);
                }
                appList.EndUpdate();
                lblApps.Text = string.Format("Win32 apps known by IME on this device: {0}  •  {1} with errors  (double-click for details)", apps.Count, apps.Count(a => a.Failed));
            }
            catch (Exception ex)
            {
                lblApps.Text = "Win32 apps known by IME on this device  •  read error";
                Log("ERROR reading the Win32 apps: " + ex.Message);
            }
            finally
            {
                appsLoading = false;
            }
        }

        // States from HKLM\...\IntuneManagementExtension\Win32Apps\<user>\<app>_<revision>, names and commands from the IME logs
        static List<Win32AppState> LoadWin32Apps()
        {
            var policies = LoadLoggedPolicies();
            var rows = new Dictionary<string, Win32AppState>(StringComparer.OrdinalIgnoreCase);
            using (var root = OpenImeKey("Win32Apps"))
            {
                if (root != null)
                {
                    foreach (var user in root.GetSubKeyNames().Where(n => GuidName.IsMatch(n)))
                    {
                        using (var userKey = root.OpenSubKey(user))
                        {
                            if (userKey == null) continue;
                            foreach (var name in userKey.GetSubKeyNames())
                            {
                                var m = VersionedName.Match(name);
                                if (!m.Success) continue;
                                // Older revisions of the same app are left over: keep the newest
                                int revision = int.Parse(m.Groups[2].Value);
                                string key = user + "|" + m.Groups[1].Value;
                                Win32AppState existing;
                                if (rows.TryGetValue(key, out existing) && existing.Revision > revision) continue;
                                using (var appKey = userKey.OpenSubKey(name))
                                    if (appKey != null) rows[key] = ReadAppState(appKey, m.Groups[1].Value, revision, user);
                            }
                        }
                    }
                }
            }

            var result = rows.Values.ToList();
            // Assigned apps that IME has not evaluated yet
            foreach (var p in policies.Values)
                if (!result.Any(r => string.Equals(r.AppId, p.Id, StringComparison.OrdinalIgnoreCase)))
                    result.Add(new Win32AppState { AppId = p.Id, Detected = "No state yet", Enforcement = "", ErrorCode = "", LastUpdate = "", Intent = IntentName(p.Intent, false) });

            foreach (var app in result)
            {
                AppPolicy policy;
                if (policies.TryGetValue(app.AppId, out policy)) app.Policy = policy;
                app.Name = policy != null && !string.IsNullOrEmpty(policy.Name) ? policy.Name : "(name not in IME logs)";
                if (string.IsNullOrEmpty(app.Intent) && policy != null) app.Intent = IntentName(policy.Intent, false);
            }
            // Named apps first (errors on top), apps without a name at the end
            return result
                .OrderBy(a => a.Policy == null || string.IsNullOrEmpty(a.Policy.Name))
                .ThenByDescending(a => a.Failed)
                .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(a => a.AppId)
                .ToList();
        }

        static RegistryKey OpenImeKey(string subKey)
        {
            int slash = ImeRegistry.IndexOf('\\');
            var hive = ImeRegistry.StartsWith("HKCU", StringComparison.OrdinalIgnoreCase) ? RegistryHive.CurrentUser : RegistryHive.LocalMachine;
            using (var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64))
                return root.OpenSubKey(ImeRegistry.Substring(slash + 1) + @"\" + subKey);
        }

        // After an export: asks whether to open the folder now (selecting the file, if given)
        void AskOpenFolder(string message, string folder, string file)
        {
            if (MessageBox.Show(this, message + "\r\n\r\nOpen the folder now?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            if (file != null) Process.Start("explorer.exe", "/select,\"" + file + "\"");
            else OpenFolder(folder);
        }

        static Win32AppState ReadAppState(RegistryKey appKey, string appId, int revision, string user)
        {
            var app = new Win32AppState { AppId = appId, Revision = revision, UserId = user, ErrorCode = "", Enforcement = "", Detected = "" };
            app.LastUpdate = Convert.ToString(appKey.GetValue("LastUpdatedTimeUtc"));
            app.ComplianceJson = SubKeyValue(appKey, "ComplianceStateMessage");
            app.EnforcementJson = SubKeyValue(appKey, "EnforcementStateMessage");
            var compliance = ParseJson(app.ComplianceJson);
            var enforcement = ParseJson(app.EnforcementJson);

            // TargetingMethod 1 = installed as a dependency of another app
            int? targeting = IntOrNull(compliance, "TargetingMethod") ?? IntOrNull(enforcement, "TargetingMethod");
            app.Intent = IntentName(Convert.ToString(appKey.GetValue("Intent")), targeting == 1);

            int? applicability = IntOrNull(compliance, "Applicability");
            int? state = IntOrNull(compliance, "ComplianceState");
            if (applicability.HasValue && applicability.Value != 0) app.Detected = "Not applicable (" + applicability.Value + ")";
            else if (state.HasValue) app.Detected = ComplianceName(state.Value);

            int? enforcementState = IntOrNull(enforcement, "EnforcementState");
            if (enforcementState.HasValue) app.Enforcement = EnforcementName(enforcementState.Value);

            long? error = LongOrNull(enforcement, "ErrorCode");
            if (!error.HasValue || error.Value == 0) error = LongOrNull(compliance, "ErrorCode");
            if (error.HasValue && error.Value != 0) app.ErrorCode = HexCode(error.Value);

            app.Failed = app.ErrorCode.Length > 0 || state == 4 || (enforcementState.HasValue && enforcementState.Value / 1000 == 5);
            return app;
        }

        static string SubKeyValue(RegistryKey key, string name)
        {
            using (var sub = key.OpenSubKey(name)) return sub != null ? Convert.ToString(sub.GetValue(name)) : null;
        }

        static Dictionary<string, object> ParseJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try { return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(json) as Dictionary<string, object>; }
            catch { return null; }
        }

        static int? IntOrNull(Dictionary<string, object> d, string key)
        {
            long? v = LongOrNull(d, key);
            return v.HasValue ? (int?)(int)v.Value : null;
        }

        static long? LongOrNull(Dictionary<string, object> d, string key)
        {
            object v;
            long n;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return null;
            return long.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), out n) ? (long?)n : null;
        }

        static string IntentName(string intent, bool dependency)
        {
            string name;
            switch (intent)
            {
                case "0": name = "Not targeted"; break;
                case "1": name = "Available"; break;
                case "3": name = "Required"; break;
                case "4": name = "Uninstall"; break;
                case null: case "": name = ""; break;
                default: name = "Intent " + intent; break;
            }
            return dependency ? (name + " (dependency)").Trim() : name;
        }

        static string ComplianceName(int state)
        {
            switch (state)
            {
                case 1: return "Installed";
                case 2: return "Not installed";
                case 4: return "Error";
                case 5: return "Unknown";
                default: return "State " + state;
            }
        }

        // IME enforcement states are grouped by thousands
        static string EnforcementName(int state)
        {
            string[] names = { "", "Success", "In progress", "Requirements not met", "Unknown", "Error" };
            int group = state / 1000;
            return (group >= 1 && group < names.Length ? names[group] : "State") + " (" + state + ")";
        }

        static string AppDetails(Win32AppState app, string dest)
        {
            var sb = new StringBuilder();
            sb.AppendLine("App name            : " + app.Name);
            sb.AppendLine("App ID              : " + app.AppId + (app.Revision > 0 ? " (revision " + app.Revision + ")" : ""));
            sb.AppendLine("Content version     : " + (app.Policy != null ? app.Policy.Version : ""));
            sb.AppendLine("Context             : " + Context(app.UserId) + (app.UserId != null && Context(app.UserId) == "User" ? " " + app.UserId : ""));
            sb.AppendLine("Intent              : " + app.Intent);
            sb.AppendLine("Detected            : " + app.Detected);
            sb.AppendLine("Last enforcement    : " + app.Enforcement);
            sb.AppendLine("Error code          : " + app.ErrorCode);
            sb.AppendLine("Last update (UTC)   : " + app.LastUpdate);
            sb.AppendLine("Read                : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " on " + Environment.MachineName);
            sb.AppendLine();
            sb.AppendLine("Compliance state    : " + app.ComplianceJson);
            sb.AppendLine("Enforcement state   : " + app.EnforcementJson);
            if (app.Policy != null) AppendPolicyInfo(sb, app.Policy, dest);
            else
            {
                sb.AppendLine();
                sb.AppendLine("The policy of this app is not in the IME logs: commands and detection rules are not available.");
            }
            return sb.ToString();
        }

        Win32AppState SelectedApp()
        {
            return appList.SelectedItems.Count > 0 ? (Win32AppState)appList.SelectedItems[0].Tag : null;
        }

        void ShowSelectedApp()
        {
            var app = SelectedApp();
            if (app == null) return;
            var pages = new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("Details", AppDetails(app, null)) };
            if (app.Policy != null)
            {
                byte[] script;
                DescribeDetection(app.Policy.DetectionRule, out script);
                if (script != null) pages.Add(new KeyValuePair<string, string>("Detection script", Encoding.UTF8.GetString(script)));
                pages.Add(new KeyValuePair<string, string>("Policy JSON", app.Policy.RawJson));
            }
            ShowTextViewer(app.Name, pages);
        }

        void ExportApps()
        {
            var apps = appList.SelectedItems.Cast<ListViewItem>().Select(i => (Win32AppState)i.Tag).ToList();
            if (apps.Count == 0)
            {
                MessageBox.Show(this, "Select one or more apps.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (!PrepareDest()) return;
            var exported = new List<string>();
            foreach (var app in apps)
            {
                try
                {
                    string label = app.Policy != null && !string.IsNullOrEmpty(app.Policy.Name) ? SafeName(app.Policy.Name) : app.AppId;
                    string dest = CreateUniqueDest(txtDest.Text, label + "_info_" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                    File.WriteAllText(LongPath(Path.Combine(dest, "app-info.txt")), AppDetails(app, dest), Encoding.UTF8);
                    Log("App info exported: " + dest);
                    exported.Add(dest);
                }
                catch (Exception ex)
                {
                    Log("ERROR exporting " + app.Name + ": " + ex.Message);
                }
            }
            AskOpenExported(exported, "app");
        }

        // One export: opens its folder; more: opens the archive folder
        void AskOpenExported(List<string> exported, string what)
        {
            if (exported.Count == 1) AskOpenFolder("Exported " + what + " to:\r\n" + exported[0], exported[0], null);
            else if (exported.Count > 1) AskOpenFolder(exported.Count + " " + what + "s exported to:\r\n" + txtDest.Text, txtDest.Text, null);
        }

        #endregion

        #region Remediations

        async void RefreshRemediations()
        {
            if (remediationsLoading) return;
            remediationsLoading = true;
            lblRemediations.Text = "Remediations on this device  •  loading...";
            try
            {
                var rows = await Task.Run(() => LoadRemediations());
                remediationsLoaded = true;
                remediationList.BeginUpdate();
                remediationList.Items.Clear();
                foreach (var r in rows)
                {
                    var item = new ListViewItem(new[] { r.PolicyId, r.Version, Context(r.UserId), r.LastRun, r.Detection, r.Remediation, r.PostDetection, r.ErrorCode }) { Tag = r };
                    if (r.Failed) item.ForeColor = RedColor;
                    remediationList.Items.Add(item);
                }
                remediationList.EndUpdate();
                lblRemediations.Text = string.Format("Remediations on this device: {0}  •  {1} with problems  (double-click for scripts and output)", rows.Count, rows.Count(r => r.Failed));
            }
            catch (Exception ex)
            {
                lblRemediations.Text = "Remediations on this device  •  read error";
                Log("ERROR reading the remediations: " + ex.Message);
            }
            finally
            {
                remediationsLoading = false;
            }
        }

        // Scripts from IMECache\HealthScripts\<policy>_<version>, results from HKLM\...\SideCarPolicies\Scripts\Reports and Execution
        List<RemediationState> LoadRemediations()
        {
            var folders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string cache = Path.Combine(cacheRoot, "HealthScripts");
            if (Directory.Exists(cache))
                foreach (var dir in Directory.GetDirectories(cache))
                    if (VersionedName.IsMatch(Path.GetFileName(dir))) folders[Path.GetFileName(dir)] = dir;

            var rows = new List<RemediationState>();
            using (var reports = OpenImeKey(@"SideCarPolicies\Scripts\Reports"))
            using (var execution = OpenImeKey(@"SideCarPolicies\Scripts\Execution"))
            {
                if (reports != null)
                {
                    foreach (var user in reports.GetSubKeyNames().Where(n => GuidName.IsMatch(n)))
                    {
                        using (var userKey = reports.OpenSubKey(user))
                        {
                            if (userKey == null) continue;
                            foreach (var name in userKey.GetSubKeyNames())
                            {
                                var m = VersionedName.Match(name);
                                if (!m.Success) continue;
                                string json;
                                using (var resultKey = userKey.OpenSubKey(name + @"\Result")) json = resultKey != null ? Convert.ToString(resultKey.GetValue("Result")) : null;
                                string lastRun = null;
                                if (execution != null)
                                    using (var runKey = execution.OpenSubKey(user + @"\" + name)) if (runKey != null) lastRun = Convert.ToString(runKey.GetValue("LastExecution"));
                                string folder;
                                folders.TryGetValue(name, out folder);
                                rows.Add(ReadRemediation(m.Groups[1].Value, m.Groups[2].Value, user, lastRun, json, folder));
                            }
                        }
                    }
                }
            }

            // Scripts cached but never reported yet
            foreach (var f in folders)
                if (!rows.Any(r => string.Equals(r.PolicyId + "_" + r.Version, f.Key, StringComparison.OrdinalIgnoreCase)))
                {
                    var m = VersionedName.Match(f.Key);
                    rows.Add(ReadRemediation(m.Groups[1].Value, m.Groups[2].Value, null, null, null, f.Value));
                }
            return rows.OrderByDescending(r => r.Failed).ThenBy(r => r.PolicyId).ToList();
        }

        static RemediationState ReadRemediation(string policyId, string version, string user, string lastRun, string json, string folder)
        {
            var r = new RemediationState { PolicyId = policyId, Version = version, UserId = user, LastRun = lastRun ?? "", Folder = folder, ResultJson = json, ErrorCode = "" };
            r.Result = ParseJson(json);
            var info = r.Result != null && r.Result.ContainsKey("Info") ? r.Result["Info"] as Dictionary<string, object> : null;

            // Detection script: exit 0 = no issue, otherwise the remediation script runs
            int? first = IntOrNull(info, "FirstDetectExitCode");
            int? remediation = IntOrNull(info, "RemediationExitCode");
            int? last = IntOrNull(info, "LastDetectExitCode");
            r.Detection = r.Result == null ? "No result yet" : !first.HasValue ? "-" : first.Value == 0 ? "No issue (exit 0)" : "Issue found (exit " + first.Value + ")";
            r.Remediation = r.Result == null ? "" : !remediation.HasValue ? "Not run" : remediation.Value == 0 ? "Done (exit 0)" : "Failed (exit " + remediation.Value + ")";
            r.PostDetection = !last.HasValue ? "" : last.Value == 0 ? "Fixed (exit 0)" : "Issue remains (exit " + last.Value + ")";
            long? error = LongOrNull(r.Result, "ErrorCode");
            if (error.HasValue && error.Value != 0) r.ErrorCode = HexCode(error.Value);
            r.Failed = r.ErrorCode.Length > 0 || (remediation.HasValue && remediation.Value != 0) || (last.HasValue && last.Value != 0);
            return r;
        }

        static string ReadScript(RemediationState r, string file)
        {
            string path = r.Folder != null ? Path.Combine(r.Folder, file) : null;
            try { return path != null && File.Exists(path) ? File.ReadAllText(path) : null; }
            catch (Exception ex) { return "Unable to read " + path + ": " + ex.Message; }
        }

        static string RemediationDetails(RemediationState r)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Policy ID           : " + r.PolicyId);
            sb.AppendLine("Version             : " + r.Version);
            sb.AppendLine("Context             : " + Context(r.UserId) + (Context(r.UserId) == "User" ? " " + r.UserId : ""));
            sb.AppendLine("Last run            : " + r.LastRun);
            sb.AppendLine("Detection           : " + r.Detection);
            sb.AppendLine("Remediation         : " + r.Remediation);
            sb.AppendLine("Post-detection      : " + r.PostDetection);
            sb.AppendLine("Error code          : " + r.ErrorCode);
            sb.AppendLine("Scripts folder      : " + (r.Folder ?? "(not in IMECache)"));
            sb.AppendLine("Read                : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " on " + Environment.MachineName);
            if (r.Result != null)
            {
                foreach (var field in new[] { "PreRemediationDetectScriptOutput", "PreRemediationDetectScriptError", "RemediationScriptOutputDetails", "RemediationScriptErrorDetails", "PostRemediationDetectScriptOutput", "PostRemediationDetectScriptError" })
                {
                    string value = Str(r.Result, field);
                    if (string.IsNullOrEmpty(value)) continue;
                    sb.AppendLine();
                    sb.AppendLine("--- " + field + " ---");
                    sb.AppendLine(value.Trim());
                }
                var info = r.Result.ContainsKey("Info") ? r.Result["Info"] as Dictionary<string, object> : null;
                if (info != null)
                {
                    sb.AppendLine();
                    sb.AppendLine("--- Info ---");
                    foreach (var kv in info) sb.AppendLine(kv.Key.PadRight(20) + ": " + Convert.ToString(kv.Value));
                }
            }
            return sb.ToString();
        }

        RemediationState SelectedRemediation()
        {
            return remediationList.SelectedItems.Count > 0 ? (RemediationState)remediationList.SelectedItems[0].Tag : null;
        }

        void ShowSelectedRemediation()
        {
            var r = SelectedRemediation();
            if (r == null) return;
            var pages = new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("Details", RemediationDetails(r)) };
            foreach (var file in new[] { "detect.ps1", "remediate.ps1" })
            {
                string script = ReadScript(r, file);
                if (script != null) pages.Add(new KeyValuePair<string, string>(file, script));
            }
            if (r.ResultJson != null) pages.Add(new KeyValuePair<string, string>("Result JSON", r.ResultJson));
            ShowTextViewer("Remediation " + r.PolicyId, pages);
        }

        void ExportRemediations()
        {
            var rows = remediationList.SelectedItems.Cast<ListViewItem>().Select(i => (RemediationState)i.Tag).ToList();
            if (rows.Count == 0)
            {
                MessageBox.Show(this, "Select one or more remediations.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (!PrepareDest()) return;
            var exported = new List<string>();
            foreach (var r in rows)
            {
                try
                {
                    string dest = CreateUniqueDest(txtDest.Text, "Remediation_" + r.PolicyId + "_v" + r.Version + "_" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                    File.WriteAllText(LongPath(Path.Combine(dest, "info.txt")), RemediationDetails(r), Encoding.UTF8);
                    foreach (var file in new[] { "detect.ps1", "remediate.ps1" })
                        if (r.Folder != null && File.Exists(Path.Combine(r.Folder, file))) File.Copy(Path.Combine(r.Folder, file), LongPath(Path.Combine(dest, file)));
                    if (r.ResultJson != null) File.WriteAllText(LongPath(Path.Combine(dest, "result.json")), r.ResultJson, Encoding.UTF8);
                    Log("Remediation exported: " + dest);
                    exported.Add(dest);
                }
                catch (Exception ex)
                {
                    Log("ERROR exporting remediation " + r.PolicyId + ": " + ex.Message);
                }
            }
            AskOpenExported(exported, "remediation");
        }

        #endregion

        #region Log collection

        async void OnCollectLogs(object sender, EventArgs e)
        {
            if (!PrepareDest()) return;
            string zip = Path.Combine(txtDest.Text, "IntuneDiagnostics_" + SafeName(Environment.MachineName) + "_" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".zip");
            btnCollectLogs.Enabled = false;
            btnCollectLogs.Text = "Collecting...";
            try
            {
                Log("Collecting the diagnostics to " + zip);
                int count = await Task.Run(() => CollectDiagnostics(zip));
                string summary = string.Format("Diagnostics collected: {0} files, {1}", count, FormatSize(new FileInfo(LongPath(zip)).Length));
                Log(summary);
                btnCollectLogs.Text = "Collect logs";
                btnCollectLogs.Enabled = true;
                AskOpenFolder(summary + "\r\n" + zip, null, zip);
            }
            catch (Exception ex)
            {
                Log("ERROR collecting the diagnostics: " + ex.Message);
            }
            finally
            {
                btnCollectLogs.Text = "Collect logs";
                btnCollectLogs.Enabled = true;
            }
        }

        int CollectDiagnostics(string zipPath)
        {
            string temp = Path.Combine(Path.GetTempPath(), "IPG", "diag-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(temp);
            int count = 0;
            try
            {
                using (var zip = ZipFile.Open(LongPath(zipPath), ZipArchiveMode.Create))
                {
                    count += AddFiles(zip, ImeLogFolder, "IME");
                    count += AddFiles(zip, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Packages\Microsoft.CompanyPortal_8wekyb3d8bbwe\LocalState"), "CompanyPortal");
                    count += AddFile(zip, logFile, "IntunePackageGrabber/grabber.log");

                    count += AddText(zip, "dsregcmd-status.txt", RunTool("dsregcmd.exe", "/status"));
                    string reg = Path.Combine(temp, "IntuneManagementExtension.reg");
                    RunTool("reg.exe", "export \"" + ImeRegistry + "\" \"" + reg + "\" /y /reg:64");
                    count += AddFile(zip, reg, "registry/IntuneManagementExtension.reg");
                    string evtx = Path.Combine(temp, "DeviceManagement-Admin.evtx");
                    RunTool("wevtutil.exe", "epl \"Microsoft-Windows-DeviceManagement-Enterprise-Diagnostics-Provider/Admin\" \"" + evtx + "\"");
                    count += AddFile(zip, evtx, "eventlogs/DeviceManagement-Enterprise-Diagnostics-Provider-Admin.evtx");

                    count += AddText(zip, "win32-apps.csv", Csv(new[] { new[] { "Application", "Version", "Context", "User ID", "Intent", "Detected", "Last enforcement", "Error", "Last update (UTC)", "App ID", "Revision" } }
                        .Concat(LoadWin32Apps().Select(a => new[] { a.Name, a.Policy != null ? a.Policy.Version : "", Context(a.UserId), a.UserId, a.Intent, a.Detected, a.Enforcement, a.ErrorCode, a.LastUpdate, a.AppId, a.Revision.ToString() }))));
                    count += AddText(zip, "remediations.csv", Csv(new[] { new[] { "Policy ID", "Version", "Context", "User ID", "Last run", "Detection", "Remediation", "Post-detection", "Error" } }
                        .Concat(LoadRemediations().Select(r => new[] { r.PolicyId, r.Version, Context(r.UserId), r.UserId, r.LastRun, r.Detection, r.Remediation, r.PostDetection, r.ErrorCode }))));
                }
            }
            finally
            {
                try { Directory.Delete(temp, true); } catch { }
            }
            return count;
        }

        static int AddFiles(ZipArchive zip, string folder, string prefix)
        {
            if (!Directory.Exists(folder)) return 0;
            return Directory.GetFiles(folder, "*.log").Sum(f => AddFile(zip, f, prefix + "/" + Path.GetFileName(f)));
        }

        // The logs are open for writing by IME: read them with shared access
        static int AddFile(ZipArchive zip, string path, string entryName)
        {
            if (!File.Exists(path)) return 0;
            try
            {
                using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var target = zip.CreateEntry(entryName, CompressionLevel.Optimal).Open()) source.CopyTo(target);
                return 1;
            }
            catch
            {
                return 0;
            }
        }

        static int AddText(ZipArchive zip, string entryName, string text)
        {
            using (var w = new StreamWriter(zip.CreateEntry(entryName, CompressionLevel.Optimal).Open(), new UTF8Encoding(true))) w.Write(text);
            return 1;
        }

        // Semicolon separated: the list separator of Excel with Italian regional settings
        static string Csv(IEnumerable<string[]> rows)
        {
            var sb = new StringBuilder();
            foreach (var row in rows) sb.AppendLine(string.Join(";", row.Select(v => "\"" + (v ?? "").Replace("\"", "\"\"") + "\"")));
            return sb.ToString();
        }

        [DllImport("kernel32.dll")]
        static extern int GetOEMCP();

        static string RunTool(string exe, string arguments)
        {
            try
            {
                // Console tools write in the OEM code page of the system (e.g. 850)
                Encoding oem;
                try { oem = Encoding.GetEncoding(GetOEMCP()); }
                catch { oem = Encoding.Default; }
                var psi = new ProcessStartInfo(exe, arguments)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = oem,
                    StandardErrorEncoding = oem
                };
                using (var p = Process.Start(psi))
                {
                    var err = p.StandardError.ReadToEndAsync();
                    string output = p.StandardOutput.ReadToEnd() + err.Result;
                    p.WaitForExit();
                    return output;
                }
            }
            catch (Exception ex)
            {
                return exe + " " + arguments + " failed: " + ex.Message;
            }
        }

        #endregion
    }
}

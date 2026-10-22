// Dark theme with Windows 11 (Fluent) colors and fonts, and the in-app tutorial.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace IntunePackageGrabber
{
    partial class MainForm
    {
        // Windows 11 fonts, with the Windows 10 ones as fallback
        static readonly string UiFont = FontOr("Segoe UI Variable Text", "Segoe UI");
        static readonly string UiSemibold = FontOr("Segoe UI Variable Text Semibold", "Segoe UI Semibold");
        static readonly string DisplaySemibold = FontOr("Segoe UI Variable Display Semibold", "Segoe UI Semibold");
        static readonly string MonoFont = FontOr("Cascadia Mono", "Consolas");

        // Fluent dark colors
        static readonly Color Background = Color.FromArgb(32, 32, 32);
        static readonly Color CardBack = Color.FromArgb(43, 43, 43);
        static readonly Color ControlBack = Color.FromArgb(55, 55, 55);
        static readonly Color ConsoleBack = Color.FromArgb(28, 28, 28);
        static readonly Color Border = Color.FromArgb(64, 64, 64);
        static readonly Color TextPrimary = Color.White;
        static readonly Color TextMuted = Color.FromArgb(197, 197, 197);
        static readonly Color ConsoleText = Color.FromArgb(212, 212, 212);
        static readonly Color DisabledBack = Color.FromArgb(48, 48, 48);
        static readonly Color DisabledText = Color.FromArgb(158, 158, 158);
        static readonly Color Accent = Color.FromArgb(96, 205, 255);
        static readonly Color HeaderStart = Color.FromArgb(12, 58, 96);
        static readonly Color GreenFill = Color.FromArgb(15, 123, 15);
        static readonly Color RedFill = Color.FromArgb(196, 43, 28);
        static readonly Color GreenColor = Color.FromArgb(108, 203, 95);
        static readonly Color RedColor = Color.FromArgb(255, 153, 164);
        static readonly Color OrangeColor = Color.FromArgb(252, 185, 0);
        static readonly Color IdleColor = Color.FromArgb(138, 138, 138);

        static string FontOr(string name, string fallback)
        {
            // GDI truncates family names to 31 characters; a missing font resolves to another family
            using (var f = new Font(name, 9f)) return name.StartsWith(f.Name, StringComparison.OrdinalIgnoreCase) && f.Name.Length >= Math.Min(name.Length, 31) ? name : fallback;
        }

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr hwnd, string appName, string idList);

        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

        // Runs 'apply' now if the handle exists, and again whenever it is (re)created
        static void OnHandle(Control c, Action apply)
        {
            if (c.IsHandleCreated) apply();
            c.HandleCreated += delegate { apply(); };
        }

        static void UseDarkTitleBar(Form f)
        {
            // DWMWA_USE_IMMERSIVE_DARK_MODE (Windows 10 20H1 and later)
            OnHandle(f, () => { int on = 1; try { DwmSetWindowAttribute(f.Handle, 20, ref on, 4); } catch { } });
        }

        static void UseDarkScrollbars(Control c)
        {
            OnHandle(c, () => { try { SetWindowTheme(c.Handle, "DarkMode_Explorer", null); } catch { } });
        }

        static void UseDarkListView(ListView lv)
        {
            lv.BackColor = CardBack;
            lv.ForeColor = TextPrimary;
            // The themed header keeps dark text: the column headers are drawn here, the rows natively
            lv.OwnerDraw = true;
            lv.DrawItem += (s, e) => e.DrawDefault = true;
            lv.DrawSubItem += (s, e) => e.DrawDefault = true;
            lv.DrawColumnHeader += (s, e) =>
            {
                using (var back = new SolidBrush(CardBack)) e.Graphics.FillRectangle(back, e.Bounds);
                using (var line = new Pen(Border))
                {
                    e.Graphics.DrawLine(line, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
                    e.Graphics.DrawLine(line, e.Bounds.Right - 1, e.Bounds.Top + 4, e.Bounds.Right - 1, e.Bounds.Bottom - 5);
                }
                var text = new Rectangle(e.Bounds.Left + 6, e.Bounds.Top, e.Bounds.Width - 10, e.Bounds.Height);
                TextRenderer.DrawText(e.Graphics, e.Header.Text, lv.Font, text, TextMuted, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            };
            OnHandle(lv, () =>
            {
                try
                {
                    SetWindowTheme(lv.Handle, "DarkMode_Explorer", null);
                    IntPtr header = SendMessage(lv.Handle, 0x101F, IntPtr.Zero, IntPtr.Zero); // LVM_GETHEADER
                    if (header != IntPtr.Zero) SetWindowTheme(header, "DarkMode_ItemsView", null);
                }
                catch { }
            });
        }

        // Subtle button for the header (Tutorial, About & credits)
        Button HeaderButton(string text, int width, EventHandler onClick)
        {
            var b = new Button
            {
                Text = text,
                UseMnemonic = false,
                FlatStyle = FlatStyle.Flat,
                BackColor = Background,
                ForeColor = TextPrimary,
                Font = new Font(UiFont, 9.5f),
                Size = new Size(width, 30),
                Cursor = Cursors.Hand,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            b.FlatAppearance.BorderColor = Border;
            b.FlatAppearance.MouseOverBackColor = ControlBack;
            b.Click += onClick;
            return b;
        }

        // Tab strip in the Windows 11 style: the native TabControl cannot be drawn dark
        class TabStrip : Panel
        {
            readonly Panel header = new Panel { Dock = DockStyle.Top, Height = 40 };
            readonly FlowLayoutPanel strip = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            // Actions valid in every tab, right-aligned next to the titles
            readonly FlowLayoutPanel tools = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.RightToLeft };
            readonly Panel body = new Panel { Dock = DockStyle.Fill };
            readonly List<Button> buttons = new List<Button>();
            readonly List<Control> pages = new List<Control>();
            int selected = -1;

            public event EventHandler SelectedIndexChanged;

            public TabStrip()
            {
                header.Controls.Add(strip);
                header.Controls.Add(tools);
                Controls.Add(body);
                Controls.Add(header);
            }

            public void AddTool(Control c)
            {
                c.Margin = new Padding(6, 2, 0, 0);
                tools.Controls.Add(c);
            }

            public void SetTitle(int index, string title)
            {
                if (buttons[index].Text != title) buttons[index].Text = title;
            }

            public void AddPage(string title, Control content)
            {
                int index = pages.Count;
                var b = new Button
                {
                    Text = title,
                    UseMnemonic = false,
                    FlatStyle = FlatStyle.Flat,
                    AutoSize = true,
                    MinimumSize = new Size(0, 36),
                    Padding = new Padding(10, 0, 10, 0),
                    Margin = new Padding(0, 0, 2, 0),
                    Font = new Font(UiSemibold, 9.5f),
                    ForeColor = TextMuted,
                    BackColor = Background,
                    Cursor = Cursors.Hand
                };
                b.FlatAppearance.BorderSize = 0;
                b.FlatAppearance.MouseOverBackColor = CardBack;
                b.FlatAppearance.MouseDownBackColor = ControlBack;
                b.Click += delegate { SelectedIndex = index; };
                b.Paint += (s, e) =>
                {
                    if (index != selected) return;
                    // Selection indicator: accent bar under the title
                    int w = Math.Max(16, b.Width / 3);
                    using (var brush = new SolidBrush(Accent)) e.Graphics.FillRectangle(brush, (b.Width - w) / 2, b.Height - 3, w, 3);
                };
                content.Dock = DockStyle.Fill;
                content.Visible = false;
                body.Controls.Add(content);
                strip.Controls.Add(b);
                buttons.Add(b);
                pages.Add(content);
                if (selected < 0) SelectedIndex = 0;
            }

            public int SelectedIndex
            {
                get { return selected; }
                set
                {
                    if (value == selected || value < 0 || value >= pages.Count) return;
                    selected = value;
                    for (int i = 0; i < pages.Count; i++)
                    {
                        pages[i].Visible = i == value;
                        buttons[i].ForeColor = i == value ? TextPrimary : TextMuted;
                        buttons[i].Invalidate();
                    }
                    if (SelectedIndexChanged != null) SelectedIndexChanged(this, EventArgs.Empty);
                }
            }
        }

        void ShowTutorial()
        {
            ShowTextViewer("Tutorial", new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("Capture",
                    "Saves a Win32 app while it installs from Company Portal, before the Intune Management Extension (IME) deletes it from C:\\Windows\\IMECache.\n\n" +
                    "1. Choose the archive folder.\n" +
                    "2. In the Capture tab, leave \"Also create the .intunewin file\" checked to rebuild the package for Intune. If the \"Download IntuneWinAppUtil\" button appears, press it once: the tool downloads IntuneWinAppUtil from Microsoft and checks its signature.\n" +
                    "3. Press Listen.\n" +
                    "4. Install the app from Company Portal.\n" +
                    "5. When the status shows \u2714 the package is saved: Content folder, .intunewin, app-info.txt (install/uninstall commands, detection rules, return codes), policy.json and detection.ps1 when the app uses a detection script. Double-click a row to open its folder.\n" +
                    "6. Press Stop when done.\n\n" +
                    "Listen keeps working while you use the other tabs: the Capture title shows \"listening\" and how many new packages were captured.\n\n" +
                    "The .intunewin is rebuilt from the content: it installs the same files, but it is not byte-identical to the original uploaded to Intune."),
                new KeyValuePair<string, string>("Win32 apps",
                    "Lists every Win32 app that IME knows on this device, read from the local IME registry and logs. Nothing is sent to Intune.\n\n" +
                    "Context: Device (installed as SYSTEM) or User.\n" +
                    "Intent: Required, Available or Uninstall; \"dependency\" means it is installed because another app needs it.\n" +
                    "Detected: result of the detection rule. \"Not applicable\" is followed by the reason code (requirements, assignment filters...).\n" +
                    "Last enforcement: result of the last installation attempt.\n" +
                    "Error: IME error code in hexadecimal, to search on Microsoft Learn.\n\n" +
                    "Apps with errors are listed first, in red; apps without a name are at the end. Click a column header to sort, Refresh to read again.\n" +
                    "Double-click an app for details, detection script and policy JSON. Export selected saves app-info.txt, policy.json and detection.ps1 in the archive folder.\n\n" +
                    "Apps without a name: their policy is no longer in the IME logs (old or removed assignments)."),
                new KeyValuePair<string, string>("Remediations",
                    "Lists the remediations on this device (C:\\Windows\\IMECache\\HealthScripts) with their last result.\n\n" +
                    "Detection: exit 0 = no issue; any other exit code = issue found, and the remediation script runs if the policy has one.\n" +
                    "Remediation: exit code of remediate.ps1, or Not run.\n" +
                    "Post-detection: detection after the remediation, Fixed or Issue remains.\n" +
                    "Error: IME error code in hexadecimal.\n\n" +
                    "The names of the remediations are not stored on the device: search the Policy ID in the Intune admin center (Devices > Scripts and remediations).\n" +
                    "Double-click a row to read detect.ps1, remediate.ps1 and the output of the scripts. Export selected saves scripts and result in the archive folder."),
                new KeyValuePair<string, string>("Collect logs",
                    "Collect logs creates IntuneDiagnostics_<computer>_<date>.zip in the archive folder, ready to attach to a ticket:\n\n" +
                    "\u2022 IME and Company Portal logs\n" +
                    "\u2022 IME registry (app and remediation states)\n" +
                    "\u2022 MDM event log (DeviceManagement-Enterprise-Diagnostics-Provider/Admin)\n" +
                    "\u2022 dsregcmd /status (Entra ID join and PRT)\n" +
                    "\u2022 win32-apps.csv and remediations.csv, to open with Excel\n\n" +
                    "The zip contains device and user identifiers: share it only with the support team."),
                new KeyValuePair<string, string>("IntuneWin Downloader",
                    "IntuneWin Downloader, created by Rudy Ooms (call4cloud), downloads and decrypts the Win32 apps assigned to this device directly from Intune, without installing them.\n\n" +
                    "Open About & credits and press Download IntuneWin Downloader: it is downloaded from GitHub on first use (about 85 MB, accepted only if its SHA-256 matches), then the same button opens it.")
            }, true);
        }
    }
}

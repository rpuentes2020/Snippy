using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace SnapMaster
{
    public class MainForm : Form
    {
        const string AppName = "Snippy";
        const string DefaultSaveFolderName = "Snippy";

        CancellationTokenSource recordingCts;
        Thread recordingThread;
        readonly object recordingFramesLock = new object();

        [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(IntPtr h, int id, uint mod, uint vk);
        [DllImport("user32.dll", SetLastError = true)] static extern bool UnregisterHotKey(IntPtr h, int id);
        [DllImport("user32.dll")] static extern void ReleaseCapture();
        [DllImport("user32.dll")] static extern int  SendMessage(IntPtr h, int m, int w, int l);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

        const int SW_RESTORE = 9;

        const int    WM_HOTKEY       = 0x0312;
        const uint   MOD_ALT         = 0x0001;
        const uint   MOD_CTRL        = 0x0002;
        const uint   MOD_SHIFT       = 0x0004;
        const uint   MOD_NOREPEAT    = 0x4000;
        const int    HK_FULLSCREEN   = 1;
        const int    HK_REGION       = 2;
        const int    HK_WINDOW       = 3;
        const int    HK_RECORD_TOGGLE= 4;
        const int    HK_SHOW_APP     = 5;

        static readonly Color BG_DARK  = Color.FromArgb(18,  18,  24);
        static readonly Color BG_PANEL = Color.FromArgb(26,  26,  36);
        static readonly Color BG_CARD  = Color.FromArgb(34,  34,  48);
        static readonly Color ACCENT   = Color.FromArgb(99,  179, 237);
        static readonly Color ACCENT2  = Color.FromArgb(154, 117, 234);
        static readonly Color SUCCESS  = Color.FromArgb(72,  199, 142);
        static readonly Color DANGER   = Color.FromArgb(237, 100, 100);
        static readonly Color TEXT_PRI = Color.FromArgb(240, 240, 255);
        static readonly Color TEXT_SEC = Color.FromArgb(140, 140, 165);
        static readonly Color BORDER   = Color.FromArgb(50,  50,  70);

        string   savePath;
        string   fileFormat          = "PNG";
        int      captureDelay        = 0;
        bool     includeMouseCursor  = false;
        List<CaptureHistoryItem> history = new List<CaptureHistoryItem>();
        System.Windows.Forms.Timer recordTimer;
        bool     isRecording         = false;
        string   recordingFormat     = "AVI";
        Rectangle recordingRegion    = Rectangle.Empty;
        RecordingOutlineForm recordingOutline;
        List<string> recordingFrameFiles = new List<string>();
        string recordingTempDir = null;
        Stopwatch recordingWatch;
        bool isStoppingRecording = false;
        const int RecordingFps = 10;
        NotifyIcon trayIcon;
        bool allowApplicationExit = false;
        bool minimizeToTrayEnabled = true;
        bool showAppHotkeyRegistered = false;
        bool hotkeysRegistered = false;
        bool copyAfterCapture = true;
        bool openEditorAfterCapture = false;

        Panel        headerPanel, sidePanel, contentPanel;
        TableLayoutPanel rootLayout, mainLayout;
        Label        statusLabel, recordTimerLabel;
        Button       btnRecord;
        FlowLayoutPanel historyFlow;
        ComboBox     formatCombo, recordingFormatCombo;
        NumericUpDown delaySpinner;
        CheckBox     cursorCheck, copyAfterCaptureCheck, openAnnotationEditorCheck;
        TextBox      savePathBox;
        Panel        activeNavItem;
        Dictionary<string, Panel> navPanels = new Dictionary<string, Panel>();
        Panel        captureView, historyView, settingsView;

        public MainForm()
        {

            this.Icon = CreateAppIcon();

            savePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                DefaultSaveFolderName);
            Directory.CreateDirectory(savePath);
            InitUI();
            SetupTray();
        }
        void InitUI()
        {
            this.Text            = AppName;
            this.Size            = new Size(940, 660);
            this.MinimumSize     = new Size(820, 560);
            this.StartPosition   = FormStartPosition.CenterScreen;
            this.BackColor       = BG_DARK;
            this.ForeColor       = TEXT_PRI;
            this.Font            = new Font("Segoe UI", 9.5f);
            this.FormBorderStyle = FormBorderStyle.None;
            this.DoubleBuffered  = true;

            this.Paint += (s, e) =>
            {
                using (var pen = new Pen(BORDER, 1))
                    e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            };

            BuildRootLayout();
            BuildHeader();
            BuildMainLayout();
            BuildSidebar();
            BuildContent();

            headerPanel.MouseDown += (s, e) =>
            {
                if (e.Button == MouseButtons.Left)
                { ReleaseCapture(); SendMessage(Handle, 0xA1, 0x2, 0); }
            };
        }

        void BuildRootLayout()
        {
            rootLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = BG_DARK,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                ColumnCount = 1,
                RowCount = 2
            };
            rootLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50f));
            rootLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            this.Controls.Add(rootLayout);
        }

        void BuildHeader()
        {
            headerPanel = new Panel
            {
                Dock      = DockStyle.Fill,
                Margin    = Padding.Empty,
                BackColor = BG_PANEL
            };

            var logo = new PictureBox
            {
                Image = CreateHeaderLogo(),
                SizeMode = PictureBoxSizeMode.Zoom,
                Size = new Size(28, 28),
                Location = new Point(14, 11),
                BackColor = Color.Transparent
            };

            var title = new Label
            {
                Text     = AppName,
                Font     = new Font("Segoe UI Semibold", 13f),
                ForeColor= TEXT_PRI,
                Location = new Point(46, 13),
                AutoSize = true
            };

            statusLabel = new Label
            {
                Text      = "● Ready",
                Font      = new Font("Segoe UI", 8.5f),
                ForeColor = SUCCESS,
                AutoSize  = false,
                Height    = 50,
                TextAlign = ContentAlignment.MiddleRight,
                Anchor    = AnchorStyles.Top | AnchorStyles.Right,
                UseMnemonic = false
            };

            var btnClose = MakeWinCtrl("✕", DANGER);
            var btnMin   = MakeWinCtrl("─", TEXT_SEC);
            btnClose.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnMin.Anchor   = AnchorStyles.Top | AnchorStyles.Right;
            btnClose.Click += (s, e) => HideToTray();
            btnMin.Click   += (s, e) => this.WindowState = FormWindowState.Minimized;

            Action positionRight = () =>
            {
                btnClose.Location = new Point(headerPanel.Width - 38, 11);
                btnMin.Location   = new Point(headerPanel.Width - 68, 11);

                int safeRight = btnMin.Left - 16;
                int minLeftAfterTitle = 230;
                int available = Math.Max(40, safeRight - minLeftAfterTitle);
                int width = Math.Min(460, available);
                statusLabel.SetBounds(safeRight - width, 0, width, 50);

                btnMin.BringToFront();
                btnClose.BringToFront();
            };
            headerPanel.SizeChanged += (s, e) => positionRight();
            headerPanel.Controls.AddRange(new Control[]
                { logo, title, statusLabel, btnMin, btnClose });
            rootLayout.Controls.Add(headerPanel, 0, 0);
            positionRight();
        }

        Icon CreateAppIcon()
        {
            using (var stream = new MemoryStream(Snippy.Properties.Resources.snapmaster))
                return new Icon(stream);
        }

        Image CreateHeaderLogo()
        {
            using (var stream = new MemoryStream(Snippy.Properties.Resources.snapmaster_icon_24x24))
            using (var image = Image.FromStream(stream))
                return new Bitmap(image);
        }

        Button MakeWinCtrl(string text, Color hoverColor)
        {
            var btn = new Button
            {
                Text      = text,
                Size      = new Size(24, 24),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.Transparent,
                ForeColor = TEXT_SEC,
                Font      = new Font("Segoe UI", 9f),
                Cursor    = Cursors.Hand
            };
            btn.FlatAppearance.BorderSize = 0;
            btn.MouseEnter += (s, e) => { btn.ForeColor = hoverColor; btn.BackColor = Color.FromArgb(40, 40, 55); };
            btn.MouseLeave += (s, e) => { btn.ForeColor = TEXT_SEC;   btn.BackColor = Color.Transparent; };
            headerPanel.Controls.Add(btn);
            return btn;
        }

        void BuildMainLayout()
        {
            mainLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = BG_DARK,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                ColumnCount = 2,
                RowCount = 1
            };
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200f));
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            rootLayout.Controls.Add(mainLayout, 0, 1);
        }

        void BuildSidebar()
        {
            sidePanel = new Panel
            {
                Dock      = DockStyle.Fill,
                Margin    = Padding.Empty,
                BackColor = BG_PANEL
            };

            sidePanel.Paint += (s, e) =>
            {
                using (var pen = new Pen(BORDER, 1))
                    e.Graphics.DrawLine(pen, sidePanel.Width - 1, 0,
                                             sidePanel.Width - 1, sidePanel.Height);
            };

            AddNavItem("📷  Capture",  "capture",  8);
            AddNavItem("🗂️  History",  "history",  52);
            AddNavItem("⚙️  Settings", "settings", 96);

            var hkHeader = new Label
            {
                Text     = "HOTKEYS",
                Font     = new Font("Segoe UI", 7.5f, FontStyle.Bold),
                ForeColor= TEXT_SEC,
                Location = new Point(16, 160),
                AutoSize = true
            };
            var hkBody = new Label
            {
                Text     = "Ctrl+Shift+F  Full screen\n" +
                           "Ctrl+Shift+R  Region\n" +
                           "Ctrl+Shift+W  Window\n" +
                           "Ctrl+Shift+V  Rec. toggle",
                Font     = new Font("Consolas", 7.5f),
                ForeColor= TEXT_SEC,
                Location = new Point(12, 180),
                AutoSize = true
            };

            sidePanel.Resize += (s, e) =>
            {
                foreach (var nav in navPanels.Values)
                    nav.Width = sidePanel.ClientSize.Width;
            };

            sidePanel.Controls.AddRange(new Control[] { hkHeader, hkBody });
            mainLayout.Controls.Add(sidePanel, 0, 0);
        }

        void AddNavItem(string label, string key, int top)
        {
            var panel = new Panel
            {
                Location  = new Point(0, top),
                Anchor    = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Size      = new Size(200, 40),
                BackColor = Color.Transparent,
                Cursor    = Cursors.Hand
            };

            var bar = new Panel
            {
                Size      = new Size(3, 40),
                Location  = new Point(0, 0),
                BackColor = Color.Transparent
            };

            var lbl = new Label
            {
                Text     = label,
                Font     = new Font("Segoe UI", 10f),
                ForeColor= TEXT_SEC,
                Location = new Point(16, 10),
                AutoSize = true
            };

            panel.Controls.Add(bar);
            panel.Controls.Add(lbl);

            EventHandler click = (s, e) => ShowView(key);
            panel.Click += click;
            lbl.Click   += click;

            panel.MouseEnter += (s, e) =>
            { if (activeNavItem != panel) { panel.BackColor = Color.FromArgb(32,32,45); lbl.ForeColor = TEXT_PRI; } };
            panel.MouseLeave += (s, e) =>
            { if (activeNavItem != panel) { panel.BackColor = Color.Transparent; lbl.ForeColor = TEXT_SEC; } };

            navPanels[key] = panel;
            sidePanel.Controls.Add(panel);
        }

        void BuildContent()
        {
            contentPanel = new Panel
            {
                Dock      = DockStyle.Fill,
                Margin    = Padding.Empty,
                BackColor = BG_DARK,
                Padding   = new Padding(24, 20, 24, 20)
            };
            mainLayout.Controls.Add(contentPanel, 1, 0);

            BuildCaptureView();
            BuildHistoryView();
            BuildSettingsView();
            ShowView("capture");
        }

        void BuildCaptureView()
        {
            captureView = new Panel
            {
                Dock      = DockStyle.Fill,
                BackColor = Color.Transparent,
                Padding   = new Padding(0)
            };

            var sectionTitle = MakeLabel("Capture Mode", 16f, FontStyle.Bold, TEXT_PRI,
                                         new Point(0, 0));

            var cardFlow = new FlowLayoutPanel
            {
                Location         = new Point(0, 36),
                Size             = new Size(860, 155),
                BackColor        = Color.Transparent,
                FlowDirection    = FlowDirection.LeftToRight,
                WrapContents     = false,
                AutoSize         = false,
                Anchor           = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };

            captureView.SizeChanged += (s, e) =>
            {
                cardFlow.Width = captureView.ClientSize.Width;
            };

            var cardFS  = MakeCaptureCard("🖥",  "Full Screen", "Capture entire desktop");
            var cardReg = MakeCaptureCard("✂",  "Region",      "Draw a selection area");
            var cardWin = MakeCaptureCard("🪟",  "Window",      "Click to pick a window");
            var cardScr = MakeCaptureCard("📜",  "Scrolling",   "Capture scrolling page");

            cardFS.Click  += (s, e) => DoCapture(CaptureMode.FullScreen);
            cardReg.Click += (s, e) => DoCapture(CaptureMode.Region);
            cardWin.Click += (s, e) => DoCapture(CaptureMode.Window);
            cardScr.Click += (s, e) => DoCapture(CaptureMode.Scrolling);

            cardFlow.Controls.AddRange(new Control[] { cardFS, cardReg, cardWin, cardScr });

            var recTitle = MakeLabel("Screen Recording", 13f, FontStyle.Bold, TEXT_PRI,
                                      new Point(0, 208));

            btnRecord = new Button
            {
                Text      = "⏺  Start Recording",
                Size      = new Size(220, 44),
                Location  = new Point(0, 236),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(45, 40, 60),
                ForeColor = TEXT_PRI,
                Font      = new Font("Segoe UI", 10f),
                Cursor    = Cursors.Hand
            };
            btnRecord.FlatAppearance.BorderColor = ACCENT2;
            btnRecord.FlatAppearance.BorderSize  = 1;
            StyleHover(btnRecord, Color.FromArgb(55, 48, 75), Color.FromArgb(45, 40, 60));
            btnRecord.Click += ToggleRecording;

            recordTimerLabel = new Label
            {
                Text     = "",
                Font     = new Font("Consolas", 11f),
                ForeColor= DANGER,
                Location = new Point(232, 247),
                AutoSize = true
            };

            var qsTitle = MakeLabel("Quick Options", 13f, FontStyle.Bold, TEXT_PRI,
                                     new Point(0, 302));

            var delayLabel = MakeLabel("Delay (sec):", 9f, FontStyle.Regular, TEXT_SEC,
                                        new Point(0, 333));
            delaySpinner = new NumericUpDown
            {
                Minimum     = 0, Maximum = 30, Value = captureDelay,
                Location    = new Point(92, 330),
                Size        = new Size(60, 24),
                BackColor   = BG_CARD,
                ForeColor   = TEXT_PRI,
                BorderStyle = BorderStyle.None
            };
            delaySpinner.ValueChanged += (s, e) => captureDelay = (int)delaySpinner.Value;

            cursorCheck = new CheckBox
            {
                Text     = "Include cursor",
                ForeColor= TEXT_SEC,
                Location = new Point(168, 332),
                AutoSize = true,
                Checked  = includeMouseCursor
            };
            cursorCheck.CheckedChanged += (s, e) => includeMouseCursor = cursorCheck.Checked;

            var fmtLabel = MakeLabel("Image format:", 9f, FontStyle.Regular, TEXT_SEC,
                                      new Point(318, 333));
            formatCombo = new ComboBox
            {
                Location      = new Point(410, 329),
                Size          = new Size(85, 24),
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor     = BG_CARD,
                ForeColor     = TEXT_PRI,
                FlatStyle     = FlatStyle.Flat
            };
            formatCombo.Items.AddRange(new[] { "PNG", "JPEG", "BMP", "GIF" });
            formatCombo.SelectedItem = "PNG";
            formatCombo.SelectedIndexChanged += (s, e) =>
                fileFormat = formatCombo.SelectedItem.ToString();

            var recFmtLabel = MakeLabel("Recording format:", 9f, FontStyle.Regular, TEXT_SEC,
                                         new Point(0, 371));
            recordingFormatCombo = new ComboBox
            {
                Location      = new Point(128, 367),
                Size          = new Size(100, 24),
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor     = BG_CARD,
                ForeColor     = TEXT_PRI,
                FlatStyle     = FlatStyle.Flat
            };
            recordingFormatCombo.Items.AddRange(new[] { "AVI", "MP4", "GIF" });
            recordingFormatCombo.SelectedItem = recordingFormat;
            recordingFormatCombo.SelectedIndexChanged += (s, e) =>
                recordingFormat = recordingFormatCombo.SelectedItem.ToString();

            captureView.Controls.AddRange(new Control[]
            {
                sectionTitle, cardFlow,
                recTitle, btnRecord, recordTimerLabel,
                qsTitle, delayLabel, delaySpinner, cursorCheck, fmtLabel, formatCombo,
                recFmtLabel, recordingFormatCombo
            });

            contentPanel.Controls.Add(captureView);
        }

        Panel MakeCaptureCard(string icon, string title, string sub)
        {
            const int W = 175, H = 145;

            var card = new Panel
            {
                Size      = new Size(W, H),
                Margin    = new Padding(0, 0, 10, 0),
                BackColor = BG_CARD,
                Cursor    = Cursors.Hand
            };

            var iconLbl = new Label
            {
                Text     = icon,
                Font     = new Font("Segoe UI Emoji", 22f),
                ForeColor= ACCENT,
                Location = new Point(14, 16),
                AutoSize = true
            };
            var titleLbl = new Label
            {
                Text     = title,
                Font     = new Font("Segoe UI Semibold", 11f),
                ForeColor= TEXT_PRI,
                Location = new Point(14, 62),
                AutoSize = true
            };
            var subLbl = new Label
            {
                Text     = sub,
                Font     = new Font("Segoe UI", 8.5f),
                ForeColor= TEXT_SEC,
                Location = new Point(14, 84),
                Size     = new Size(W - 20, 36)
            };

            card.Controls.AddRange(new Control[] { iconLbl, titleLbl, subLbl });

            card.Paint += (s, e) =>
            {
                using (var pen = new Pen(BORDER, 1))
                    e.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1);
            };

            Action hover  = () => { card.BackColor = Color.FromArgb(42, 42, 60); titleLbl.ForeColor = ACCENT; };
            Action normal = () => { card.BackColor = BG_CARD;                    titleLbl.ForeColor = TEXT_PRI; };

            foreach (Control c in new Control[] { card, iconLbl, titleLbl, subLbl })
            {
                c.MouseEnter += (s, e) => hover();
                c.MouseLeave += (s, e) => normal();
            }

            foreach (Control c in new Control[] { iconLbl, titleLbl, subLbl })
                c.Click += (s, e) => RaiseControlClick(card);

            return card;
        }

        static void RaiseControlClick(Control control)
        {
            if (control == null) return;

            var mi = typeof(Control).GetMethod(
                "OnClick",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            mi?.Invoke(control, new object[] { EventArgs.Empty });
        }

        void BuildHistoryView()
        {
            historyView = new Panel
            {
                Dock      = DockStyle.Fill,
                BackColor = Color.Transparent,
                Visible   = false
            };

            var title    = MakeLabel("Capture History", 16f, FontStyle.Bold, TEXT_PRI, new Point(0, 0));
            var clearBtn = new Button
            {
                Text      = "🗑  Clear All",
                Location  = new Point(600, 0),
                Size      = new Size(110, 28),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.Transparent,
                ForeColor = DANGER,
                Cursor    = Cursors.Hand,
                Anchor    = AnchorStyles.Top | AnchorStyles.Right
            };
            clearBtn.FlatAppearance.BorderColor = DANGER;
            clearBtn.FlatAppearance.BorderSize  = 1;
            clearBtn.Click += (s, e) => { history.Clear(); RefreshHistory(); };

            historyFlow = new FlowLayoutPanel
            {
                Location      = new Point(0, 40),
                BackColor     = Color.Transparent,
                AutoScroll    = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents  = true,
                Anchor        = AnchorStyles.Top | AnchorStyles.Left |
                                AnchorStyles.Right | AnchorStyles.Bottom
            };
            historyView.SizeChanged += (s, e) =>
            {
                clearBtn.Location  = new Point(historyView.ClientSize.Width - 120, 0);
                historyFlow.Size   = new Size(historyView.ClientSize.Width,
                                             historyView.ClientSize.Height - 45);
            };

            historyView.Controls.AddRange(new Control[] { title, clearBtn, historyFlow });
            contentPanel.Controls.Add(historyView);
        }

        void RefreshHistory()
        {
            historyFlow.Controls.Clear();
            var items = new List<CaptureHistoryItem>(history);
            items.Reverse();
            foreach (var item in items)
                historyFlow.Controls.Add(BuildHistoryCard(item));
        }

        Control BuildHistoryCard(CaptureHistoryItem item)
        {
            var card = new Panel
            {
                Size      = new Size(160, 165),
                Margin    = new Padding(6),
                BackColor = BG_CARD,
                Cursor    = Cursors.Hand
            };
            card.Paint += (s, e) =>
            {
                using (var pen = new Pen(BORDER))
                    e.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1);
            };

            var thumb = new PictureBox
            {
                Size     = new Size(160, 110),
                Location = new Point(0, 0),
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor= Color.Black
            };
            try { if (File.Exists(item.FilePath)) thumb.Image = Image.FromFile(item.FilePath); }
            catch { }

            var nameLbl = new Label
            {
                Text      = item.FileName,
                Font      = new Font("Segoe UI", 7.5f),
                ForeColor = TEXT_SEC,
                Location  = new Point(4, 113),
                Size      = new Size(152, 16),
                AutoEllipsis = true
            };
            var timeLbl = new Label
            {
                Text     = item.CapturedAt.ToString("HH:mm:ss"),
                Font     = new Font("Consolas", 7.5f),
                ForeColor= TEXT_SEC,
                Location = new Point(4, 130),
                AutoSize = true
            };

            EventHandler openFile = (s, e) =>
            { try { System.Diagnostics.Process.Start(item.FilePath); } catch { } };
            card.Click  += openFile;
            thumb.Click += openFile;

            Action hov = () => card.BackColor = Color.FromArgb(42, 42, 60);
            Action nor = () => card.BackColor = BG_CARD;
            card.MouseEnter  += (s, e) => hov(); card.MouseLeave  += (s, e) => nor();
            thumb.MouseEnter += (s, e) => hov(); thumb.MouseLeave += (s, e) => nor();

            card.Controls.AddRange(new Control[] { thumb, nameLbl, timeLbl });
            return card;
        }

        void BuildSettingsView()
        {
            settingsView = new Panel
            {
                Dock      = DockStyle.Fill,
                BackColor = Color.Transparent,
                Visible   = false
            };

            int y = 0;
            settingsView.Controls.Add(
                MakeLabel("Settings", 16f, FontStyle.Bold, TEXT_PRI, new Point(0, y)));
            y += 40;

            settingsView.Controls.Add(SettingHeader("Save Location", ref y));
            savePathBox = new TextBox
            {
                Text        = savePath,
                Location    = new Point(0, y),
                Size        = new Size(520, 26),
                BackColor   = BG_CARD,
                ForeColor   = TEXT_PRI,
                BorderStyle = BorderStyle.FixedSingle
            };
            var browseSaveBtn = new Button
            {
                Text      = "Browse",
                Location  = new Point(530, y - 1),
                Size      = new Size(80, 28),
                FlatStyle = FlatStyle.Flat,
                BackColor = BG_CARD,
                ForeColor = ACCENT,
                Cursor    = Cursors.Hand
            };
            browseSaveBtn.FlatAppearance.BorderColor = BORDER;
            browseSaveBtn.Click += (s, e) =>
            {
                using (var fb = new FolderBrowserDialog())
                {
                    fb.SelectedPath = savePath;
                    if (fb.ShowDialog() == DialogResult.OK)
                    { savePath = fb.SelectedPath; savePathBox.Text = savePath; }
                }
            };
            savePathBox.TextChanged += (s, e) =>
            { savePath = savePathBox.Text; try { Directory.CreateDirectory(savePath); } catch { } };
            settingsView.Controls.AddRange(new Control[] { savePathBox, browseSaveBtn });
            y += 36;

            settingsView.Controls.Add(SettingHeader("Default Format", ref y));
            var fmtCombo2 = new ComboBox
            {
                Location      = new Point(0, y),
                Size          = new Size(120, 26),
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor     = BG_CARD,
                ForeColor     = TEXT_PRI,
                FlatStyle     = FlatStyle.Flat
            };
            fmtCombo2.Items.AddRange(new[] { "PNG", "JPEG", "BMP", "GIF" });
            fmtCombo2.SelectedItem = "PNG";
            fmtCombo2.SelectedIndexChanged += (s, e) =>
            {
                fileFormat = fmtCombo2.SelectedItem.ToString();
                if (formatCombo != null) formatCombo.SelectedItem = fileFormat;
            };
            settingsView.Controls.Add(fmtCombo2);
            y += 50;

            settingsView.Controls.Add(SettingHeader("JPEG Quality", ref y));
            var qualityTrack = new TrackBar
            {
                Minimum       = 10, Maximum = 100, Value = 90,
                Location      = new Point(0, y),
                Size          = new Size(260, 30),
                TickFrequency = 10, SmallChange = 5
            };
            var qualityLbl = MakeLabel("90%", 9.5f, FontStyle.Regular, TEXT_PRI,
                                        new Point(270, y + 5));
            qualityTrack.ValueChanged += (s, e) => qualityLbl.Text = qualityTrack.Value + "%";
            settingsView.Controls.AddRange(new Control[] { qualityTrack, qualityLbl });
            y += 50;

            settingsView.Controls.Add(SettingHeader("Behaviour", ref y));

            copyAfterCaptureCheck = new CheckBox
            {
                Text     = "Copy captured image to clipboard automatically",
                ForeColor= TEXT_SEC,
                Location = new Point(0, y),
                AutoSize = true,
                Checked  = copyAfterCapture
            };

            openAnnotationEditorCheck = new CheckBox
            {
                Text     = "Open captured image in annotation editor before saving",
                ForeColor= TEXT_SEC,
                Location = new Point(0, y + 28),
                AutoSize = true,
                Checked  = openEditorAfterCapture
            };

            copyAfterCaptureCheck.CheckedChanged += (s, e) =>
            {
                if (copyAfterCaptureCheck.Checked)
                {
                    copyAfterCapture = true;
                    openEditorAfterCapture = false;
                    if (openAnnotationEditorCheck != null)
                        openAnnotationEditorCheck.Checked = false;
                }
                else if (!openEditorAfterCapture)
                {
                    copyAfterCaptureCheck.Checked = true;
                }
            };

            openAnnotationEditorCheck.CheckedChanged += (s, e) =>
            {
                if (openAnnotationEditorCheck.Checked)
                {
                    openEditorAfterCapture = true;
                    copyAfterCapture = false;
                    if (copyAfterCaptureCheck != null)
                        copyAfterCaptureCheck.Checked = false;
                }
                else if (!copyAfterCapture)
                {
                    openAnnotationEditorCheck.Checked = true;
                }
            };

            settingsView.Controls.AddRange(new Control[] { copyAfterCaptureCheck, openAnnotationEditorCheck });
            y += 64;

            var notifCheck = new CheckBox
            {
                Text     = "Show tray notification after capture",
                ForeColor= TEXT_SEC, Location = new Point(0, y), AutoSize = true, Checked = true
            };
            settingsView.Controls.Add(notifCheck);
            y += 48;

            settingsView.Controls.Add(SettingHeader("Hotkeys", ref y));
            settingsView.Controls.Add(new Label
            {
                Text     = "Ctrl+Shift+F  =  Full Screen Capture\n" +
                           "Ctrl+Shift+R  =  Region Select\n" +
                           "Ctrl+Shift+W  =  Window Capture\n" +
                           "Ctrl+Shift+V  =  Recording Toggle",
                Font     = new Font("Consolas", 9f),
                ForeColor= TEXT_SEC,
                Location = new Point(0, y),
                AutoSize = true
            });

            contentPanel.Controls.Add(settingsView);
        }

        Label SettingHeader(string text, ref int y)
        {
            var lbl = new Label
            {
                Text     = text.ToUpper(),
                Font     = new Font("Segoe UI", 8f, FontStyle.Bold),
                ForeColor= TEXT_SEC,
                Location = new Point(0, y),
                AutoSize = true
            };
            y += 22;
            return lbl;
        }

        void ShowView(string key)
        {
            captureView .Visible = (key == "capture");
            historyView .Visible = (key == "history");
            settingsView.Visible = (key == "settings");

            if (key == "history") RefreshHistory();

            foreach (var kv in navPanels)
            {
                bool active = kv.Key == key;
                var p   = kv.Value;
                var bar = p.Controls[0] as Panel;
                var lbl = p.Controls[1] as Label;
                p.BackColor = active ? Color.FromArgb(30, 30, 45) : Color.Transparent;
                if (bar != null) bar.BackColor = active ? ACCENT   : Color.Transparent;
                if (lbl != null) lbl.ForeColor = active ? ACCENT   : TEXT_SEC;
            }
            activeNavItem = navPanels.ContainsKey(key) ? navPanels[key] : null;
        }

        enum CaptureMode { FullScreen, Region, Window, Scrolling }

        void DoCapture(CaptureMode mode)
        {
            this.Hide();
            int delayMs = Math.Max(200, captureDelay * 1000);
            Thread.Sleep(delayMs);

            Bitmap bmp = null;
            try
            {
                switch (mode)
                {
                    case CaptureMode.FullScreen: bmp = CaptureFullScreen(); break;
                    case CaptureMode.Region:     bmp = CaptureRegion();     break;
                    case CaptureMode.Window:     bmp = CaptureWindow();     break;
                    case CaptureMode.Scrolling: bmp = CaptureScrolling(); break;
                }
            }
            finally { this.Show(); this.BringToFront(); }

            if (bmp != null) SaveAndNotify(bmp, mode.ToString());
        }

        Bitmap CaptureFullScreen()
        {
            if (Screen.AllScreens.Length == 1)
                return CaptureScreen(Screen.AllScreens[0]);

            Screen selectedScreen = null;

            using (var selector = new ScreenClickSelector())
            {
                if (selector.ShowDialog() != DialogResult.OK)
                    return null;

                selectedScreen = selector.SelectedScreen;
            }

            if (selectedScreen == null)
                return null;

            Application.DoEvents();
            Thread.Sleep(200);

            return CaptureScreen(selectedScreen);
        }

        Bitmap CaptureScreen(Screen screen)
        {
            if (screen == null)
                return null;

            var bounds = screen.Bounds;
            var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);

            using (var g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);

                if (includeMouseCursor)
                    DrawCursor(g, bounds.Location);
            }

            return bmp;
        }

        Bitmap CaptureRegion()
        {
            using (var sel = new RegionSelector())
            {
                if (sel.ShowDialog() != DialogResult.OK) return null;
                var r = sel.SelectedRegion;
                if (r.Width < 2 || r.Height < 2) return null;
                var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen(r.Location, Point.Empty, r.Size);
                    if (includeMouseCursor) DrawCursor(g, r.Location);
                }
                return bmp;
            }
        }

        Bitmap CaptureWindow()
        {
            using (var sel = new WindowSelector())
            {
                if (sel.ShowDialog() != DialogResult.OK) return null;
                var r = sel.SelectedRect;
                if (r.Width < 2 || r.Height < 2) return null;
                var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp))
                    g.CopyFromScreen(r.Location, Point.Empty, r.Size);
                return bmp;
            }
        }
        void DrawCursor(Graphics g, Point captureOrigin)
        {
            try
            {
                var ci = new CURSORINFO(); ci.cbSize = Marshal.SizeOf(ci);
                if (GetCursorInfo(out ci) && ci.flags == 1)
                {
                    var cur = new Cursor(ci.hCursor);
                    cur.Draw(g, new Rectangle(
                        ci.ptScreenPos.x - captureOrigin.X - cur.HotSpot.X,
                        ci.ptScreenPos.y - captureOrigin.Y - cur.HotSpot.Y,
                        cur.Size.Width, cur.Size.Height));
                }
            }
            catch { }
        }

        [StructLayout(LayoutKind.Sequential)] struct CPOINT { public int x, y; }
        [StructLayout(LayoutKind.Sequential)] struct CURSORINFO
        { public int cbSize, flags; public IntPtr hCursor; public CPOINT ptScreenPos; }
        [DllImport("user32.dll")] static extern bool GetCursorInfo(out CURSORINFO pci);

        void SaveAndNotify(Bitmap bmp, string tag)
        {
            if (bmp == null)
                return;

            Bitmap imageToSave = null;

            try
            {
                imageToSave = new Bitmap(bmp);

                if (openEditorAfterCapture)
                {
                    using (var editor = new AnnotationEditorForm(imageToSave))
                    {
                        var result = editor.ShowDialog(this);

                        if (result != DialogResult.OK || editor.EditedImage == null)
                        {
                            ShowStatus("Capture canceled — annotation editor was closed.", TEXT_SEC);
                            return;
                        }

                        imageToSave.Dispose();
                        imageToSave = new Bitmap(editor.EditedImage);
                    }
                }

                var ts   = DateTime.Now;
                var name = $"Snap_{ts:yyyy-MM-dd_HH-mm-ss}_{tag}";
                var ext  = fileFormat.ToLower();
                var path = Path.Combine(savePath, name + "." + ext);

                ImageFormat fmt = ImageFormat.Png;
                if (fileFormat == "JPEG") fmt = ImageFormat.Jpeg;
                else if (fileFormat == "BMP") fmt = ImageFormat.Bmp;
                else if (fileFormat == "GIF") fmt = ImageFormat.Gif;

                imageToSave.Save(path, fmt);

                if (copyAfterCapture)
                {
                    try { Clipboard.SetImage(imageToSave); } catch { }
                }

                history.Add(new CaptureHistoryItem
                    { FilePath = path, FileName = name + "." + ext, CapturedAt = ts });

                ShowStatus($"✔  Saved: {name}.{ext}", SUCCESS);
                trayIcon?.ShowBalloonTip(2000, AppName, $"Saved {name}.{ext}", ToolTipIcon.None);
            }
            finally
            {
                imageToSave?.Dispose();
            }
        }

        void ToggleRecording(object sender, EventArgs e)
        {
            if (isStoppingRecording) return;
            if (!isRecording) StartRecording(); else RequestStopRecording();
        }

        void StartRecording()
        {
            if (isRecording || isStoppingRecording) return;

            using (var sel = new RegionSelector())
            {
                Hide();
                Thread.Sleep(150);
                var result = sel.ShowDialog();
                Show();
                Activate();

                if (result != DialogResult.OK || sel.SelectedRegion.Width < 8 || sel.SelectedRegion.Height < 8)
                {
                    ShowStatus("Recording canceled — no region selected.", TEXT_SEC);
                    return;
                }

                recordingRegion = sel.SelectedRegion;
            }

            recordingTempDir = Path.Combine(Path.GetTempPath(), "Snippy_Record_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(recordingTempDir);
            recordingFrameFiles.Clear();
            isRecording = true;
            isStoppingRecording = false;
            recordingWatch = Stopwatch.StartNew();

            recordingOutline = new RecordingOutlineForm(recordingRegion);
            recordingOutline.Show();

            btnRecord.Enabled = true;
            btnRecord.Text      = "⏹  Stop Recording";
            btnRecord.BackColor = Color.FromArgb(60, 30, 30);
            btnRecord.FlatAppearance.BorderColor = DANGER;

            recordTimer = new System.Windows.Forms.Timer { Interval = 250 };
            recordTimer.Tick += (s, e) =>
            {
                var elapsed = recordingWatch != null ? recordingWatch.Elapsed : TimeSpan.Zero;
                recordTimerLabel.Text = $"⏺ {elapsed.Hours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}";
            };
            recordTimer.Start();

            recordingCts = new CancellationTokenSource();

            recordingThread = new Thread(() => RecordingCaptureLoop(recordingCts.Token))
            {
                IsBackground = true,
                Name = "Snippy Recording Capture"
            };

            recordingThread.Start();

            ShowStatus($"⏺ Recording {recordingRegion.Width}×{recordingRegion.Height} as {recordingFormat}...", DANGER);
        }

        void RecordingCaptureLoop(CancellationToken token)
        {
            int frameDelay = 1000 / RecordingFps;

            try
            {
                while (!token.IsCancellationRequested)
                {
                    var frameWatch = Stopwatch.StartNew();

                    CaptureRecordingFrameToDisk();

                    if (recordingWatch != null && recordingWatch.Elapsed.TotalMinutes >= 10)
                    {
                        BeginInvoke(new Action(RequestStopRecording));
                        break;
                    }

                    int remainingDelay = frameDelay - (int)frameWatch.ElapsedMilliseconds;

                    if (remainingDelay > 0)
                    {
                        try
                        {
                            token.WaitHandle.WaitOne(remainingDelay);
                        }
                        catch (ObjectDisposedException)
                        {
                            break;
                        }
                    }
                }
            }
            finally
            {
            }
        }

        void CaptureRecordingFrameToDisk()
        {
            if (!isRecording || isStoppingRecording) return;
            if (recordingRegion.Width <= 0 || recordingRegion.Height <= 0) return;

            try
            {
                using (var bmp = new Bitmap(recordingRegion.Width, recordingRegion.Height, PixelFormat.Format24bppRgb))
                {
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.CopyFromScreen(
                            recordingRegion.Location,
                            Point.Empty,
                            recordingRegion.Size,
                            CopyPixelOperation.SourceCopy);
                    }

                    string framePath;

                    lock (recordingFramesLock)
                    {
                        framePath = Path.Combine(
                            recordingTempDir,
                            $"frame_{recordingFrameFiles.Count:D06}.bmp");

                        bmp.Save(framePath, ImageFormat.Bmp);
                        recordingFrameFiles.Add(framePath);
                    }
                }
            }
            catch (Exception ex)
            {
                BeginInvoke(new Action(() =>
                    ShowStatus("Recording frame failed: " + ex.Message, DANGER)));
            }
        }

        void RequestStopRecording()
        {
            if (!isRecording || isStoppingRecording) return;

            isStoppingRecording = true;
            isRecording = false;
            recordingCts?.Cancel();
            recordTimer?.Stop();
            recordTimer?.Dispose();
            recordTimer = null;

            recordingWatch?.Stop();

            if (recordingOutline != null)
            {
                recordingOutline.Close();
                recordingOutline.Dispose();
                recordingOutline = null;
            }

            btnRecord.Enabled = false;
            btnRecord.Text = "💾  Saving...";
            recordTimerLabel.Text = "";
            ShowStatus($"💾 Saving {recordingFormat}...", ACCENT);

            List<string> frames;
            lock (recordingFramesLock)
            {
                frames = new List<string>(recordingFrameFiles);
            }

            string tempDir = recordingTempDir;
            string format = recordingFormat;
            string outDir = savePath;
            DateTime ts = DateTime.Now;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                string savedPath = null;
                Exception error = null;

                try
                {
                    if (frames.Count == 0)
                        throw new InvalidOperationException("No frames were captured.");

                    savedPath = SaveRecordingFromFrames(frames, outDir, ts, format);
                }
                catch (Exception ex)
                {
                    error = ex;
                }
                finally
                {
                    try
                    {
                        if (!string.IsNullOrEmpty(tempDir) && Directory.Exists(tempDir))
                            Directory.Delete(tempDir, true);
                    }
                    catch { }
                }

                BeginInvoke(new Action(() => CompleteStopRecording(savedPath, error, ts)));
            });
        }
        
        
        void CompleteStopRecording(string savedPath, Exception error, DateTime ts)
        {
            btnRecord.Enabled = true;
            btnRecord.Text = "⏺  Start Recording";
            btnRecord.BackColor = Color.FromArgb(45, 40, 60);
            btnRecord.FlatAppearance.BorderColor = ACCENT2;

            recordingFrameFiles.Clear();
            recordingTempDir = null;

            recordingCts?.Dispose();
            recordingCts = null;
            recordingThread = null;

            isStoppingRecording = false;

            if (error != null)
            {
                ShowStatus("Could not save recording: " + error.Message, DANGER);
                return;
            }

            history.Add(new CaptureHistoryItem
                { FilePath = savedPath, FileName = Path.GetFileName(savedPath), CapturedAt = ts });
            ShowStatus($"✔ Recording saved: {Path.GetFileName(savedPath)}", SUCCESS);
            trayIcon?.ShowBalloonTip(1500, AppName, $"Saved {Path.GetFileName(savedPath)}", ToolTipIcon.None);
        }

        string SaveRecordingFromFrames(List<string> frameFiles, string outDir, DateTime ts, string requestedFormat)
        {
            Directory.CreateDirectory(outDir);

            if (frameFiles == null || frameFiles.Count == 0)
                throw new InvalidOperationException("No frames were captured.");

            string ffmpeg = FindFfmpeg();
            if (ffmpeg == null)
                throw new InvalidOperationException("ffmpeg.exe was not found. Place ffmpeg.exe next to the app .exe or add it to PATH.");

            string format = requestedFormat.ToUpperInvariant();
            string ext = format.ToLowerInvariant();
            string outputPath = Path.Combine(outDir, $"Rec_{ts:yyyy-MM-dd_HH-mm-ss}.{ext}");
            string inputPattern = Path.Combine(Path.GetDirectoryName(frameFiles[0]), "frame_%06d.bmp");

            string evenScale = "scale=trunc(iw/2)*2:trunc(ih/2)*2";

            if (format == "MP4")
            {
                RunFfmpeg(ffmpeg,
                    $"-hide_banner -loglevel error -y -framerate {RecordingFps} " +
                    $"-i \"{inputPattern}\" -vf \"{evenScale}\" " +
                    $"-c:v libx264 -preset veryfast -crf 23 -pix_fmt yuv420p " +
                    $"\"{outputPath}\"");

                return outputPath;
            }

            if (format == "AVI")
            {
                RunFfmpeg(ffmpeg,
                    $"-hide_banner -loglevel error -y -framerate {RecordingFps} " +
                    $"-i \"{inputPattern}\" -vf \"{evenScale}\" " +
                    $"-c:v mjpeg -q:v 5 " +
                    $"\"{outputPath}\"");

                return outputPath;
            }

            if (format == "GIF")
            {
                RunFfmpeg(ffmpeg,
                    $"-hide_banner -loglevel error -y -framerate {RecordingFps} " +
                    $"-i \"{inputPattern}\" -vf \"fps={RecordingFps},{evenScale}:flags=lanczos\" " +
                    $"\"{outputPath}\"");

                return outputPath;
            }

            throw new InvalidOperationException("Unsupported recording format: " + requestedFormat);
        }

        void RunFfmpeg(string ffmpeg, string args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };

            using (var p = Process.Start(psi))
            {
                string stdout = p.StandardOutput.ReadToEnd();
                string stderr = p.StandardError.ReadToEnd();

                p.WaitForExit();

                if (p.ExitCode != 0)
                {
                    string logPath = Path.Combine(savePath, "ffmpeg-error-log.txt");
                    File.WriteAllText(logPath,
                        "FFMPEG:\r\n" + ffmpeg +
                        "\r\n\r\nARGS:\r\n" + args +
                        "\r\n\r\nSTDOUT:\r\n" + stdout +
                        "\r\n\r\nSTDERR:\r\n" + stderr);

                    throw new Exception("ffmpeg failed. See ffmpeg-error-log.txt in your save folder.");
                }
            }
        }

        string FindFfmpeg()
        {
            string local = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe");
            if (File.Exists(local)) return local;

            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in path.Split(Path.PathSeparator))
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    string candidate = Path.Combine(dir.Trim(), "ffmpeg.exe");
                    if (File.Exists(candidate)) return candidate;
                }
                catch { }
            }
            return null;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            RegisterHotKeys();
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            UnregisterHotKeys();
            base.OnHandleDestroyed(e);
        }

        void RegisterHotKeys()
        {
            if (hotkeysRegistered || Handle == IntPtr.Zero)
                return;

            bool okFull = RegisterOneHotKey(HK_FULLSCREEN, MOD_CTRL | MOD_SHIFT | MOD_NOREPEAT, (uint)Keys.F, "Ctrl+Shift+F");
            bool okRegion = RegisterOneHotKey(HK_REGION, MOD_CTRL | MOD_SHIFT | MOD_NOREPEAT, (uint)Keys.R, "Ctrl+Shift+R");
            bool okWindow = RegisterOneHotKey(HK_WINDOW, MOD_CTRL | MOD_SHIFT | MOD_NOREPEAT, (uint)Keys.W, "Ctrl+Shift+W");
            bool okRecord = RegisterOneHotKey(HK_RECORD_TOGGLE, MOD_CTRL | MOD_SHIFT | MOD_NOREPEAT, (uint)Keys.V, "Ctrl+Shift+V");

            showAppHotkeyRegistered = RegisterOneHotKey(HK_SHOW_APP, MOD_CTRL | MOD_ALT | MOD_NOREPEAT, (uint)Keys.S, "Ctrl+Alt+S");

            hotkeysRegistered = true;

            if (!(okFull && okRegion && okWindow && okRecord && showAppHotkeyRegistered))
            {
                BeginInvoke(new Action(() =>
                {
                    ShowStatus("One or more hotkeys could not be registered. Check tray notification.", DANGER);
                    trayIcon?.ShowBalloonTip(4000,
                        AppName + " hotkey issue",
                        "One or more keyboard shortcuts are already used by another app or were rejected by Windows.",
                        ToolTipIcon.Warning);
                }));
            }
        }

        bool RegisterOneHotKey(int id, uint modifiers, uint key, string label)
        {
            bool registered = RegisterHotKey(Handle, id, modifiers, key);

            if (!registered)
            {
                int error = Marshal.GetLastWin32Error();
                Debug.WriteLine($"Snippy failed to register {label}. Win32 error: {error}");
            }

            return registered;
        }

        void UnregisterHotKeys()
        {
            if (!hotkeysRegistered || Handle == IntPtr.Zero)
                return;

            UnregisterHotKey(Handle, HK_FULLSCREEN);
            UnregisterHotKey(Handle, HK_REGION);
            UnregisterHotKey(Handle, HK_WINDOW);
            UnregisterHotKey(Handle, HK_RECORD_TOGGLE);
            UnregisterHotKey(Handle, HK_SHOW_APP);

            hotkeysRegistered = false;
            showAppHotkeyRegistered = false;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY)
            {
                switch (m.WParam.ToInt32())
                {
                    case HK_FULLSCREEN:    DoCapture(CaptureMode.FullScreen); break;
                    case HK_REGION:        DoCapture(CaptureMode.Region);     break;
                    case HK_WINDOW:        DoCapture(CaptureMode.Window);     break;
                    case HK_RECORD_TOGGLE: ToggleRecording(null, null);       break;
                    case HK_SHOW_APP:       ShowFromTray();                    break;
                }

                return;
            }

            base.WndProc(ref m);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            UnregisterHotKeys();
            base.OnFormClosed(e);
        }

        void SetupTray()
        {

            trayIcon = new NotifyIcon
            {
                Text = AppName + " - Ctrl+Alt+S to show",
                Icon = CreateAppIcon(),
                Visible = true
            };

            var menu = new ContextMenuStrip();
            menu.Items.Add("Open " + AppName + " (Ctrl+Alt+S)", null, (s, e) => ShowFromTray());
            menu.Items.Add("Hide to Tray", null, (s, e) => HideToTray());
            menu.Items.Add("Full Screen Capture", null, (s, e) => DoCapture(CaptureMode.FullScreen));
            menu.Items.Add("Region Capture", null, (s, e) => DoCapture(CaptureMode.Region));
            menu.Items.Add("Window Capture", null, (s, e) => DoCapture(CaptureMode.Window));
            menu.Items.Add("-");
            menu.Items.Add("Exit", null, (s, e) => ExitApplication());

            trayIcon.ContextMenuStrip = menu;
            trayIcon.DoubleClick += (s, e) => ShowFromTray();
            trayIcon.BalloonTipClicked += (s, e) => ShowFromTray();
        }

        void ShowFromTray()
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(ShowFromTray));
                return;
            }

            ShowInTaskbar = true;
            Show();

            if (WindowState == FormWindowState.Minimized)
                WindowState = FormWindowState.Normal;

            ShowWindowAsync(Handle, SW_RESTORE);
            Activate();
            BringToFront();
            Focus();
            SetForegroundWindow(Handle);

            ShowStatus(showAppHotkeyRegistered
                ? "● Ready — Ctrl+Alt+S shows " + AppName + " from the tray"
                : "● Ready — Ctrl+Alt+S is unavailable on this PC",
                showAppHotkeyRegistered ? SUCCESS : DANGER);
        }

        void HideToTray()
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(HideToTray));
                return;
            }

            Hide();
            ShowInTaskbar = false;

            if (showAppHotkeyRegistered)
            {
                trayIcon?.ShowBalloonTip(1600,
                    AppName + " is still running",
                    "Press Ctrl+Alt+S to reopen, or use the tray icon.",
                    ToolTipIcon.Info);
            }
        }

        void ExitApplication()
        {
            allowApplicationExit = true;
            trayIcon?.Dispose();
            Application.Exit();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!allowApplicationExit && minimizeToTrayEnabled && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }

            base.OnFormClosing(e);
        }

        void ShowStatus(string msg, Color color)
        {
            if (statusLabel.InvokeRequired)
                statusLabel.Invoke(new Action(() => { statusLabel.Text = msg; statusLabel.ForeColor = color; }));
            else { statusLabel.Text = msg; statusLabel.ForeColor = color; }
        }

        Label MakeLabel(string text, float size, FontStyle style, Color color, Point loc) =>
            new Label { Text = text, Font = new Font("Segoe UI", size, style),
                        ForeColor = color, Location = loc, AutoSize = true };

        void StyleHover(Button btn, Color hoverBg, Color normalBg)
        {
            btn.MouseEnter += (s, e) => btn.BackColor = hoverBg;
            btn.MouseLeave += (s, e) => btn.BackColor = normalBg;
        }
        Bitmap CaptureScrolling()
        {
            Rectangle region;

            using (var sel = new RegionSelector())
            {
                if (sel.ShowDialog() != DialogResult.OK)
                    return null;

                region = sel.SelectedRegion;

                if (region.Width < 8 || region.Height < 8)
                    return null;
            }

            using (var session = new AutoScrollCaptureSession(region, includeMouseCursor))
            {
                if (session.ShowDialog() != DialogResult.OK)
                    return null;

                return session.GetFinalImage();
            }
        }

    }

    class CaptureHistoryItem
    {
        public string   FilePath    { get; set; }
        public string   FileName    { get; set; }
        public DateTime CapturedAt  { get; set; }
    }

    class RegionSelector : Form
    {
        public Rectangle SelectedRegion;
        Point   startPt;
        bool    dragging;
        Rectangle currentRect;

        static readonly Color ACC = Color.FromArgb(99, 179, 237);

        public RegionSelector()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition   = FormStartPosition.Manual;
            Bounds          = SystemInformation.VirtualScreen;
            TopMost         = true;
            Opacity         = 0.35;
            BackColor       = Color.Black;
            Cursor          = Cursors.Cross;
            DoubleBuffered  = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }

        protected override void OnMouseDown(MouseEventArgs e) { startPt = e.Location; dragging = true; }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (!dragging) return;
            int x = Math.Min(startPt.X, e.X), y = Math.Min(startPt.Y, e.Y);
            currentRect = new Rectangle(x, y, Math.Abs(startPt.X - e.X), Math.Abs(startPt.Y - e.Y));
            Invalidate();
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            dragging = false;
            SelectedRegion = new Rectangle(Left + currentRect.Left, Top + currentRect.Top,
                                            currentRect.Width, currentRect.Height);
            DialogResult = DialogResult.OK;
            Close();
        }
        protected override void OnKeyDown(KeyEventArgs e)
        { if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); } }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (currentRect.Width < 1 || currentRect.Height < 1) return;

            e.Graphics.SetClip(currentRect);
            e.Graphics.Clear(Color.FromArgb(1, 1, 1));
            e.Graphics.ResetClip();

            using (var pen = new Pen(ACC, 2))
                e.Graphics.DrawRectangle(pen, currentRect);

            string hint = $"{currentRect.Width} × {currentRect.Height}";
            using (var f = new Font("Consolas", 11))
            using (var b = new SolidBrush(ACC))
                e.Graphics.DrawString(hint, f, b, currentRect.X + 4,
                                       Math.Max(0, currentRect.Y - 22));
        }
    }

    class WindowSelector : Form
    {
        public Rectangle SelectedRect;

        [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(WP p);
        [DllImport("user32.dll")] static extern bool   GetWindowRect(IntPtr h, out WR r);
        [StructLayout(LayoutKind.Sequential)] struct WP { public int x, y; }
        [StructLayout(LayoutKind.Sequential)] struct WR { public int L, T, R, B; }

        Rectangle highlighted;
        static readonly Color ACC = Color.FromArgb(99, 179, 237);

        public WindowSelector()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition   = FormStartPosition.Manual;
            Bounds          = SystemInformation.VirtualScreen;
            TopMost         = true;
            Opacity         = 0.20;
            BackColor       = Color.Black;
            Cursor          = Cursors.Hand;
            DoubleBuffered  = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            var pt = new WP { x = e.X + Left, y = e.Y + Top };
            if (GetWindowRect(WindowFromPoint(pt), out WR r))
            {
                highlighted = Rectangle.FromLTRB(r.L - Left, r.T - Top, r.R - Left, r.B - Top);
                Invalidate();
            }
        }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            var screenPoint = new WP { x = e.X + Left, y = e.Y + Top };

            Hide();
            Application.DoEvents();
            Thread.Sleep(100);

            IntPtr hwnd = WindowFromPoint(screenPoint);

            if (hwnd != IntPtr.Zero && GetWindowRect(hwnd, out WR r))
            {
                SelectedRect = Rectangle.FromLTRB(r.L, r.T, r.R, r.B);
                DialogResult = DialogResult.OK;
            }
            else
            {
                DialogResult = DialogResult.Cancel;
            }

            Close();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        { if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); } }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (highlighted.Width > 0)
                using (var pen = new Pen(ACC, 3))
                    e.Graphics.DrawRectangle(pen, highlighted);
        }
    }


    class RecordingOutlineForm : Form
    {
        readonly Rectangle region;
        static readonly Color DANGER = Color.FromArgb(237, 100, 100);

        public RecordingOutlineForm(Rectangle recordingRegion)
        {
            region = recordingRegion;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition   = FormStartPosition.Manual;
            Bounds          = recordingRegion;
            TopMost         = true;
            ShowInTaskbar   = false;
            BackColor       = Color.Magenta;
            TransparencyKey = Color.Magenta;
            DoubleBuffered  = true;
            Enabled         = false;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                const int WS_EX_TOOLWINDOW = 0x00000080;
                const int WS_EX_TRANSPARENT = 0x00000020;
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT;
                return cp;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(DANGER, 3))
                e.Graphics.DrawRectangle(pen, 1, 1, Width - 3, Height - 3);
        }
    }

    class DibAviWriter
    {
        struct IndexEntry { public uint Offset; public uint Size; }

        public void Build(List<string> frameFiles, string outputPath, int fps)
        {
            if (frameFiles == null || frameFiles.Count == 0)
                throw new InvalidOperationException("No frames were captured.");

            using (var first = new Bitmap(frameFiles[0]))
            {
                int w = first.Width;
                int h = first.Height;
                int stride = ((w * 3 + 3) / 4) * 4;
                int frameSize = stride * h;
                var index = new List<IndexEntry>();

                using (var fs = new FileStream(outputPath, FileMode.Create, FileAccess.ReadWrite))
                using (var bw = new BinaryWriter(fs))
                {
                    W4(bw, "RIFF"); long riffSize = fs.Position; bw.Write(0); W4(bw, "AVI ");

                    W4(bw, "LIST"); long hdrlSize = fs.Position; bw.Write(0); W4(bw, "hdrl");

                    W4(bw, "avih"); bw.Write(56);
                    bw.Write(1000000 / fps);              // dwMicroSecPerFrame
                    bw.Write(frameSize * fps);            // dwMaxBytesPerSec
                    bw.Write(0);                          // dwPaddingGranularity
                    bw.Write(0x10);                       // AVIF_HASINDEX
                    bw.Write(frameFiles.Count);            // dwTotalFrames
                    bw.Write(0);                          // dwInitialFrames
                    bw.Write(1);                          // dwStreams
                    bw.Write(frameSize);                  // dwSuggestedBufferSize
                    bw.Write(w); bw.Write(h);
                    for (int i = 0; i < 4; i++) bw.Write(0);

                    W4(bw, "LIST"); long strlSize = fs.Position; bw.Write(0); W4(bw, "strl");

                    W4(bw, "strh"); bw.Write(56);
                    W4(bw, "vids");
                    W4(bw, "DIB ");
                    bw.Write(0);                          // dwFlags
                    bw.Write((short)0);                   // wPriority
                    bw.Write((short)0);                   // wLanguage
                    bw.Write(0);                          // dwInitialFrames
                    bw.Write(1);                          // dwScale
                    bw.Write(fps);                        // dwRate
                    bw.Write(0);                          // dwStart
                    bw.Write(frameFiles.Count);            // dwLength
                    bw.Write(frameSize);                  // dwSuggestedBufferSize
                    bw.Write(-1);                         // dwQuality
                    bw.Write(0);                          // dwSampleSize
                    bw.Write(0); bw.Write(0); bw.Write(w); bw.Write(h); // rcFrame

                    W4(bw, "strf"); bw.Write(40);
                    bw.Write(40);                         // biSize
                    bw.Write(w); bw.Write(h);
                    bw.Write((short)1);                   // biPlanes
                    bw.Write((short)24);                  // biBitCount
                    bw.Write(0);                          // BI_RGB
                    bw.Write(frameSize);                  // biSizeImage
                    bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);

                    PatchSize(fs, strlSize);
                    PatchSize(fs, hdrlSize);

                    W4(bw, "LIST"); long moviSize = fs.Position; bw.Write(0); W4(bw, "movi");
                    long moviListDataStart = fs.Position;

                    foreach (var file in frameFiles)
                    {
                        using (var src = new Bitmap(file))
                        using (var frame = EnsureSize(src, w, h))
                        {
                            long chunkStart = fs.Position;
                            W4(bw, "00db");
                            bw.Write(frameSize);
                            WriteDibFrame(frame, bw, stride);
                            if ((frameSize & 1) == 1) bw.Write((byte)0);
                            index.Add(new IndexEntry
                            {
                                Offset = (uint)(chunkStart - moviListDataStart),
                                Size = (uint)frameSize
                            });
                        }
                    }

                    PatchSize(fs, moviSize);

                    W4(bw, "idx1"); bw.Write(index.Count * 16);
                    foreach (var entry in index)
                    {
                        W4(bw, "00db");
                        bw.Write(0x10);
                        bw.Write(entry.Offset);
                        bw.Write(entry.Size);
                    }

                    PatchSize(fs, riffSize);
                }
            }
        }

        static Bitmap EnsureSize(Bitmap source, int w, int h)
        {
            if (source.Width == w && source.Height == h) return new Bitmap(source);
            var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(bmp)) g.DrawImage(source, 0, 0, w, h);
            return bmp;
        }

        static void WriteDibFrame(Bitmap frame, BinaryWriter bw, int stride)
        {
            int w = frame.Width;
            int h = frame.Height;
            byte[] row = new byte[stride];

            // Positive-height DIBs are stored bottom-up.
            for (int y = h - 1; y >= 0; y--)
            {
                Array.Clear(row, 0, row.Length);
                for (int x = 0; x < w; x++)
                {
                    Color c = frame.GetPixel(x, y);
                    int i = x * 3;
                    row[i] = c.B;
                    row[i + 1] = c.G;
                    row[i + 2] = c.R;
                }
                bw.Write(row);
            }
        }

        static void W4(BinaryWriter bw, string s) => bw.Write(Encoding.ASCII.GetBytes(s));

        static void PatchSize(FileStream fs, long sizePos)
        {
            long cur = fs.Position;
            fs.Position = sizePos;
            using (var bw = new BinaryWriter(fs, Encoding.ASCII, true))
                bw.Write((int)(cur - sizePos - 4));
            fs.Position = cur;
        }
    }

    class ScreenClickSelector : Form
    {
        public Screen SelectedScreen { get; private set; }

        static readonly Color ACCENT = Color.FromArgb(99, 179, 237);
        static readonly Color TEXT = Color.White;
        static readonly Color BACKDROP = Color.FromArgb(95, 0, 0, 0);

        public ScreenClickSelector()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = SystemInformation.VirtualScreen;
            TopMost = true;
            ShowInTaskbar = false;
            BackColor = Color.Black;
            Opacity = 0.88;
            Cursor = Cursors.Hand;
            DoubleBuffered = true;
            KeyPreview = true;

            Text = "Click a screen to capture";
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Activate();
            BringToFront();
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            var screenPoint = new Point(Left + e.X, Top + e.Y);
            SelectedScreen = Screen.FromPoint(screenPoint);

            Hide();
            DialogResult = DialogResult.OK;
            Close();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                Hide();
                DialogResult = DialogResult.Cancel;
                Close();
            }

            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            using (var backdrop = new SolidBrush(BACKDROP))
                e.Graphics.FillRectangle(backdrop, ClientRectangle);

            for (int i = 0; i < Screen.AllScreens.Length; i++)
            {
                var screen = Screen.AllScreens[i];
                var local = new Rectangle(
                    screen.Bounds.Left - Left,
                    screen.Bounds.Top - Top,
                    screen.Bounds.Width,
                    screen.Bounds.Height);

                DrawScreenCard(e.Graphics, local, i + 1, screen.Primary);
            }
        }

        void DrawScreenCard(Graphics g, Rectangle bounds, int number, bool primary)
        {
            int boxSize = Math.Min(170, Math.Max(100, Math.Min(bounds.Width, bounds.Height) / 5));
            int boxX = bounds.Left + (bounds.Width - boxSize) / 2;
            int boxY = bounds.Top + (bounds.Height - boxSize) / 2;
            var box = new Rectangle(boxX, boxY, boxSize, boxSize);

            using (var shadow = new SolidBrush(Color.FromArgb(130, 0, 0, 0)))
                g.FillEllipse(shadow, boxX + 8, boxY + 8, boxSize, boxSize);

            using (var fill = new SolidBrush(Color.FromArgb(235, 26, 26, 36)))
                g.FillEllipse(fill, box);

            using (var pen = new Pen(ACCENT, 5))
                g.DrawEllipse(pen, box);

            using (var font = new Font("Segoe UI Semibold", boxSize * 0.45f, FontStyle.Bold))
            using (var brush = new SolidBrush(TEXT))
            {
                string text = number.ToString();
                SizeF textSize = g.MeasureString(text, font);
                float tx = boxX + (boxSize - textSize.Width) / 2f;
                float ty = boxY + (boxSize - textSize.Height) / 2f - 3;
                g.DrawString(text, font, brush, tx, ty);
            }

            string label = primary ? "Click to capture this screen (Primary)" : "Click to capture this screen";
            using (var font = new Font("Segoe UI Semibold", 13f, FontStyle.Bold))
            using (var brush = new SolidBrush(TEXT))
            using (var bg = new SolidBrush(Color.FromArgb(170, 0, 0, 0)))
            {
                SizeF labelSize = g.MeasureString(label, font);
                var labelRect = new RectangleF(
                    bounds.Left + (bounds.Width - labelSize.Width) / 2f - 16,
                    box.Bottom + 18,
                    labelSize.Width + 32,
                    labelSize.Height + 12);

                g.FillRectangle(bg, labelRect);
                g.DrawString(label, font, brush,
                    labelRect.Left + 16,
                    labelRect.Top + 6);
            }

            using (var pen = new Pen(Color.FromArgb(160, 255, 255, 255), 2))
                g.DrawRectangle(pen, bounds.Left + 2, bounds.Top + 2, bounds.Width - 4, bounds.Height - 4);
        }
    }

    class ScreenSelector : Form
    {
        public Screen SelectedScreen { get; private set; }

        static readonly Color BG = Color.FromArgb(18, 18, 24);
        static readonly Color CARD = Color.FromArgb(34, 34, 48);
        static readonly Color ACCENT = Color.FromArgb(99, 179, 237);
        static readonly Color TEXT = Color.FromArgb(240, 240, 255);
        static readonly Color TEXT_SEC = Color.FromArgb(140, 140, 165);

        public ScreenSelector()
        {
            Text = "Select Screen";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = BG;
            ForeColor = TEXT;
            Width = 420;
            Height = 150 + (Screen.AllScreens.Length * 58);
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;

            var title = new Label
            {
                Text = "Choose a screen to capture",
                Font = new Font("Segoe UI Semibold", 12f),
                ForeColor = TEXT,
                Location = new Point(16, 14),
                AutoSize = true
            };

            Controls.Add(title);

            int y = 52;

            for (int i = 0; i < Screen.AllScreens.Length; i++)
            {
                var screen = Screen.AllScreens[i];
                var btn = new Button
                {
                    Text = $"Screen {i + 1} {(screen.Primary ? "(Primary)" : "")}\n{screen.Bounds.Width} × {screen.Bounds.Height}  |  X:{screen.Bounds.X}, Y:{screen.Bounds.Y}",
                    Tag = screen,
                    Location = new Point(16, y),
                    Size = new Size(370, 48),
                    TextAlign = ContentAlignment.MiddleLeft,
                    FlatStyle = FlatStyle.Flat,
                    BackColor = CARD,
                    ForeColor = TEXT,
                    Font = new Font("Segoe UI", 9f),
                    Cursor = Cursors.Hand
                };

                btn.FlatAppearance.BorderColor = ACCENT;
                btn.FlatAppearance.BorderSize = 1;

                btn.Click += (s, e) =>
                {
                    SelectedScreen = (Screen)((Button)s).Tag;
                    Hide();
                    DialogResult = DialogResult.OK;
                    Close();
                };

                Controls.Add(btn);
                y += 58;
            }

            var cancel = new Button
            {
                Text = "Cancel",
                Location = new Point(286, y + 8),
                Size = new Size(100, 32),
                FlatStyle = FlatStyle.Flat,
                BackColor = BG,
                ForeColor = TEXT_SEC,
                Cursor = Cursors.Hand
            };

            cancel.FlatAppearance.BorderColor = TEXT_SEC;
            cancel.Click += (s, e) =>
            {
                Hide();
                DialogResult = DialogResult.Cancel;
                Close();
            };

            Controls.Add(cancel);

            AcceptButton = null;
            CancelButton = cancel;
        }
    }

    class ScreenNumberOverlay : Form
    {
        readonly int screenNumber;

        public ScreenNumberOverlay(Screen screen, int number)
        {
            screenNumber = number;

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = screen.Bounds;
            ShowInTaskbar = false;
            TopMost = true;

            BackColor = Color.Magenta;
            TransparencyKey = Color.Magenta;

            DoubleBuffered = true;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                const int WS_EX_TOOLWINDOW = 0x00000080;
                const int WS_EX_TRANSPARENT = 0x00000020;

                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT;
                return cp;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            e.Graphics.SmoothingMode =
                System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            string text = screenNumber.ToString();

            using (var font = new Font("Segoe UI Semibold", 72, FontStyle.Bold))
            {
                SizeF textSize = e.Graphics.MeasureString(text, font);

                int boxSize = 150;
                int x = (Width - boxSize) / 2;
                int y = (Height - boxSize) / 2;

                var rect = new Rectangle(x, y, boxSize, boxSize);

                using (var shadow = new SolidBrush(Color.FromArgb(120, 0, 0, 0)))
                    e.Graphics.FillEllipse(shadow, x + 6, y + 6, boxSize, boxSize);

                using (var bg = new SolidBrush(Color.FromArgb(210, 28, 28, 38)))
                    e.Graphics.FillEllipse(bg, rect);

                using (var pen = new Pen(Color.FromArgb(99, 179, 237), 5))
                    e.Graphics.DrawEllipse(pen, rect);

                using (var brush = new SolidBrush(Color.White))
                {
                    float tx = x + (boxSize - textSize.Width) / 2f;
                    float ty = y + (boxSize - textSize.Height) / 2f - 4;
                    e.Graphics.DrawString(text, font, brush, tx, ty);
                }
            }
        }
    }

    class AutoScrollCaptureSession : Form
    {
        readonly Rectangle captureRegion;
        readonly bool includeCursor;
        readonly List<Bitmap> rawCaptures = new List<Bitmap>();
        readonly PictureBox previewBox;
        readonly Label countLabel;
        readonly Label helpLabel;

        System.Windows.Forms.Timer autoScrollTimer;
        bool captureInProgress = false;
        bool autoScrollStopped = false;
        int unchangedCount = 0;
        Bitmap lastRawFrame = null;
        Bitmap finalImage;

        int stickyTopPixels = 80;

        const int AutoScrollIntervalMs = 180;
        const int PageSettleDelayMs = 90;
        const int WheelAmount = -720; // negative = scroll down

        [DllImport("user32.dll")]
        static extern bool SetCursorPos(int X, int Y);

        [DllImport("user32.dll")]
        static extern void mouse_event(uint dwFlags, uint dx, uint dy, int dwData, UIntPtr dwExtraInfo);

        const uint MOUSEEVENTF_WHEEL = 0x0800;

        public AutoScrollCaptureSession(Rectangle region, bool includeMouseCursor)
        {
            captureRegion = region;
            includeCursor = includeMouseCursor;

            Text = "Scrolling Capture";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.Manual;
            Size = new Size(420, 620);
            TopMost = false;
            ShowInTaskbar = false;
            MaximizeBox = false;
            MinimizeBox = false;

            BackColor = Color.FromArgb(18, 18, 24);
            ForeColor = Color.White;

            var title = new Label
            {
                Text = "Scrolling Capture",
                Font = new Font("Segoe UI Semibold", 13f),
                ForeColor = Color.White,
                Location = new Point(16, 14),
                AutoSize = true
            };

            helpLabel = new Label
            {
                Text = "Auto-scrolling and capturing. Click Finish when the page is captured, or Cancel to discard.",
                Font = new Font("Segoe UI", 9f),
                ForeColor = Color.FromArgb(170, 170, 190),
                Location = new Point(16, 45),
                Size = new Size(370, 44)
            };

            countLabel = new Label
            {
                Text = "Captured sections: 0",
                Font = new Font("Segoe UI", 9f),
                ForeColor = Color.FromArgb(99, 179, 237),
                Location = new Point(16, 94),
                AutoSize = true
            };

            previewBox = new PictureBox
            {
                Location = new Point(16, 124),
                Size = new Size(370, 390),
                BackColor = Color.Black,
                SizeMode = PictureBoxSizeMode.Zoom,
                BorderStyle = BorderStyle.FixedSingle
            };

            var finishBtn = new Button
            {
                Text = "Finish",
                Location = new Point(196, 530),
                Size = new Size(90, 34),
                DialogResult = DialogResult.OK
            };

            var cancelBtn = new Button
            {
                Text = "Cancel",
                Location = new Point(296, 530),
                Size = new Size(90, 34),
                DialogResult = DialogResult.Cancel
            };

            finishBtn.Click += (s, e) =>
            {
                StopAutoScroll();
                countLabel.Text = "Building final image...";
                Application.DoEvents();

                finalImage = BuildFinalScrollingImage(rawCaptures, stickyTopPixels);
                DialogResult = DialogResult.OK;
                Close();
            };

            cancelBtn.Click += (s, e) =>
            {
                StopAutoScroll();
                DialogResult = DialogResult.Cancel;
                Close();
            };

            Controls.Add(title);
            Controls.Add(helpLabel);
            Controls.Add(countLabel);
            Controls.Add(previewBox);
            Controls.Add(finishBtn);
            Controls.Add(cancelBtn);

            Shown += (s, e) =>
            {
                MoveSessionWindowAwayFromCaptureRegion();
                CaptureRawFrameFast();
                StartAutoScroll();
            };
        }

        void MoveSessionWindowAwayFromCaptureRegion()
        {
            var screen = Screen.FromRectangle(captureRegion);
            var wa = screen.WorkingArea;

            var candidates = new[]
            {
                new Point(wa.Right - Width - 20, wa.Top + 20),
                new Point(wa.Left + 20, wa.Top + 20),
                new Point(wa.Right - Width - 20, wa.Bottom - Height - 20),
                new Point(wa.Left + 20, wa.Bottom - Height - 20)
            };

            foreach (var p in candidates)
            {
                var rect = new Rectangle(p, Size);
                if (!rect.IntersectsWith(captureRegion))
                {
                    Location = p;
                    return;
                }
            }

            Location = candidates[0];
        }

        void StartAutoScroll()
        {
            autoScrollStopped = false;
            autoScrollTimer = new System.Windows.Forms.Timer();
            autoScrollTimer.Interval = AutoScrollIntervalMs;
            autoScrollTimer.Tick += AutoScrollTimer_Tick;
            autoScrollTimer.Start();
        }

        void StopAutoScroll()
        {
            autoScrollStopped = true;
            autoScrollTimer?.Stop();
            autoScrollTimer?.Dispose();
            autoScrollTimer = null;
        }

        async void AutoScrollTimer_Tick(object sender, EventArgs e)
        {
            if (captureInProgress || autoScrollStopped) return;

            captureInProgress = true;

            try
            {
                SetCursorPos(
                    captureRegion.Left + captureRegion.Width / 2,
                    captureRegion.Top + captureRegion.Height / 2);

                mouse_event(MOUSEEVENTF_WHEEL, 0, 0, WheelAmount, UIntPtr.Zero);

                await System.Threading.Tasks.Task.Delay(PageSettleDelayMs);

                Bitmap frame = CaptureRegionBitmapFast(captureRegion);

                if (lastRawFrame != null && AreImagesSimilarFast(lastRawFrame, frame, 0.995))
                {
                    unchangedCount++;
                    frame.Dispose();

                    if (unchangedCount >= 3)
                    {
                        StopAutoScroll();
                        countLabel.Text = $"Captured sections: {rawCaptures.Count} — reached bottom";
                        helpLabel.Text = "Auto-scroll stopped because the page stopped changing. Click Finish to save.";
                    }

                    return;
                }

                unchangedCount = 0;

                lastRawFrame?.Dispose();
                lastRawFrame = new Bitmap(frame);

                rawCaptures.Add(frame);
                countLabel.Text = $"Captured sections: {rawCaptures.Count}";

                if (rawCaptures.Count % 5 == 0)
                    UpdatePreviewFromRawFast();
            }
            catch
            {
                // Keep the session alive if one frame fails.
            }
            finally
            {
                captureInProgress = false;
            }
        }

        void CaptureRawFrameFast()
        {
            var frame = CaptureRegionBitmapFast(captureRegion);
            rawCaptures.Add(frame);

            lastRawFrame?.Dispose();
            lastRawFrame = new Bitmap(frame);

            countLabel.Text = $"Captured sections: {rawCaptures.Count}";
            UpdatePreviewFromRawFast();
        }

        Bitmap CaptureRegionBitmapFast(Rectangle region)
        {
            var bmp = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppArgb);

            using (var g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(region.Location, Point.Empty, region.Size);

                if (includeCursor)
                    DrawCursorOnBitmap(g, region.Location);
            }

            return bmp;
        }

        void UpdatePreviewFromRawFast()
        {
            if (rawCaptures.Count == 0 || previewBox.Width <= 0 || previewBox.Height <= 0) return;

            var latest = rawCaptures[rawCaptures.Count - 1];
            var preview = new Bitmap(latest, previewBox.Width, previewBox.Height);

            var old = previewBox.Image;
            previewBox.Image = preview;
            old?.Dispose();
        }

        public Bitmap GetFinalImage()
        {
            return finalImage;
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            StopAutoScroll();
            lastRawFrame?.Dispose();
            lastRawFrame = null;

            if (DialogResult != DialogResult.OK)
            {
                finalImage?.Dispose();
                finalImage = null;
            }

            foreach (var bmp in rawCaptures)
                bmp.Dispose();

            var old = previewBox.Image;
            previewBox.Image = null;
            old?.Dispose();

            base.OnFormClosed(e);
        }

        static Bitmap BuildFinalScrollingImage(List<Bitmap> frames, int stickyTopPixels)
        {
            if (frames == null || frames.Count == 0)
                return null;

            var outputSections = new List<Bitmap>();

            outputSections.Add(new Bitmap(frames[0]));

            for (int i = 1; i < frames.Count; i++)
            {
                using (var stitchedSoFar = StitchVertical(outputSections))
                using (var cleaned = CropTop(frames[i], stickyTopPixels))
                {
                    Bitmap newPart = ExtractNewContentFromBottom(stitchedSoFar, cleaned);

                    if (newPart != null && newPart.Height > 35)
                        outputSections.Add(newPart);
                    else
                        newPart?.Dispose();
                }
            }

            Bitmap final = StitchVertical(outputSections);

            foreach (var bmp in outputSections)
                bmp.Dispose();

            return final;
        }

        static Bitmap CropTop(Bitmap source, int topPixels)
        {
            topPixels = Math.Max(0, Math.Min(topPixels, source.Height - 1));

            var cropped = new Bitmap(
                source.Width,
                source.Height - topPixels,
                PixelFormat.Format32bppArgb);

            using (var g = Graphics.FromImage(cropped))
            {
                g.DrawImage(
                    source,
                    new Rectangle(0, 0, cropped.Width, cropped.Height),
                    new Rectangle(0, topPixels, cropped.Width, cropped.Height),
                    GraphicsUnit.Pixel);
            }

            return cropped;
        }

        static Bitmap ExtractNewContentFromBottom(Bitmap stitchedSoFar, Bitmap current)
        {
            int overlap = FindBestOverlapAgainstBottom(stitchedSoFar, current);

            if (overlap <= 0)
                overlap = Math.Min(current.Height / 2, current.Height - 80);

            if (overlap >= current.Height - 35)
                overlap = Math.Max(0, current.Height - 220);

            int newHeight = current.Height - overlap;

            if (newHeight < 40)
                return null;

            var result = new Bitmap(current.Width, newHeight, PixelFormat.Format32bppArgb);

            using (var g = Graphics.FromImage(result))
            {
                g.DrawImage(
                    current,
                    new Rectangle(0, 0, current.Width, newHeight),
                    new Rectangle(0, overlap, current.Width, newHeight),
                    GraphicsUnit.Pixel);
            }

            return result;
        }

        static int FindBestOverlapAgainstBottom(Bitmap stitchedSoFar, Bitmap current)
        {
            int maxOverlap = Math.Min(stitchedSoFar.Height, current.Height - 80);
            int minOverlap = Math.Min(120, maxOverlap);

            if (maxOverlap <= 0 || minOverlap <= 0 || minOverlap > maxOverlap)
                return 0;

            int bestOverlap = 0;
            double bestScore = double.MaxValue;

            for (int overlap = minOverlap; overlap <= maxOverlap; overlap += 4)
            {
                double score = CompareMainScrollableArea(stitchedSoFar, current, overlap);

                if (score < bestScore)
                {
                    bestScore = score;
                    bestOverlap = overlap;
                }
            }

            return bestScore <= 32.0 ? bestOverlap : 0;
        }

        static double CompareMainScrollableArea(Bitmap stitched, Bitmap current, int overlap)
        {
            int width = Math.Min(stitched.Width, current.Width);

            int left = width / 30;
            int right = (int)(width * 0.62);

            if (right <= left)
                return double.MaxValue;

            int bottomStartY = stitched.Height - overlap;

            int stepX = Math.Max(1, (right - left) / 120);
            int stepY = Math.Max(1, overlap / 120);

            long diff = 0;
            long samples = 0;

            for (int y = 0; y < overlap; y += stepY)
            {
                int sy = bottomStartY + y;
                int cy = y;

                if (sy < 0 || sy >= stitched.Height || cy < 0 || cy >= current.Height)
                    continue;

                for (int x = left; x < right; x += stepX)
                {
                    Color a = stitched.GetPixel(x, sy);
                    Color b = current.GetPixel(x, cy);

                    if (IsNearlyWhite(a) && IsNearlyWhite(b))
                        continue;

                    diff += Math.Abs(a.R - b.R);
                    diff += Math.Abs(a.G - b.G);
                    diff += Math.Abs(a.B - b.B);
                    samples++;
                }
            }

            if (samples < 80)
                return double.MaxValue;

            return diff / (double)(samples * 3);
        }

        static bool AreImagesSimilarFast(Bitmap a, Bitmap b, double threshold)
        {
            if (a == null || b == null) return false;
            if (a.Width != b.Width || a.Height != b.Height) return false;

            int stepX = Math.Max(1, a.Width / 80);
            int stepY = Math.Max(1, a.Height / 80);

            long diff = 0;
            long samples = 0;

            for (int y = 0; y < a.Height; y += stepY)
            {
                for (int x = 0; x < a.Width; x += stepX)
                {
                    Color ca = a.GetPixel(x, y);
                    Color cb = b.GetPixel(x, y);

                    diff += Math.Abs(ca.R - cb.R);
                    diff += Math.Abs(ca.G - cb.G);
                    diff += Math.Abs(ca.B - cb.B);
                    samples++;
                }
            }

            if (samples == 0)
                return false;

            long maxDiff = samples * 3L * 255L;
            double similarity = 1.0 - (diff / (double)maxDiff);

            return similarity >= threshold;
        }

        static bool IsNearlyWhite(Color c)
        {
            return c.R > 240 && c.G > 240 && c.B > 240;
        }

        static Bitmap StitchVertical(List<Bitmap> images)
        {
            if (images == null || images.Count == 0)
                return null;

            int width = images[0].Width;
            int totalHeight = 0;

            foreach (var img in images)
                totalHeight += img.Height;

            var stitched = new Bitmap(width, totalHeight, PixelFormat.Format32bppArgb);

            using (var g = Graphics.FromImage(stitched))
            {
                g.Clear(Color.White);

                int y = 0;

                foreach (var img in images)
                {
                    g.DrawImage(img, 0, y, img.Width, img.Height);
                    y += img.Height;
                }
            }

            return stitched;
        }

        static void DrawCursorOnBitmap(Graphics g, Point captureOrigin)
        {
            // Optional: leave empty unless cursor drawing is needed for scrolling captures.
        }
    }


    
class ScrollCapturePrompt : Form
    {
        public ScrollCapturePrompt()
        {
            Text = "Scrolling Capture";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            Width = 360;
            Height = 170;
            TopMost = true;
            ShowInTaskbar = false;
            MaximizeBox = false;
            MinimizeBox = false;

            BackColor = Color.FromArgb(18, 18, 24);
            ForeColor = Color.White;

            var label = new Label
            {
                Text = "Scroll the page, then click Capture Next.\nClick Finish when done.",
                Location = new Point(18, 18),
                Size = new Size(310, 45),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9.5f)
            };

            var nextBtn = new Button
            {
                Text = "Capture Next",
                Location = new Point(18, 82),
                Size = new Size(130, 34),
                DialogResult = DialogResult.Retry
            };

            var finishBtn = new Button
            {
                Text = "Finish",
                Location = new Point(158, 82),
                Size = new Size(80, 34),
                DialogResult = DialogResult.OK
            };

            var cancelBtn = new Button
            {
                Text = "Cancel",
                Location = new Point(248, 82),
                Size = new Size(80, 34),
                DialogResult = DialogResult.Cancel
            };

            Controls.Add(label);
            Controls.Add(nextBtn);
            Controls.Add(finishBtn);
            Controls.Add(cancelBtn);

            AcceptButton = nextBtn;
            CancelButton = cancelBtn;
        }
    }



    class AnnotationEditorForm : Form
    {
        enum ToolMode { Pen, Rectangle, Arrow, Text }

        readonly Bitmap canvas;
        readonly PictureBox picture;
        readonly Panel scrollPanel;
        readonly List<Bitmap> undoStack = new List<Bitmap>();
        ToolMode currentTool = ToolMode.Pen;
        bool drawing = false;
        Point startPoint;
        Point lastPoint;
        Color drawColor = Color.Red;
        int penWidth = 4;

        public Bitmap EditedImage { get; private set; }

        public AnnotationEditorForm(Bitmap source)
        {
            canvas = new Bitmap(source);

            Text = "Annotate Capture";
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(1100, 760);
            MinimumSize = new Size(760, 520);
            BackColor = Color.FromArgb(18, 18, 24);
            ForeColor = Color.White;
            FormBorderStyle = FormBorderStyle.Sizable;
            KeyPreview = true;

            var toolbar = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 48,
                Padding = new Padding(8, 8, 8, 6),
                BackColor = Color.FromArgb(26, 26, 36),
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false
            };

            var penBtn = MakeToolButton("Pen");
            var rectBtn = MakeToolButton("Rectangle");
            var arrowBtn = MakeToolButton("Arrow");
            var textBtn = MakeToolButton("Text");
            var undoBtn = MakeToolButton("Undo");
            var saveBtn = MakeToolButton("Save");
            var cancelBtn = MakeToolButton("Cancel");

            penBtn.Click += (s, e) => currentTool = ToolMode.Pen;
            rectBtn.Click += (s, e) => currentTool = ToolMode.Rectangle;
            arrowBtn.Click += (s, e) => currentTool = ToolMode.Arrow;
            textBtn.Click += (s, e) => currentTool = ToolMode.Text;
            undoBtn.Click += (s, e) => Undo();
            saveBtn.Click += (s, e) => SaveAndClose();
            cancelBtn.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };

            var hint = new Label
            {
                Text = "Draw with the mouse. Use Text, then click the image to place text.",
                ForeColor = Color.FromArgb(170, 170, 190),
                AutoSize = true,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(12, 8, 0, 0)
            };

            toolbar.Controls.AddRange(new Control[] { penBtn, rectBtn, arrowBtn, textBtn, undoBtn, saveBtn, cancelBtn, hint });

            scrollPanel = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                BackColor = Color.FromArgb(10, 10, 14)
            };

            picture = new PictureBox
            {
                Image = canvas,
                SizeMode = PictureBoxSizeMode.Normal,
                Location = new Point(0, 0),
                Size = canvas.Size,
                BackColor = Color.Black
            };

            picture.MouseDown += Picture_MouseDown;
            picture.MouseMove += Picture_MouseMove;
            picture.MouseUp += Picture_MouseUp;
            picture.Paint += Picture_Paint;

            scrollPanel.Controls.Add(picture);
            Controls.Add(scrollPanel);
            Controls.Add(toolbar);

            KeyDown += (s, e) =>
            {
                if (e.Control && e.KeyCode == Keys.Z)
                    Undo();
                else if (e.Control && e.KeyCode == Keys.S)
                    SaveAndClose();
                else if (e.KeyCode == Keys.Escape)
                {
                    DialogResult = DialogResult.Cancel;
                    Close();
                }
            };
        }

        Button MakeToolButton(string text)
        {
            var btn = new Button
            {
                Text = text,
                Width = 86,
                Height = 30,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(34, 34, 48),
                ForeColor = Color.White,
                Cursor = Cursors.Hand,
                Margin = new Padding(3, 0, 3, 0)
            };
            btn.FlatAppearance.BorderColor = Color.FromArgb(99, 179, 237);
            return btn;
        }

        void PushUndo()
        {
            undoStack.Add(new Bitmap(canvas));

            if (undoStack.Count > 20)
            {
                undoStack[0].Dispose();
                undoStack.RemoveAt(0);
            }
        }

        void Undo()
        {
            if (undoStack.Count == 0)
                return;

            using (var g = Graphics.FromImage(canvas))
            {
                g.DrawImageUnscaled(undoStack[undoStack.Count - 1], 0, 0);
            }

            undoStack[undoStack.Count - 1].Dispose();
            undoStack.RemoveAt(undoStack.Count - 1);
            picture.Invalidate();
        }

        Point ImagePoint(MouseEventArgs e)
        {
            return new Point(
                Math.Max(0, Math.Min(canvas.Width - 1, e.X)),
                Math.Max(0, Math.Min(canvas.Height - 1, e.Y)));
        }

        void Picture_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left)
                return;

            startPoint = ImagePoint(e);
            lastPoint = startPoint;

            if (currentTool == ToolMode.Text)
            {
                using (var prompt = new TextPromptDialog())
                {
                    if (prompt.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(prompt.ResultText))
                    {
                        PushUndo();

                        using (var g = Graphics.FromImage(canvas))
                        using (var font = new Font("Segoe UI Semibold", 22f, FontStyle.Bold))
                        using (var brush = new SolidBrush(drawColor))
                        using (var shadow = new SolidBrush(Color.FromArgb(130, 0, 0, 0)))
                        {
                            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                            g.DrawString(prompt.ResultText, font, shadow, startPoint.X + 2, startPoint.Y + 2);
                            g.DrawString(prompt.ResultText, font, brush, startPoint);
                        }

                        picture.Invalidate();
                    }
                }

                return;
            }

            PushUndo();
            drawing = true;
        }

        void Picture_MouseMove(object sender, MouseEventArgs e)
        {
            if (!drawing)
                return;

            var current = ImagePoint(e);

            if (currentTool == ToolMode.Pen)
            {
                using (var g = Graphics.FromImage(canvas))
                using (var pen = new Pen(drawColor, penWidth) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round })
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.DrawLine(pen, lastPoint, current);
                }

                lastPoint = current;
            }
            else
            {
                lastPoint = current;
            }

            picture.Invalidate();
        }

        void Picture_MouseUp(object sender, MouseEventArgs e)
        {
            if (!drawing)
                return;

            drawing = false;
            var endPoint = ImagePoint(e);

            if (currentTool == ToolMode.Rectangle)
            {
                using (var g = Graphics.FromImage(canvas))
                using (var pen = new Pen(drawColor, penWidth))
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.DrawRectangle(pen, NormalizeRect(startPoint, endPoint));
                }
            }
            else if (currentTool == ToolMode.Arrow)
            {
                using (var g = Graphics.FromImage(canvas))
                using (var pen = new Pen(drawColor, penWidth))
                {
                    pen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                    pen.EndCap = System.Drawing.Drawing2D.LineCap.ArrowAnchor;
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.DrawLine(pen, startPoint, endPoint);
                }
            }

            picture.Invalidate();
        }

        void Picture_Paint(object sender, PaintEventArgs e)
        {
            if (!drawing)
                return;

            if (currentTool == ToolMode.Rectangle)
            {
                using (var pen = new Pen(Color.FromArgb(190, drawColor), penWidth))
                    e.Graphics.DrawRectangle(pen, NormalizeRect(startPoint, lastPoint));
            }
            else if (currentTool == ToolMode.Arrow)
            {
                using (var pen = new Pen(Color.FromArgb(190, drawColor), penWidth))
                {
                    pen.EndCap = System.Drawing.Drawing2D.LineCap.ArrowAnchor;
                    e.Graphics.DrawLine(pen, startPoint, lastPoint);
                }
            }
        }

        Rectangle NormalizeRect(Point a, Point b)
        {
            return new Rectangle(
                Math.Min(a.X, b.X),
                Math.Min(a.Y, b.Y),
                Math.Abs(a.X - b.X),
                Math.Abs(a.Y - b.Y));
        }

        void SaveAndClose()
        {
            EditedImage?.Dispose();
            EditedImage = new Bitmap(canvas);
            DialogResult = DialogResult.OK;
            Close();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            foreach (var bmp in undoStack)
                bmp.Dispose();

            if (DialogResult != DialogResult.OK)
            {
                EditedImage?.Dispose();
                EditedImage = null;
            }

            canvas.Dispose();
            base.OnFormClosed(e);
        }
    }

    class TextPromptDialog : Form
    {
        TextBox textBox;
        public string ResultText { get; private set; }

        public TextPromptDialog()
        {
            Text = "Add Text";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            Size = new Size(420, 150);
            MaximizeBox = false;
            MinimizeBox = false;
            BackColor = Color.FromArgb(18, 18, 24);
            ForeColor = Color.White;

            var lbl = new Label
            {
                Text = "Text to place on the image:",
                Location = new Point(14, 14),
                AutoSize = true,
                ForeColor = Color.White
            };

            textBox = new TextBox
            {
                Location = new Point(14, 40),
                Size = new Size(370, 25),
                BackColor = Color.FromArgb(34, 34, 48),
                ForeColor = Color.White
            };

            var ok = new Button
            {
                Text = "OK",
                Location = new Point(214, 76),
                Size = new Size(80, 28),
                DialogResult = DialogResult.OK
            };

            var cancel = new Button
            {
                Text = "Cancel",
                Location = new Point(304, 76),
                Size = new Size(80, 28),
                DialogResult = DialogResult.Cancel
            };

            ok.Click += (s, e) => ResultText = textBox.Text;

            Controls.AddRange(new Control[] { lbl, textBox, ok, cancel });

            AcceptButton = ok;
            CancelButton = cancel;
        }
    }

}


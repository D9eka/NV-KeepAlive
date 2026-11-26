using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace NvKeepAliveUI {
    public class GpuItem {
        public int Index;
        public string PciBusId;
        public string Name;
        public string DeviceIdHex;
        public ulong TotalVramMiB;
        public bool IsMining;
        public bool IsCmp40hx;
        public bool HasVulkan;
        public bool HasCuda;

        // Live telemetry from nvidia-smi
        public string UsedVram = "0";
        public string LiveTotalVram = "";
        public string TempC = "-";
        public string PowerW = "-";
        public string PcieText = "";

        // Draft UI selection
        public bool Selected = true;
        public string Backend = "vulkan"; // "vulkan" or "cuda"
    }

    public class MainWindow : Window {
        const string Version = "1.0.0";
        const string TaskName = "NV-KeepAlive";
        const string Gen2TaskName = "40HX PCIe Gen2 Bring-up";
        const string MutexName = "Global\\NvKeepAliveMutex";
        const string UnlockerUrl = "https://github.com/PZH1gdmu/CMP40HX-Unlock/releases";
        const string LunarGUrl = "https://vulkan.lunarg.com/sdk/home#windows";
        const string NvidiaDrvUrl = "https://www.nvidia.com/drivers";

        static readonly string InstallDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NV-KeepAlive");
        static readonly string InstalledExe = System.IO.Path.Combine(InstallDir, "NvKeepAlive.exe");
        static readonly string PauseFile = System.IO.Path.Combine(InstallDir, "keepalive.pause");
        static readonly string LogFile = System.IO.Path.Combine(InstallDir, "keepalive.log");
        static readonly string Gen2WrapperPath = System.IO.Path.Combine(InstallDir, "gen2-sync-wrapper.cmd");
        static readonly string Gen2OrigXmlPath = System.IO.Path.Combine(InstallDir, "gen2-orig.xml");
        static readonly string Gen2OrigCmdPath = System.IO.Path.Combine(InstallDir, "gen2-orig.txt");

        // Win32 dark titlebar & env broadcast
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, UIntPtr wParam, string lParam, uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);

        static void ApplyDarkTitleBar(Window w) {
            try {
                IntPtr hwnd = new WindowInteropHelper(w).Handle;
                if (hwnd == IntPtr.Zero) return;
                int val = 1;
                if (DwmSetWindowAttribute(hwnd, 20, ref val, sizeof(int)) != 0)
                    DwmSetWindowAttribute(hwnd, 19, ref val, sizeof(int));
            } catch {}
        }

        // Brushes (Windows 11 Fluent Dark palette)
        static SolidColorBrush Br(string hex) {
            var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex);
            b.Freeze();
            return b;
        }
        static readonly Brush BgWindow = Br("#202020");
        static readonly Brush BgCard = Br("#2B2B2B");
        static readonly Brush BorderCard = Br("#353535");
        static readonly Brush BgRow = Br("#242424");
        static readonly Brush BgRowSel = Br("#25303A");
        static readonly Brush BorderRowSel = Br("#4A8CB0");
        static readonly Brush BgSeg = Br("#1B1B1B");
        static readonly Brush BorderSeg = Br("#3C3C3C");
        static readonly Brush BgLog = Br("#181818");
        static readonly Brush Accent = Br("#60CDFF");
        static readonly Brush AccentDarkFg = Br("#0B1620");
        static readonly Brush FgPrimary = Br("#F3F3F3");
        static readonly Brush FgSecondary = Br("#A5A5A5");
        static readonly Brush FgMuted = Br("#9D9D9D");
        static readonly Brush GreenBg = Br("#1D3A27");
        static readonly Brush GreenBorder = Br("#2C5C3B");
        static readonly Brush GreenFg = Br("#6CCB5F");
        static readonly Brush YellowBg = Br("#3E321A");
        static readonly Brush YellowBorder = Br("#614E25");
        static readonly Brush YellowFg = Br("#FCE100");
        static readonly Brush RedBg = Br("#362224");
        static readonly Brush RedBorder = Br("#5A3438");
        static readonly Brush RedFg = Br("#FF99A4");
        static readonly Brush BtnSecBg = Br("#333333");
        static readonly Brush BtnSecBorder = Br("#444444");
        static readonly Brush MiningBg = Br("#362C1E");
        static readonly Brush MiningFg = Br("#F5B951");
        static readonly Brush BlueBadgeBg = Br("#1D3548");

        // State
        bool _isRu;
        string _daemonSourceExe;
        List<GpuItem> _gpus = new List<GpuItem>();
        Dictionary<int, string> _appliedTargets = new Dictionary<int, string>();
        bool _serviceInstalled = false;
        bool _serviceRunning = false;
        bool _servicePaused = false;
        DateTime _sessionStart = DateTime.Now;
        int _tdr5m = 0, _tdrStart = 0;

        // Live CPU calculation
        int _lastPid = -1;
        TimeSpan _lastCpuTime = TimeSpan.Zero;
        DateTime _lastCpuWall = DateTime.MinValue;
        string _liveCpuText = "-";

        // Vulkan probe
        string _vkCheckStatus = "OK"; // OK | NO_LOADER | NO_NVIDIA_ICD

        // Gen2 Unlock state
        string _gen2State = "absent"; // absent | unsynced | synced
        string _gen2PcieSummary = "";

        // COOPMAT2 state
        bool _coopmatApplied = false;
        bool _coopmatDraft = false;

        // Polling & Test
        DispatcherTimer _pollTimer;
        volatile bool _pollBusy = false;
        volatile bool _testRunning = false;
        string _lastLogText = "";

        // UI Controls
        Button _btnRu, _btnEn;
        TextBlock _lblStatusTitle, _lblTdr5m, _valTdr5m, _lblTdrStart, _valTdrStart, _valCpu;
        Border _statusPill; Ellipse _statusDot; TextBlock _statusText;
        StackPanel _gpuListPanel;
        Border _pendingHintBox; TextBlock _pendingHintText;
        Border _vkBadge; TextBlock _vkBadgeText, _legendVk, _cudaBadgeText, _legendCuda;

        Border _gen2Card, _gen2Badge;
        TextBlock _lblGen2Title, _descGen2, _gen2BadgeText;
        Button _btnGen2Help, _btnGen2Sync;

        Border _coopmatCard, _coopmatAppliedBadge;
        TextBlock _lblCoopmat, _descCoopmat, _coopmatAppliedText, _coopmatSwitchLbl;
        Border _toggleTrack; Ellipse _toggleThumb;
        Button _btnApplyCoopmat;

        Button _btnInstall, _btnPause, _btnTest, _btnRemove;
        StackPanel _testProgressWrap; TextBlock _testProgressLbl, _testProgressPct; ProgressBar _testProgressBar;
        TextBlock _lblLogTitle; Button _btnOpenFolder; TextBox _logBox;

        public MainWindow() {
            _isRu = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("ru", StringComparison.OrdinalIgnoreCase);
            _daemonSourceExe = FindDaemonExe();

            Title = "NV-KeepAlive v" + Version;
            Width = 650;
            MinWidth = 600;
            SizeToContent = SizeToContent.Height;
            MaxHeight = 820;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = BgWindow;
            Foreground = FgPrimary;
            FontFamily = new FontFamily("Segoe UI Variable, Segoe UI");
            FontSize = 13;

            SourceInitialized += (s, e) => ApplyDarkTitleBar(this);
            Loaded += OnLoaded;
            Closed += (s, e) => { if (_pollTimer != null) _pollTimer.Stop(); };

            BuildUi();
        }

        static string FindDaemonExe() {
            string local = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "NvKeepAlive.exe");
            if (File.Exists(local)) return local;
            if (File.Exists(InstalledExe)) return InstalledExe;
            return local;
        }

        string T(string ru, string en) { return _isRu ? ru : en; }

        // ===================== UI Construction =====================
        Style MakeButtonStyle(Brush bg, Brush border, Brush fg, bool bold) {
            var st = new Style(typeof(Button));
            st.Setters.Add(new Setter(Control.BackgroundProperty, bg));
            st.Setters.Add(new Setter(Control.BorderBrushProperty, border));
            st.Setters.Add(new Setter(Control.ForegroundProperty, fg));
            st.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
            st.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 6, 10, 6)));
            st.Setters.Add(new Setter(Control.FontSizeProperty, 12.0));
            st.Setters.Add(new Setter(Control.FontWeightProperty, bold ? FontWeights.SemiBold : FontWeights.Normal));
            st.Setters.Add(new Setter(FrameworkElement.CursorProperty, Cursors.Hand));

            var tpl = new ControlTemplate(typeof(Button));
            var bd = new FrameworkElementFactory(typeof(Border));
            bd.Name = "bd";
            bd.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            bd.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            bd.SetBinding(Border.BorderThicknessProperty, new System.Windows.Data.Binding("BorderThickness") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
            bd.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            bd.AppendChild(cp);
            tpl.VisualTree = bd;

            var trigHover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            trigHover.Setters.Add(new Setter(UIElement.OpacityProperty, 0.90));
            tpl.Triggers.Add(trigHover);

            var trigDis = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            trigDis.Setters.Add(new Setter(UIElement.OpacityProperty, 0.42));
            tpl.Triggers.Add(trigDis);

            st.Setters.Add(new Setter(Control.TemplateProperty, tpl));
            return st;
        }

        Style PrimaryBtnStyle() { return MakeButtonStyle(Accent, Accent, AccentDarkFg, true); }
        Style SecondaryBtnStyle() { return MakeButtonStyle(BtnSecBg, BtnSecBorder, FgPrimary, false); }
        Style DangerBtnStyle() { return MakeButtonStyle(RedBg, RedBorder, RedFg, false); }

        Border MakeCard(UIElement child, Thickness pad) {
            return new Border {
                Background = BgCard,
                BorderBrush = BorderCard,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = pad,
                Margin = new Thickness(0, 0, 0, 10),
                Child = child
            };
        }

        void BuildUi() {
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var root = new StackPanel { Margin = new Thickness(16, 12, 16, 14) };

            // Top bar (Title + RU/EN toggle)
            var topBar = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            var langBox = new Border {
                Background = BgCard, BorderBrush = BorderSeg, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5), Padding = new Thickness(2)
            };
            var langSp = new StackPanel { Orientation = Orientation.Horizontal };
            _btnRu = new Button { Content = "RU", Padding = new Thickness(8, 2, 8, 2), FontSize = 11 };
            _btnEn = new Button { Content = "EN", Padding = new Thickness(8, 2, 8, 2), FontSize = 11, Margin = new Thickness(2, 0, 0, 0) };
            _btnRu.Click += (s, e) => { _isRu = true; RefreshTextsAndState(); };
            _btnEn.Click += (s, e) => { _isRu = false; RefreshTextsAndState(); };
            langSp.Children.Add(_btnRu);
            langSp.Children.Add(_btnEn);
            langBox.Child = langSp;
            DockPanel.SetDock(langBox, Dock.Right);
            topBar.Children.Add(langBox);

            var titleRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            titleRow.Children.Add(new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(2), Background = Accent, Margin = new Thickness(0, 0, 8, 0) });
            titleRow.Children.Add(new TextBlock { Text = "NV-KeepAlive v" + Version, FontWeight = FontWeights.SemiBold, FontSize = 14, Foreground = FgPrimary });
            topBar.Children.Add(titleRow);
            root.Children.Add(topBar);

            // ===== Block 1: GPUs, Backends, Live CPU & Short-Window TDR =====
            var b1 = new StackPanel();
            var b1Head = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };

            _statusPill = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Padding = new Thickness(8, 2, 8, 2), VerticalAlignment = VerticalAlignment.Center };
            var pillSp = new StackPanel { Orientation = Orientation.Horizontal };
            _statusDot = new Ellipse { Width = 6, Height = 6, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            _statusText = new TextBlock { FontSize = 11.5, FontWeight = FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center };
            pillSp.Children.Add(_statusDot);
            pillSp.Children.Add(_statusText);
            _statusPill.Child = pillSp;
            DockPanel.SetDock(_statusPill, Dock.Right);
            b1Head.Children.Add(_statusPill);

            var metricsSp = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
            _lblStatusTitle = new TextBlock { FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = FgMuted, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            metricsSp.Children.Add(_lblStatusTitle);
            metricsSp.Children.Add(new TextBlock { Text = "|", Foreground = Br("#555555"), Margin = new Thickness(0, 0, 8, 0), FontSize = 11 });
            _lblTdr5m = new TextBlock { FontSize = 11.5, Foreground = FgMuted, Margin = new Thickness(0, 0, 3, 0) };
            _valTdr5m = new TextBlock { FontSize = 11.5, FontFamily = new FontFamily("Consolas"), FontWeight = FontWeights.Bold, Foreground = GreenFg, Text = "0", Margin = new Thickness(0, 0, 8, 0) };
            _lblTdrStart = new TextBlock { FontSize = 11.5, Foreground = FgMuted, Margin = new Thickness(0, 0, 3, 0) };
            _valTdrStart = new TextBlock { FontSize = 11.5, FontFamily = new FontFamily("Consolas"), FontWeight = FontWeights.Bold, Foreground = GreenFg, Text = "0", Margin = new Thickness(0, 0, 8, 0) };
            var lblCpu = new TextBlock { FontSize = 11.5, Foreground = FgMuted, Text = "CPU: " };
            _valCpu = new TextBlock { FontSize = 11.5, FontFamily = new FontFamily("Consolas"), FontWeight = FontWeights.Bold, Foreground = FgPrimary, Text = "-" };
            metricsSp.Children.Add(_lblTdr5m); metricsSp.Children.Add(_valTdr5m);
            metricsSp.Children.Add(_lblTdrStart); metricsSp.Children.Add(_valTdrStart);
            metricsSp.Children.Add(lblCpu); metricsSp.Children.Add(_valCpu);
            b1Head.Children.Add(metricsSp);
            b1.Children.Add(b1Head);

            _gpuListPanel = new StackPanel();
            b1.Children.Add(_gpuListPanel);

            _pendingHintBox = new Border {
                Background = YellowBg, BorderBrush = YellowBorder, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5), Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(0, 6, 0, 0),
                Visibility = Visibility.Collapsed
            };
            _pendingHintText = new TextBlock { FontSize = 11.5, Foreground = YellowFg, TextWrapping = TextWrapping.Wrap };
            _pendingHintBox.Child = _pendingHintText;
            b1.Children.Add(_pendingHintBox);

            // Compact backend legend (no hardcoded CPU!)
            var legGrid = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            legGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            legGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            legGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var vkLeg = new Border { Background = Br("#232323"), BorderBrush = Br("#323232"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), Padding = new Thickness(8, 5, 8, 5) };
            var vkLegSp = new StackPanel();
            var vkTop = new StackPanel { Orientation = Orientation.Horizontal };
            vkTop.Children.Add(new TextBlock { Text = "Vulkan", FontWeight = FontWeights.SemiBold, FontSize = 11.5, Foreground = FgPrimary });
            _vkBadge = new Border { CornerRadius = new CornerRadius(3), Padding = new Thickness(5, 0, 5, 1), Margin = new Thickness(6, 0, 0, 0), Cursor = Cursors.Hand };
            _vkBadgeText = new TextBlock { FontSize = 10.5 };
            _vkBadge.Child = _vkBadgeText;
            _vkBadge.MouseLeftButtonUp += (s, e) => { if (_vkCheckStatus != "OK") ShowVulkanGuideModal(); };
            vkTop.Children.Add(_vkBadge);
            _legendVk = new TextBlock { FontSize = 11, Foreground = FgSecondary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
            vkLegSp.Children.Add(vkTop); vkLegSp.Children.Add(_legendVk);
            vkLeg.Child = vkLegSp;
            Grid.SetColumn(vkLeg, 0);
            legGrid.Children.Add(vkLeg);

            var cuLeg = new Border { Background = Br("#232323"), BorderBrush = Br("#323232"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), Padding = new Thickness(8, 5, 8, 5) };
            var cuLegSp = new StackPanel();
            var cuTop = new StackPanel { Orientation = Orientation.Horizontal };
            cuTop.Children.Add(new TextBlock { Text = "CUDA", FontWeight = FontWeights.SemiBold, FontSize = 11.5, Foreground = FgPrimary });
            var cuBdg = new Border { Background = GreenBg, CornerRadius = new CornerRadius(3), Padding = new Thickness(5, 0, 5, 1), Margin = new Thickness(6, 0, 0, 0) };
            _cudaBadgeText = new TextBlock { FontSize = 10.5, Foreground = GreenFg };
            cuBdg.Child = _cudaBadgeText;
            cuTop.Children.Add(cuBdg);
            _legendCuda = new TextBlock { FontSize = 11, Foreground = FgSecondary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
            cuLegSp.Children.Add(cuTop); cuLegSp.Children.Add(_legendCuda);
            cuLeg.Child = cuLegSp;
            Grid.SetColumn(cuLeg, 2);
            legGrid.Children.Add(cuLeg);

            b1.Children.Add(legGrid);
            root.Children.Add(MakeCard(b1, new Thickness(12, 10, 12, 10)));

            // ===== Block 2: PCIe Gen2 Unlock Sync + '?' (shown ONLY when CMP 40HX is selected) =====
            var g2Dock = new DockPanel();
            _btnGen2Sync = new Button { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            _btnGen2Sync.Click += OnGen2SyncClick;
            DockPanel.SetDock(_btnGen2Sync, Dock.Right);
            g2Dock.Children.Add(_btnGen2Sync);

            var g2Left = new StackPanel();
            var g2Top = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
            _lblGen2Title = new TextBlock { FontWeight = FontWeights.Medium, Foreground = FgPrimary, VerticalAlignment = VerticalAlignment.Center };
            _btnGen2Help = new Button {
                Content = "?", Width = 19, Height = 19, Padding = new Thickness(0), Margin = new Thickness(7, 0, 7, 0),
                FontSize = 11, FontWeight = FontWeights.Bold, Style = MakeButtonStyle(BtnSecBg, Br("#555555"), FgPrimary, true)
            };
            _btnGen2Help.Click += (s, e) => ShowGen2InfoModal();
            _gen2Badge = new Border { CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1), Padding = new Thickness(7, 1, 7, 1), VerticalAlignment = VerticalAlignment.Center };
            _gen2BadgeText = new TextBlock { FontSize = 11, FontWeight = FontWeights.Medium };
            _gen2Badge.Child = _gen2BadgeText;
            g2Top.Children.Add(_lblGen2Title); g2Top.Children.Add(_btnGen2Help); g2Top.Children.Add(_gen2Badge);
            _descGen2 = new TextBlock { FontSize = 11.5, Foreground = FgSecondary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
            g2Left.Children.Add(g2Top); g2Left.Children.Add(_descGen2);
            g2Dock.Children.Add(g2Left);
            _gen2Card = MakeCard(g2Dock, new Thickness(12, 10, 12, 10));
            root.Children.Add(_gen2Card);

            // ===== Block 3: GGML_VK_DISABLE_COOPMAT2 Toggle + Apply (shown ONLY when a CMP GPU is selected) =====
            var cmDock = new DockPanel();
            var cmRight = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            _toggleTrack = new Border {
                Width = 40, Height = 20, CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(1), Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center
            };
            _toggleThumb = new Ellipse { Width = 12, Height = 12, Margin = new Thickness(3, 0, 3, 0), VerticalAlignment = VerticalAlignment.Center };
            _toggleTrack.Child = _toggleThumb;
            _toggleTrack.MouseLeftButtonUp += (s, e) => { _coopmatDraft = !_coopmatDraft; RenderCoopmat(); };
            _coopmatSwitchLbl = new TextBlock { FontSize = 12, Foreground = FgPrimary, Width = 34, Margin = new Thickness(6, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            _btnApplyCoopmat = new Button { Style = PrimaryBtnStyle(), VerticalAlignment = VerticalAlignment.Center };
            _btnApplyCoopmat.Click += OnApplyCoopmatClick;
            cmRight.Children.Add(_toggleTrack); cmRight.Children.Add(_coopmatSwitchLbl); cmRight.Children.Add(_btnApplyCoopmat);
            DockPanel.SetDock(cmRight, Dock.Right);
            cmDock.Children.Add(cmRight);

            var cmLeft = new StackPanel();
            var cmTop = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
            _lblCoopmat = new TextBlock { Text = "GGML_VK_DISABLE_COOPMAT2", FontWeight = FontWeights.Medium, Foreground = FgPrimary, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            _coopmatAppliedBadge = new Border { CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), Padding = new Thickness(6, 1, 6, 1), VerticalAlignment = VerticalAlignment.Center };
            _coopmatAppliedText = new TextBlock { FontSize = 10.5, FontFamily = new FontFamily("Consolas") };
            _coopmatAppliedBadge.Child = _coopmatAppliedText;
            cmTop.Children.Add(_lblCoopmat); cmTop.Children.Add(_coopmatAppliedBadge);
            _descCoopmat = new TextBlock { FontSize = 11.5, Foreground = FgSecondary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
            cmLeft.Children.Add(cmTop); cmLeft.Children.Add(_descCoopmat);
            cmDock.Children.Add(cmLeft);
            _coopmatCard = MakeCard(cmDock, new Thickness(12, 10, 12, 10));
            root.Children.Add(_coopmatCard);

            // ===== Block 4: Action Buttons =====
            var actGrid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            for (int i = 0; i < 4; i++) {
                if (i > 0) actGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
                actGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            }
            _btnInstall = new Button { Padding = new Thickness(8, 8, 8, 8) };
            _btnPause   = new Button { Padding = new Thickness(8, 8, 8, 8), Style = SecondaryBtnStyle() };
            _btnTest    = new Button { Padding = new Thickness(8, 8, 8, 8) };
            _btnRemove  = new Button { Padding = new Thickness(8, 8, 8, 8), Style = DangerBtnStyle() };

            _btnInstall.Click += OnInstallClick;
            _btnPause.Click   += OnPauseClick;
            _btnTest.Click    += OnTestClick;
            _btnRemove.Click  += OnRemoveClick;

            Grid.SetColumn(_btnInstall, 0); actGrid.Children.Add(_btnInstall);
            Grid.SetColumn(_btnPause, 2);   actGrid.Children.Add(_btnPause);
            Grid.SetColumn(_btnTest, 4);    actGrid.Children.Add(_btnTest);
            Grid.SetColumn(_btnRemove, 6);  actGrid.Children.Add(_btnRemove);
            root.Children.Add(actGrid);

            // Test Progress Bar
            _testProgressWrap = new StackPanel { Margin = new Thickness(0, 0, 0, 10), Visibility = Visibility.Collapsed };
            var tpTop = new DockPanel { Margin = new Thickness(0, 0, 0, 3) };
            _testProgressPct = new TextBlock { FontSize = 11.5, FontFamily = new FontFamily("Consolas"), Foreground = FgSecondary };
            DockPanel.SetDock(_testProgressPct, Dock.Right);
            tpTop.Children.Add(_testProgressPct);
            _testProgressLbl = new TextBlock { FontSize = 11.5, Foreground = FgSecondary };
            tpTop.Children.Add(_testProgressLbl);
            _testProgressBar = new ProgressBar { Height = 6, Minimum = 0, Maximum = 100, Background = BtnSecBg, Foreground = Accent, BorderThickness = new Thickness(0) };
            _testProgressWrap.Children.Add(tpTop);
            _testProgressWrap.Children.Add(_testProgressBar);
            root.Children.Add(_testProgressWrap);

            // ===== Block 5: Event Log =====
            var logHead = new DockPanel { Margin = new Thickness(0, 2, 0, 4) };
            _btnOpenFolder = new Button {
                Style = MakeButtonStyle(Brushes.Transparent, Brushes.Transparent, Accent, false),
                Padding = new Thickness(2, 0, 2, 0), FontSize = 11.5
            };
            _btnOpenFolder.Click += (s, e) => {
                try {
                    Directory.CreateDirectory(InstallDir);
                    Process.Start("explorer.exe", InstallDir);
                } catch {}
            };
            DockPanel.SetDock(_btnOpenFolder, Dock.Right);
            logHead.Children.Add(_btnOpenFolder);
            _lblLogTitle = new TextBlock { FontSize = 11.5, Foreground = FgMuted, FontWeight = FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center };
            logHead.Children.Add(_lblLogTitle);
            root.Children.Add(logHead);

            _logBox = new TextBox {
                IsReadOnly = true, Height = 105, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = BgLog, Foreground = Br("#CCCCCC"), BorderBrush = Br("#323232"), BorderThickness = new Thickness(1),
                FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 11, Padding = new Thickness(8, 6, 8, 6)
            };
            root.Children.Add(_logBox);

            scroll.Content = root;
            Content = scroll;
        }

        // ===================== Startup & Live Polling =====================
        void OnLoaded(object sender, RoutedEventArgs e) {
            LoadCoopmatState();
            ProbeInitialHardwareAndService();
            RefreshTextsAndState();

            _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _pollTimer.Tick += (s, ev) => TriggerBackgroundPoll();
            _pollTimer.Start();
            TriggerBackgroundPoll();
        }

        void LoadCoopmatState() {
            string val = Environment.GetEnvironmentVariable("GGML_VK_DISABLE_COOPMAT2", EnvironmentVariableTarget.Machine);
            _coopmatApplied = (val == "1");
            _coopmatDraft = _coopmatApplied;
        }

        void ProbeInitialHardwareAndService() {
            CheckVulkanNow();
            ReadInstalledServiceConfig();

            // Enumerate NVIDIA GPUs via NvKeepAlive.exe --list-tsv
            _gpus.Clear();
            string outStr;
            int rc = RunProcessCapture(_daemonSourceExe, "--list-tsv", 6000, out outStr);
            if (rc == 0 && !string.IsNullOrEmpty(outStr)) {
                foreach (string rawLine in outStr.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)) {
                    string[] p = rawLine.Split('\t');
                    if (p.Length < 9) continue;
                    int idx; ulong vram;
                    if (!int.TryParse(p[0], out idx)) continue;
                    ulong.TryParse(p[4], out vram);
                    var g = new GpuItem {
                        Index = idx,
                        PciBusId = p[1] == "-" ? "" : p[1],
                        Name = p[2],
                        DeviceIdHex = p[3],
                        TotalVramMiB = vram,
                        IsMining = p[5] == "1",
                        IsCmp40hx = p[6] == "1",
                        HasVulkan = p[7] == "1",
                        HasCuda = p[8] == "1",
                        Backend = (p[7] == "1" && _vkCheckStatus == "OK") ? "vulkan" : "cuda"
                    };
                    _gpus.Add(g);
                }
            }

            // Initialize draft selections from currently installed service, or sensible defaults
            if (_appliedTargets.Count > 0) {
                foreach (var g in _gpus) {
                    if (_appliedTargets.ContainsKey(g.Index)) {
                        g.Selected = true;
                        g.Backend = _appliedTargets[g.Index];
                    } else {
                        g.Selected = (_gpus.Count == 1);
                    }
                }
            } else if (_gpus.Count == 1) {
                _gpus[0].Selected = true;
            } else if (_gpus.Count > 1) {
                bool anyMining = false;
                foreach (var g in _gpus) if (g.IsMining) anyMining = true;
                foreach (var g in _gpus) g.Selected = anyMining ? g.IsMining : true;
            }

            CheckGen2Status();
        }

        void CheckVulkanNow() {
            string outStr;
            int rc = RunProcessCapture(_daemonSourceExe, "--check-vulkan", 5000, out outStr);
            if (rc == 0 || (outStr != null && outStr.StartsWith("OK"))) _vkCheckStatus = "OK";
            else if (rc == 2 || (outStr != null && outStr.Contains("NO_LOADER"))) _vkCheckStatus = "NO_LOADER";
            else _vkCheckStatus = "NO_NVIDIA_ICD";
        }

        void ReadInstalledServiceConfig() {
            _appliedTargets.Clear();
            _serviceInstalled = false;
            string xmlOut;
            int rc = RunProcessCapture("schtasks.exe", "/Query /TN \"" + TaskName + "\" /XML", 4000, out xmlOut);
            if (rc != 0 || string.IsNullOrEmpty(xmlOut)) return;
            _serviceInstalled = true;

            // Parse <Arguments>...</Arguments>
            var mArg = Regex.Match(xmlOut, @"<Arguments>(.*?)</Arguments>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (!mArg.Success) return;
            string args = System.Net.WebUtility.HtmlDecode(mArg.Groups[1].Value);

            var mTargets = Regex.Match(args, @"--targets\s+([^\s""]+)", RegexOptions.IgnoreCase);
            if (mTargets.Success) {
                foreach (string tok in mTargets.Groups[1].Value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)) {
                    int c = tok.IndexOf(':');
                    int idx;
                    if (c > 0 && int.TryParse(tok.Substring(0, c), out idx)) {
                        _appliedTargets[idx] = tok.Substring(c + 1).Trim().ToLowerInvariant();
                    }
                }
                return;
            }

            // Fallback: --backend + --device
            var mBack = Regex.Match(args, @"--backend\s+([^\s""]+)", RegexOptions.IgnoreCase);
            var mDev  = Regex.Match(args, @"--device\s+([^\s""]+)", RegexOptions.IgnoreCase);
            string b = mBack.Success ? mBack.Groups[1].Value.ToLowerInvariant() : "vulkan";
            if (b != "cuda") b = "vulkan";
            if (mDev.Success) {
                string ds = mDev.Groups[1].Value;
                if (ds.Equals("all", StringComparison.OrdinalIgnoreCase) || ds.Equals("auto", StringComparison.OrdinalIgnoreCase)) {
                    _appliedTargets[0] = b;
                } else {
                    foreach (string tok in ds.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)) {
                        int idx; if (int.TryParse(tok.Trim(), out idx)) _appliedTargets[idx] = b;
                    }
                }
            } else {
                _appliedTargets[0] = b;
            }
        }

        void CheckGen2Status() {
            string xmlOut;
            int rc = RunProcessCapture("schtasks.exe", "/Query /TN \"" + Gen2TaskName + "\" /XML", 4000, out xmlOut);
            if (rc != 0 || string.IsNullOrEmpty(xmlOut)) {
                _gen2State = "absent";
                return;
            }
            if (xmlOut.IndexOf("gen2-sync-wrapper.cmd", StringComparison.OrdinalIgnoreCase) >= 0)
                _gen2State = "synced";
            else
                _gen2State = "unsynced";
        }

        void TriggerBackgroundPoll() {
            if (_pollBusy) return;
            _pollBusy = true;
            ThreadPool.QueueUserWorkItem(_ => {
                try {
                    // 1. Service running / paused / live CPU%
                    bool running = false;
                    string cpuTxt = "-";
                    DateTime? procStart = null;
                    try {
                        Mutex m;
                        if (Mutex.TryOpenExisting(MutexName, out m)) { running = true; m.Dispose(); }
                    } catch (UnauthorizedAccessException) { running = true; } catch {}

                    try {
                        var procs = Process.GetProcessesByName("NvKeepAlive");
                        if (procs.Length > 0) {
                            running = true;
                            var p = procs[0];
                            try {
                                procStart = p.StartTime;
                                TimeSpan curCpu = p.TotalProcessorTime;
                                DateTime now = DateTime.UtcNow;
                                if (_lastPid == p.Id && _lastCpuWall != DateTime.MinValue) {
                                    double dtMs = (now - _lastCpuWall).TotalMilliseconds;
                                    if (dtMs > 200) {
                                        double pct = (curCpu - _lastCpuTime).TotalMilliseconds / (dtMs * Environment.ProcessorCount) * 100.0;
                                        if (pct < 0) pct = 0;
                                        if (pct > 100) pct = 100;
                                        cpuTxt = pct.ToString("F2", CultureInfo.InvariantCulture) + "%";
                                    }
                                } else {
                                    cpuTxt = "0.00%";
                                }
                                _lastPid = p.Id;
                                _lastCpuTime = curCpu;
                                _lastCpuWall = now;
                            } catch { cpuTxt = "-"; }
                        } else {
                            _lastPid = -1;
                            _lastCpuWall = DateTime.MinValue;
                            cpuTxt = "-";
                        }
                        foreach (var pr in procs) pr.Dispose();
                    } catch {}

                    bool paused = File.Exists(PauseFile);
                    if (paused && running) cpuTxt = "0.00%";

                    // 2. Live nvidia-smi telemetry (PCI Bus ID, VRAM, Temp, Power, PCIe Max Gen & Width)
                    string smiOut;
                    var smiMap = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
                    var smiList = new List<string[]>();
                    if (RunProcessCapture("nvidia-smi.exe",
                        "--query-gpu=pci.bus_id,memory.used,memory.total,temperature.gpu,power.draw,pcie.link.gen.max,pcie.link.width.current --format=csv,noheader,nounits",
                        3500, out smiOut) == 0 && !string.IsNullOrEmpty(smiOut)) {
                        foreach (string line in smiOut.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)) {
                            string[] cols = line.Split(',');
                            if (cols.Length < 7) continue;
                            for (int i = 0; i < cols.Length; i++) cols[i] = cols[i].Trim();
                            string bus = cols[0].ToLowerInvariant();
                            if (bus.Length == 16 && bus[8] == ':') bus = bus.Substring(4);
                            smiMap[bus] = cols;
                            smiList.Add(cols);
                        }
                    }

                    // 3. Short-window TDR counts (last 5 min + since service/UI start)
                    DateTime startRef = procStart ?? _sessionStart;
                    int tdr5 = CountNvlddmkmSince(DateTime.Now.AddMinutes(-5));
                    int tdrS = CountNvlddmkmSince(startRef);

                    // 4. Tail keepalive.log
                    string logTxt = "";
                    if (File.Exists(LogFile)) {
                        try {
                            string[] all = File.ReadAllLines(LogFile, Encoding.UTF8);
                            int from = Math.Max(0, all.Length - 60);
                            logTxt = string.Join(Environment.NewLine, all, from, all.Length - from);
                        } catch {}
                    }

                    Dispatcher.BeginInvoke((Action)(() => {
                        _serviceRunning = running;
                        _servicePaused = paused;
                        _liveCpuText = cpuTxt;
                        _tdr5m = tdr5;
                        _tdrStart = tdrS;

                        string pcie40 = "";
                        for (int i = 0; i < _gpus.Count; i++) {
                            var g = _gpus[i];
                            string[] cols = null;
                            if (!string.IsNullOrEmpty(g.PciBusId) && smiMap.ContainsKey(g.PciBusId)) cols = smiMap[g.PciBusId];
                            else if (i < smiList.Count) cols = smiList[i];
                            if (cols != null) {
                                g.UsedVram = cols[1];
                                g.LiveTotalVram = cols[2];
                                g.TempC = cols[3];
                                g.PowerW = cols[4].Split('.')[0];
                                g.PcieText = "Gen" + cols[5] + " x" + cols[6];
                                if (g.IsCmp40hx) pcie40 = g.PcieText;
                            }
                        }
                        if (!string.IsNullOrEmpty(pcie40)) _gen2PcieSummary = pcie40;

                        if (logTxt != _lastLogText) {
                            _lastLogText = logTxt;
                            _logBox.Text = string.IsNullOrEmpty(logTxt) ? T("(журнал пока пуст)", "(log is empty)") : logTxt;
                            _logBox.ScrollToEnd();
                        }

                        RefreshTextsAndState();
                    }));
                } finally {
                    _pollBusy = false;
                }
            });
        }

        static int CountNvlddmkmSince(DateTime sinceLocal) {
            try {
                DateTime utc = sinceLocal.ToUniversalTime();
                string iso = utc.ToString("yyyy-MM-ddTHH:mm:ss.000Z", CultureInfo.InvariantCulture);
                string query = "*[System[Provider[@Name='nvlddmkm'] and (EventID=14 or EventID=153) and TimeCreated[@SystemTime>='" + iso + "']]]";
                var elq = new System.Diagnostics.Eventing.Reader.EventLogQuery("System", System.Diagnostics.Eventing.Reader.PathType.LogName, query);
                int count = 0;
                using (var reader = new System.Diagnostics.Eventing.Reader.EventLogReader(elq)) {
                    while (reader.ReadEvent() != null) {
                        count++;
                        if (count >= 999) break;
                    }
                }
                return count;
            } catch {
                return 0;
            }
        }

        // ===================== State Rendering =====================
        List<GpuItem> SelectedGpus() {
            var res = new List<GpuItem>();
            if (_gpus.Count == 1) { res.Add(_gpus[0]); return res; }
            foreach (var g in _gpus) if (g.Selected) res.Add(g);
            return res;
        }

        string DraftTargetsSpec() {
            var parts = new List<string>();
            foreach (var g in SelectedGpus()) parts.Add(g.Index + ":" + g.Backend);
            return string.Join(",", parts.ToArray());
        }

        string AppliedTargetsSpec() {
            var keys = new List<int>(_appliedTargets.Keys);
            keys.Sort();
            var parts = new List<string>();
            foreach (int k in keys) parts.Add(k + ":" + _appliedTargets[k]);
            return string.Join(",", parts.ToArray());
        }

        bool HasPendingServiceChanges() {
            return _serviceInstalled && !string.Equals(DraftTargetsSpec(), AppliedTargetsSpec(), StringComparison.OrdinalIgnoreCase);
        }

        void RefreshTextsAndState() {
            // Language toggle style
            _btnRu.Style = _isRu ? PrimaryBtnStyle() : MakeButtonStyle(Brushes.Transparent, Brushes.Transparent, FgSecondary, false);
            _btnEn.Style = !_isRu ? PrimaryBtnStyle() : MakeButtonStyle(Brushes.Transparent, Brushes.Transparent, FgSecondary, false);

            _lblStatusTitle.Text = T("ВИДЕОКАРТЫ NVIDIA И ДВИЖКИ", "NVIDIA GPUS & BACKENDS");
            _lblTdr5m.Text = T("TDR (5 мин):", "TDR (5m):");
            _lblTdrStart.Text = T("С запуска:", "Since start:");
            _valTdr5m.Text = _tdr5m.ToString();
            _valTdr5m.Foreground = _tdr5m == 0 ? GreenFg : RedFg;
            _valTdrStart.Text = _tdrStart.ToString();
            _valTdrStart.Foreground = _tdrStart == 0 ? GreenFg : RedFg;
            _valCpu.Text = _liveCpuText;

            // Service status pill
            var appParts = new List<string>();
            var appKeys = new List<int>(_appliedTargets.Keys); appKeys.Sort();
            foreach (int k in appKeys) {
                string bName = _appliedTargets[k] == "cuda" ? "CUDA" : "Vulkan";
                appParts.Add((_gpus.Count > 1 ? ("GPU" + k + ":") : "") + bName);
            }
            string appSum = appParts.Count > 0 ? string.Join(", ", appParts.ToArray()) : "-";

            if (_serviceRunning && !_servicePaused) {
                _statusPill.Background = GreenBg; _statusPill.BorderBrush = GreenBorder;
                _statusDot.Fill = GreenFg; _statusText.Foreground = GreenFg;
                _statusText.Text = T("Работает (" + appSum + ")", "Running (" + appSum + ")");
                _btnPause.Content = T("Пауза", "Pause");
            } else if (_serviceRunning && _servicePaused) {
                _statusPill.Background = YellowBg; _statusPill.BorderBrush = YellowBorder;
                _statusDot.Fill = YellowFg; _statusText.Foreground = YellowFg;
                _statusText.Text = T("На паузе", "Paused");
                _btnPause.Content = T("Возобновить", "Resume");
            } else {
                _statusPill.Background = RedBg; _statusPill.BorderBrush = RedBorder;
                _statusDot.Fill = RedFg; _statusText.Foreground = RedFg;
                _statusText.Text = _serviceInstalled ? T("Остановлена", "Stopped") : T("Не установлена", "Not installed");
                _btnPause.Content = T("Пауза", "Pause");
            }

            // Legend
            if (_vkCheckStatus == "OK") {
                _vkBadge.Background = GreenBg; _vkBadgeText.Foreground = GreenFg;
                _vkBadgeText.Text = T("Доступен", "Available");
            } else {
                _vkBadge.Background = YellowBg; _vkBadgeText.Foreground = YellowFg;
                _vkBadgeText.Text = T("Не найден (гайд)", "Missing (guide)");
            }
            _cudaBadgeText.Text = T("Доступен", "Available");
            _legendVk.Text = T("Минимальный расход VRAM (~5-20 MiB). Рекомендуется для большинства систем.",
                               "Minimal VRAM footprint (~5-20 MiB). Recommended for most NVIDIA systems.");
            _legendCuda.Text = T("Повышенный расход VRAM (~200-300 MiB). Если нет Vulkan или на карте он нестабилен.",
                                 "Higher VRAM footprint (~200-300 MiB). Use if Vulkan is missing or unstable on a GPU.");

            // Action buttons + dynamic accent (Install/Reinstall -> Test 25s when installed & up-to-date)
            _btnTest.Content = T("Тест 25с", "Test 25s");
            _btnRemove.Content = T("Удалить", "Uninstall");
            bool pending = HasPendingServiceChanges();
            if (!_serviceInstalled) {
                _btnInstall.Content = T("Установить", "Install");
                _btnInstall.Style = PrimaryBtnStyle();
                _btnTest.Style = SecondaryBtnStyle();
            } else if (pending) {
                _btnInstall.Content = T("Переустановить", "Reinstall");
                _btnInstall.Style = PrimaryBtnStyle();
                _btnTest.Style = SecondaryBtnStyle();
            } else {
                _btnInstall.Content = T("Переустановить", "Reinstall");
                _btnInstall.Style = SecondaryBtnStyle();
                _btnTest.Style = PrimaryBtnStyle();
            }
            _btnPause.IsEnabled = _serviceRunning;
            _btnTest.IsEnabled = !_testRunning;

            _pendingHintText.Text = T("Настройки движка/карт изменены. Нажмите «Переустановить», чтобы применить их к службе.",
                                      "Backend/GPU selection changed. Click 'Reinstall' to apply changes to the service.");
            _pendingHintBox.Visibility = pending ? Visibility.Visible : Visibility.Collapsed;

            _lblLogTitle.Text = T("Журнал событий (keepalive.log)", "Event Log (keepalive.log)");
            _btnOpenFolder.Content = T("Открыть папку", "Open folder");

            RenderGpuRows();
            RenderGen2Block();
            RenderCoopmat();
        }

        void RenderGpuRows() {
            _gpuListPanel.Children.Clear();
            if (_gpus.Count == 0) {
                _gpuListPanel.Children.Add(new TextBlock {
                    Text = T("Видеокарты NVIDIA не обнаружены (убедитесь, что драйвер NVIDIA установлен).",
                             "No NVIDIA GPUs detected (verify that an NVIDIA driver is installed)."),
                    Foreground = RedFg, FontSize = 12, Margin = new Thickness(2, 4, 2, 4)
                });
                return;
            }

            bool multi = _gpus.Count > 1;
            foreach (var gpu in _gpus) {
                var g = gpu;
                bool isSel = multi ? g.Selected : true;
                var row = new Border {
                    Background = isSel ? BgRowSel : BgRow,
                    BorderBrush = isSel ? BorderRowSel : BorderCard,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(10, 8, 10, 8),
                    Margin = new Thickness(0, 0, 0, 6)
                };

                var dock = new DockPanel();

                // Per-GPU segmented Vulkan | CUDA control
                var segBorder = new Border {
                    Background = BgSeg, BorderBrush = BorderSeg, BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(5), Padding = new Thickness(2),
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0),
                    Opacity = isSel ? 1.0 : 0.45
                };
                var segSp = new StackPanel { Orientation = Orientation.Horizontal };
                bool vkMissing = (_vkCheckStatus != "OK");
                var bVk = new Button {
                    Content = vkMissing ? "Vulkan !" : "Vulkan",
                    Padding = new Thickness(8, 3, 8, 3), FontSize = 11,
                    Style = (g.Backend == "vulkan")
                        ? PrimaryBtnStyle()
                        : MakeButtonStyle(Brushes.Transparent, Brushes.Transparent, vkMissing ? YellowFg : FgSecondary, false)
                };
                var bCu = new Button {
                    Content = "CUDA", Margin = new Thickness(2, 0, 0, 0),
                    Padding = new Thickness(8, 3, 8, 3), FontSize = 11,
                    Style = (g.Backend == "cuda")
                        ? PrimaryBtnStyle()
                        : MakeButtonStyle(Brushes.Transparent, Brushes.Transparent, FgSecondary, false)
                };
                bVk.Click += (s, e) => {
                    g.Selected = true;
                    g.Backend = "vulkan";
                    if (_vkCheckStatus != "OK") ShowVulkanGuideModal();
                    RefreshTextsAndState();
                };
                bCu.Click += (s, e) => {
                    g.Selected = true;
                    g.Backend = "cuda";
                    RefreshTextsAndState();
                };
                segSp.Children.Add(bVk);
                segSp.Children.Add(bCu);
                segBorder.Child = segSp;
                DockPanel.SetDock(segBorder, Dock.Right);
                dock.Children.Add(segBorder);

                // Left side: optional checkbox (ONLY if 2+ GPUs) + name + live telemetry
                var leftSp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                if (multi) {
                    var cb = new CheckBox {
                        IsChecked = g.Selected, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0)
                    };
                    cb.Checked   += (s, e) => { g.Selected = true; RefreshTextsAndState(); };
                    cb.Unchecked += (s, e) => { g.Selected = false; RefreshTextsAndState(); };
                    leftSp.Children.Add(cb);
                }

                var infoSp = new StackPanel();
                var nameRow = new StackPanel { Orientation = Orientation.Horizontal };
                if (multi) {
                    nameRow.Children.Add(new TextBlock {
                        Text = "[GPU " + g.Index + "] ", FontFamily = new FontFamily("Consolas"), FontSize = 12,
                        Foreground = Accent, VerticalAlignment = VerticalAlignment.Center
                    });
                }
                nameRow.Children.Add(new TextBlock { Text = g.Name, FontWeight = FontWeights.Medium, Foreground = FgPrimary, VerticalAlignment = VerticalAlignment.Center });
                if (g.IsMining) {
                    var mBdg = new Border { Background = MiningBg, CornerRadius = new CornerRadius(3), Padding = new Thickness(5, 0, 5, 1), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                    mBdg.Child = new TextBlock { Text = "CMP / Mining", FontSize = 10, Foreground = MiningFg };
                    nameRow.Children.Add(mBdg);
                }
                infoSp.Children.Add(nameRow);

                string totVram = !string.IsNullOrEmpty(g.LiveTotalVram) ? g.LiveTotalVram : g.TotalVramMiB.ToString();
                string pciPart = !string.IsNullOrEmpty(g.PciBusId) ? g.PciBusId : g.DeviceIdHex;
                string pciePart = !string.IsNullOrEmpty(g.PcieText) ? ("  |  " + g.PcieText) : "";
                infoSp.Children.Add(new TextBlock {
                    Text = string.Format("{0}{1}  |  {2}/{3} MiB  |  {4} C  |  {5} W", pciPart, pciePart, g.UsedVram, totVram, g.TempC, g.PowerW),
                    FontFamily = new FontFamily("Consolas"), FontSize = 11, Foreground = FgMuted, Margin = new Thickness(0, 2, 0, 0)
                });
                leftSp.Children.Add(infoSp);
                dock.Children.Add(leftSp);

                row.Child = dock;
                _gpuListPanel.Children.Add(row);
            }
        }

        void RenderGen2Block() {
            bool show = false;
            foreach (var g in SelectedGpus()) if (g.IsCmp40hx) { show = true; break; }
            _gen2Card.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (!show) return;

            _lblGen2Title.Text = T("Синхронизация с PCIe Gen2 Unlock", "PCIe Gen2 Unlock Sync");
            _descGen2.Text = T("Паузит KeepAlive на время 40HXInstaller -gen2, чинит Gen2 на чипсетных/нижних слотах (PCIe Bridge Retrain) и восстанавливает Gen2 после сна.",
                               "Pauses KeepAlive during 40HXInstaller -gen2, fixes Gen2 on chipset/secondary PCIe slots (Bridge Retrain), and restores Gen2 after sleep.");

            if (_gen2State == "absent") {
                _gen2Badge.Background = BtnSecBg; _gen2Badge.BorderBrush = BtnSecBorder; _gen2BadgeText.Foreground = FgSecondary;
                _gen2BadgeText.Text = T("Нет", "Not installed");
                _btnGen2Sync.Content = T("Установить", "Install");
                _btnGen2Sync.Style = SecondaryBtnStyle();
            } else if (_gen2State == "unsynced") {
                _gen2Badge.Background = YellowBg; _gen2Badge.BorderBrush = YellowBorder; _gen2BadgeText.Foreground = YellowFg;
                _gen2BadgeText.Text = T("Есть (не синхр.)", "Installed (unsynced)");
                _btnGen2Sync.Content = T("Синхронизировать", "Sync now");
                _btnGen2Sync.Style = PrimaryBtnStyle();
            } else {
                _gen2Badge.Background = GreenBg; _gen2Badge.BorderBrush = GreenBorder; _gen2BadgeText.Foreground = GreenFg;
                string suffix = !string.IsNullOrEmpty(_gen2PcieSummary) ? (" | " + _gen2PcieSummary) : "";
                _gen2BadgeText.Text = T("Оптимизировано", "Optimized") + suffix;
                _btnGen2Sync.Content = T("Отключить", "Restore");
                _btnGen2Sync.Style = SecondaryBtnStyle();
            }
        }

        void RenderCoopmat() {
            bool show = false;
            foreach (var g in SelectedGpus()) if (g.IsMining) { show = true; break; }
            _coopmatCard.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (!show) return;

            _descCoopmat.Text = T("Отключает VK_NV_cooperative_matrix2. Нужно только для стоковых CMP без EFI-анлока (llama.cpp / LM Studio). После анлока выключите.",
                                  "Disables VK_NV_cooperative_matrix2. Only needed for stock CMP GPUs without EFI unlock (llama.cpp / LM Studio). Turn off after unlock.");
            _toggleTrack.Background = _coopmatDraft ? Accent : BgWindow;
            _toggleTrack.BorderBrush = _coopmatDraft ? Accent : Br("#8C8C8C");
            _toggleThumb.Fill = _coopmatDraft ? AccentDarkFg : Br("#CCCCCC");
            _toggleThumb.HorizontalAlignment = _coopmatDraft ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            _coopmatSwitchLbl.Text = _coopmatDraft ? T("Вкл", "On") : T("Выкл", "Off");
            _btnApplyCoopmat.Content = T("Применить", "Apply");

            bool dirty = (_coopmatDraft != _coopmatApplied);
            _btnApplyCoopmat.IsEnabled = dirty;
            if (dirty) {
                _coopmatAppliedBadge.Background = YellowBg; _coopmatAppliedBadge.BorderBrush = YellowBorder;
                _coopmatAppliedText.Foreground = YellowFg;
                _coopmatAppliedText.Text = T("Есть изменения", "Unsaved change");
            } else if (_coopmatApplied) {
                _coopmatAppliedBadge.Background = BlueBadgeBg; _coopmatAppliedBadge.BorderBrush = BorderRowSel;
                _coopmatAppliedText.Foreground = Accent;
                _coopmatAppliedText.Text = T("В системе: Вкл", "Applied: On");
            } else {
                _coopmatAppliedBadge.Background = Br("#2F2F2F"); _coopmatAppliedBadge.BorderBrush = BorderCard;
                _coopmatAppliedText.Foreground = FgSecondary;
                _coopmatAppliedText.Text = T("В системе: Выкл", "Applied: Off");
            }
        }

        // ===================== Actions =====================
        void AppendUiLog(string msg) {
            try {
                Directory.CreateDirectory(InstallDir);
                File.AppendAllText(LogFile, string.Format("[{0:yyyy-MM-dd HH:mm:ss}] [UI] {1}{2}", DateTime.Now, msg, Environment.NewLine), Encoding.UTF8);
            } catch {}
            TriggerBackgroundPoll();
        }

        void OnInstallClick(object sender, RoutedEventArgs e) {
            var sel = SelectedGpus();
            if (sel.Count == 0) {
                MessageBox.Show(this, T("Выберите хотя бы одну видеокарту NVIDIA.", "Select at least one NVIDIA GPU first."), "NV-KeepAlive", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            bool needsVulkan = false;
            foreach (var g in sel) if (g.Backend == "vulkan") needsVulkan = true;
            if (needsVulkan && _vkCheckStatus != "OK") {
                ShowVulkanGuideModal();
                return;
            }
            if (!File.Exists(_daemonSourceExe)) {
                MessageBox.Show(this, T("Не найден файл NvKeepAlive.exe рядом с программой.", "NvKeepAlive.exe was not found next to NvKeepAliveUI.exe."), "NV-KeepAlive", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            try {
                StopRunningService();
                Directory.CreateDirectory(InstallDir);
                if (!string.Equals(System.IO.Path.GetFullPath(_daemonSourceExe), System.IO.Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase)) {
                    File.Copy(_daemonSourceExe, InstalledExe, true);
                }
                if (File.Exists(PauseFile)) File.Delete(PauseFile);

                string targets = DraftTargetsSpec();
                string args = string.Format("--targets {0} --quiet --log \"{1}\"", targets, LogFile);

                string psScript =
                    "$ErrorActionPreference='Stop';" +
                    "$act = New-ScheduledTaskAction -Execute '" + InstalledExe.Replace("'", "''") + "' -Argument '" + args.Replace("'", "''") + "' -WorkingDirectory '" + InstallDir.Replace("'", "''") + "';" +
                    "$t1 = New-ScheduledTaskTrigger -AtStartup;" +
                    "$t2 = New-ScheduledTaskTrigger -AtLogOn;" +
                    "$pr = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest;" +
                    "$st = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Days 0) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1);" +
                    "Register-ScheduledTask -TaskName '" + TaskName + "' -Action $act -Trigger @($t1,$t2) -Principal $pr -Settings $st -Force | Out-Null;" +
                    "Start-ScheduledTask -TaskName '" + TaskName + "';";

                string psOut;
                int rc = RunPowerShell(psScript, 12000, out psOut);
                if (rc != 0) throw new Exception(psOut);

                _sessionStart = DateTime.Now;
                ReadInstalledServiceConfig();
                AppendUiLog("Service installed/updated: --targets " + targets);
                RefreshTextsAndState();
            } catch (Exception ex) {
                MessageBox.Show(this, T("Ошибка установки службы:\n", "Failed to install service:\n") + ex.Message, "NV-KeepAlive", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        void OnPauseClick(object sender, RoutedEventArgs e) {
            try {
                Directory.CreateDirectory(InstallDir);
                if (File.Exists(PauseFile)) {
                    File.Delete(PauseFile);
                    AppendUiLog("Pause cleared by user.");
                } else {
                    File.WriteAllText(PauseFile, "paused-by-ui");
                    AppendUiLog("Paused by user.");
                }
                TriggerBackgroundPoll();
            } catch (Exception ex) {
                MessageBox.Show(this, ex.Message, "NV-KeepAlive", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        void OnRemoveClick(object sender, RoutedEventArgs e) {
            var r = MessageBox.Show(this,
                T("Остановить и удалить службу NV-KeepAlive из системы?", "Stop and uninstall the NV-KeepAlive service from this PC?"),
                "NV-KeepAlive", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return;

            try {
                // Restore Gen2 Unlock task if it was synced before removing InstallDir
                if (_gen2State == "synced") RestoreGen2Task();
                StopRunningService();
                string dummy;
                RunProcessCapture("schtasks.exe", "/Delete /TN \"" + TaskName + "\" /F", 5000, out dummy);
                if (File.Exists(PauseFile)) File.Delete(PauseFile);
                _serviceInstalled = false;
                _serviceRunning = false;
                _appliedTargets.Clear();
                AppendUiLog("Service NV-KeepAlive removed.");
                RefreshTextsAndState();
            } catch (Exception ex) {
                MessageBox.Show(this, ex.Message, "NV-KeepAlive", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        void OnTestClick(object sender, RoutedEventArgs e) {
            if (_testRunning) return;
            var sel = SelectedGpus();
            if (sel.Count == 0) {
                MessageBox.Show(this, T("Выберите хотя бы одну видеокарту NVIDIA.", "Select at least one NVIDIA GPU first."), "NV-KeepAlive", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            bool needsVulkan = false;
            foreach (var g in sel) if (g.Backend == "vulkan") needsVulkan = true;
            if (needsVulkan && _vkCheckStatus != "OK") { ShowVulkanGuideModal(); return; }

            _testRunning = true;
            _testProgressWrap.Visibility = Visibility.Visible;
            _testProgressBar.Value = 0;
            _testProgressPct.Text = "0%";
            _testProgressLbl.Text = T("Проверка анти-TDR (25с)...", "Running anti-TDR self-test (25s)...");
            _btnTest.IsEnabled = false;

            bool needTempProc = !_serviceRunning || _servicePaused;
            string targets = DraftTargetsSpec();
            string exePath = File.Exists(InstalledExe) ? InstalledExe : _daemonSourceExe;

            ThreadPool.QueueUserWorkItem(_ => {
                DateTime tStart = DateTime.Now;
                Process tempProc = null;
                try {
                    if (needTempProc && File.Exists(exePath)) {
                        tempProc = Process.Start(new ProcessStartInfo {
                            FileName = exePath,
                            Arguments = string.Format("--targets {0} --sec 26 --no-mutex --quiet", targets),
                            UseShellExecute = false, CreateNoWindow = true
                        });
                    }
                    for (int s = 1; s <= 25; s++) {
                        Thread.Sleep(1000);
                        int pct = s * 4;
                        Dispatcher.BeginInvoke((Action)(() => {
                            _testProgressBar.Value = pct;
                            _testProgressPct.Text = pct + "%";
                        }));
                    }
                    int errs = CountNvlddmkmSince(tStart);
                    Dispatcher.BeginInvoke((Action)(() => {
                        _testRunning = false;
                        _btnTest.IsEnabled = true;
                        _testProgressLbl.Text = errs == 0
                            ? T("Тест пройден: 0 ошибок nvlddmkm за 25с", "Self-test passed: 0 nvlddmkm errors in 25s")
                            : T("Обнаружено ошибок nvlddmkm: " + errs, "Detected nvlddmkm errors: " + errs);
                        AppendUiLog(string.Format("25s anti-TDR test finished (targets={0}): {1} nvlddmkm errors.", targets, errs));
                    }));
                } catch (Exception ex) {
                    Dispatcher.BeginInvoke((Action)(() => {
                        _testRunning = false;
                        _btnTest.IsEnabled = true;
                        _testProgressLbl.Text = "Error: " + ex.Message;
                    }));
                } finally {
                    if (tempProc != null) {
                        try { if (!tempProc.HasExited) tempProc.Kill(); } catch {}
                        tempProc.Dispose();
                    }
                }
            });
        }

        void OnApplyCoopmatClick(object sender, RoutedEventArgs e) {
            try {
                Environment.SetEnvironmentVariable("GGML_VK_DISABLE_COOPMAT2", _coopmatDraft ? "1" : null, EnvironmentVariableTarget.Machine);
                UIntPtr res;
                SendMessageTimeout(new IntPtr(0xFFFF), 0x001A, UIntPtr.Zero, "Environment", 0x0002, 2000, out res);
                _coopmatApplied = _coopmatDraft;
                RenderCoopmat();
                AppendUiLog("System env GGML_VK_DISABLE_COOPMAT2=" + (_coopmatApplied ? "1" : "(removed)"));
            } catch (Exception ex) {
                MessageBox.Show(this, ex.Message, "NV-KeepAlive", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        void OnGen2SyncClick(object sender, RoutedEventArgs e) {
            if (_gen2State == "absent") {
                ShowGen2InfoModal();
                return;
            }
            if (_gen2State == "synced") {
                try {
                    RestoreGen2Task();
                    CheckGen2Status();
                    AppendUiLog("Restored original '40HX PCIe Gen2 Bring-up' scheduled task.");
                    RefreshTextsAndState();
                } catch (Exception ex) {
                    MessageBox.Show(this, ex.Message, "NV-KeepAlive", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                return;
            }

            // Sync: backup original task, create wrapper with pause + original 40HXInstaller + our --retrain-gen2 + wake trigger
            try {
                Directory.CreateDirectory(InstallDir);
                if (!File.Exists(InstalledExe) && File.Exists(_daemonSourceExe)) {
                    File.Copy(_daemonSourceExe, InstalledExe, true);
                }

                string xmlOut;
                if (RunProcessCapture("schtasks.exe", "/Query /TN \"" + Gen2TaskName + "\" /XML", 5000, out xmlOut) != 0 || string.IsNullOrEmpty(xmlOut))
                    throw new Exception("Cannot read scheduled task '" + Gen2TaskName + "'.");

                var mCmd = Regex.Match(xmlOut, @"<Command>(.*?)</Command>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                var mArg = Regex.Match(xmlOut, @"<Arguments>(.*?)</Arguments>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                if (!mCmd.Success) throw new Exception("Task XML has no <Command> element.");

                string origExe = System.Net.WebUtility.HtmlDecode(mCmd.Groups[1].Value).Trim().Trim('"');
                string origArgs = mArg.Success ? System.Net.WebUtility.HtmlDecode(mArg.Groups[1].Value).Trim() : "-gen2 -silent";

                if (!File.Exists(Gen2OrigXmlPath)) File.WriteAllText(Gen2OrigXmlPath, xmlOut, Encoding.Unicode);
                File.WriteAllText(Gen2OrigCmdPath, origExe + Environment.NewLine + origArgs, Encoding.UTF8);

                string wrapper =
                    "@echo off\r\n" +
                    "setlocal\r\n" +
                    "set \"KA_DIR=" + InstallDir + "\"\r\n" +
                    "set \"PAUSE_FILE=%KA_DIR%\\keepalive.pause\"\r\n" +
                    "if exist \"%KA_DIR%\" type nul > \"%PAUSE_FILE%\"\r\n" +
                    "timeout /t 2 /nobreak >nul\r\n" +
                    "if exist \"" + origExe + "\" (\r\n" +
                    "    \"" + origExe + "\" " + origArgs + "\r\n" +
                    ")\r\n" +
                    "if exist \"%KA_DIR%\\NvKeepAlive.exe\" (\r\n" +
                    "    \"%KA_DIR%\\NvKeepAlive.exe\" --retrain-gen2 --wait 20 --log \"%KA_DIR%\\keepalive.log\"\r\n" +
                    ")\r\n" +
                    "if exist \"%PAUSE_FILE%\" del /F /Q \"%PAUSE_FILE%\" >nul 2>&1\r\n" +
                    "endlocal\r\n";
                File.WriteAllText(Gen2WrapperPath, wrapper, Encoding.ASCII);

                // Update task action + add Wake-from-Sleep trigger (Event ID 1 Power-Troubleshooter)
                string ps =
                    "$ErrorActionPreference='Stop';" +
                    "$t = Get-ScheduledTask -TaskName '" + Gen2TaskName + "';" +
                    "$act = New-ScheduledTaskAction -Execute 'cmd.exe' -Argument '/c \"" + Gen2WrapperPath.Replace("'", "''") + "\"' -WorkingDirectory '" + InstallDir.Replace("'", "''") + "';" +
                    "$cim = Get-CimClass -ClassName MSFT_TaskEventTrigger -Namespace Root/Microsoft/Windows/TaskScheduler;" +
                    "$wake = New-CimInstance -CimClass $cim -ClientOnly;" +
                    "$wake.Enabled = $true;" +
                    "$wake.Delay = 'PT5S';" +
                    "$wake.Subscription = '<QueryList><Query Id=\"0\" Path=\"System\"><Select Path=\"System\">*[System[Provider[@Name=''Microsoft-Windows-Power-Troubleshooter''] and EventID=1]]</Select></Query></QueryList>';" +
                    "$trigs = @($t.Triggers) + @($wake);" +
                    "Set-ScheduledTask -TaskName '" + Gen2TaskName + "' -Action $act -Trigger $trigs | Out-Null;";

                string psOut;
                int rc = RunPowerShell(ps, 12000, out psOut);
                if (rc != 0) throw new Exception(psOut);

                CheckGen2Status();
                AppendUiLog("Synced '" + Gen2TaskName + "' with NV-KeepAlive pause + built-in PCIe Gen2 chipset-bridge retrain + wake trigger.");

                // Run immediate retrain check in background (no-op if already Gen2)
                ThreadPool.QueueUserWorkItem(_ => {
                    string dummy;
                    string exe = File.Exists(InstalledExe) ? InstalledExe : _daemonSourceExe;
                    RunProcessCapture(exe, "--retrain-gen2 --wait 5 --log \"" + LogFile + "\"", 10000, out dummy);
                    TriggerBackgroundPoll();
                });
                RefreshTextsAndState();
            } catch (Exception ex) {
                MessageBox.Show(this, T("Ошибка синхронизации Gen2 Unlock:\n", "Failed to sync Gen2 Unlock:\n") + ex.Message, "NV-KeepAlive", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        void RestoreGen2Task() {
            if (File.Exists(Gen2OrigXmlPath)) {
                string outStr;
                int rc = RunProcessCapture("schtasks.exe", "/Create /TN \"" + Gen2TaskName + "\" /XML \"" + Gen2OrigXmlPath + "\" /F", 8000, out outStr);
                if (rc != 0) throw new Exception(outStr);
            } else if (File.Exists(Gen2OrigCmdPath)) {
                string[] lines = File.ReadAllLines(Gen2OrigCmdPath, Encoding.UTF8);
                if (lines.Length >= 1) {
                    string exe = lines[0].Trim();
                    string arg = lines.Length >= 2 ? lines[1].Trim() : "-gen2 -silent";
                    string ps = "$act = New-ScheduledTaskAction -Execute '" + exe.Replace("'", "''") + "' -Argument '" + arg.Replace("'", "''") + "'; Set-ScheduledTask -TaskName '" + Gen2TaskName + "' -Action $act | Out-Null;";
                    string psOut; RunPowerShell(ps, 8000, out psOut);
                }
            }
        }

        void StopRunningService() {
            string dummy;
            RunProcessCapture("schtasks.exe", "/End /TN \"" + TaskName + "\"", 4000, out dummy);
            try {
                foreach (var p in Process.GetProcessesByName("NvKeepAlive")) {
                    try { p.Kill(); p.WaitForExit(2000); } catch {}
                    p.Dispose();
                }
            } catch {}
            Thread.Sleep(300);
        }

        // ===================== Modal 1: Vulkan Missing Installation Guide =====================
        void ShowVulkanGuideModal() {
            var dlg = new Window {
                Title = T("Vulkan Runtime не обнаружен", "Vulkan Runtime Not Found"),
                Width = 510, SizeToContent = SizeToContent.Height,
                Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize, Background = Br("#242424"), Foreground = FgPrimary,
                FontFamily = FontFamily, FontSize = 12.5
            };
            dlg.SourceInitialized += (s, e) => ApplyDarkTitleBar(dlg);

            var sp = new StackPanel { Margin = new Thickness(16) };
            string reason = (_vkCheckStatus == "NO_LOADER")
                ? T("Причина: отсутствует системная библиотека vulkan-1.dll (Vulkan Loader).", "Reason: system vulkan-1.dll (Vulkan Loader) is missing.")
                : T("Причина: vulkan-1.dll найден, но драйвер NVIDIA не зарегистрировал Vulkan ICD.", "Reason: vulkan-1.dll found, but NVIDIA driver has no Vulkan ICD registered.");
            sp.Children.Add(new TextBlock { Text = reason, Foreground = YellowFg, FontFamily = new FontFamily("Consolas"), FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
            sp.Children.Add(new TextBlock {
                Text = T("Для работы лёгкого движка Vulkan Compute (~5-20 MiB VRAM) требуется системный Vulkan Runtime и поддержка Vulkan в драйвере NVIDIA. Выберите один из вариантов:",
                         "The lightweight Vulkan Compute backend (~5-20 MiB VRAM) requires the system Vulkan Runtime and NVIDIA Vulkan ICD. Choose one of the options below:"),
                Foreground = Br("#CCCCCC"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10)
            });

            Action<string, string, string, string> addStep = (title, body, linkLbl, url) => {
                var c = new StackPanel();
                c.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Foreground = FgPrimary, Margin = new Thickness(0, 0, 0, 2) });
                c.Children.Add(new TextBlock { Text = body, Foreground = FgSecondary, FontSize = 11.5, TextWrapping = TextWrapping.Wrap });
                if (!string.IsNullOrEmpty(url)) {
                    var lnk = new Button {
                        Content = linkLbl, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0),
                        Padding = new Thickness(0), Style = MakeButtonStyle(Brushes.Transparent, Brushes.Transparent, Accent, true)
                    };
                    lnk.Click += (s, e) => OpenUrl(url);
                    c.Children.Add(lnk);
                }
                sp.Children.Add(MakeCard(c, new Thickness(10, 8, 10, 8)));
            };

            addStep(
                T("Вариант 1 - Установить официальный Vulkan Runtime (1 минута)", "Option 1 - Install Official Vulkan Runtime (1 minute)"),
                T("Скачайте и запустите «Latest Runtime / Installer» со страницы LunarG (устанавливает системный vulkan-1.dll), затем нажмите «Проверить снова».",
                  "Download and run the Latest Runtime / Installer from LunarG (installs system vulkan-1.dll), then click Recheck."),
                T("Открыть vulkan.lunarg.com/sdk/home", "Open vulkan.lunarg.com/sdk/home"), LunarGUrl);

            addStep(
                T("Вариант 2 - Переустановить драйвер NVIDIA с компонентом Vulkan", "Option 2 - Reinstall NVIDIA Driver with Vulkan Enabled"),
                T("Если драйвер ставился через NVCleanstall или в урезанном режиме, переустановите драйвер NVIDIA с включённым Vulkan (или выполните чистую установку).",
                  "If you installed a stripped driver via NVCleanstall or headless package, reinstall the NVIDIA driver with Vulkan enabled (or run Clean Install)."),
                T("Открыть nvidia.com/drivers", "Open nvidia.com/drivers"), NvidiaDrvUrl);

            addStep(
                T("Вариант 3 - Переключить выбранные карты на CUDA", "Option 3 - Switch Selected GPUs to CUDA"),
                T("Работает напрямую через nvcuda.dll без Vulkan, но занимает ~200-300 MiB VRAM на каждую выбранную карту.",
                  "Uses nvcuda.dll directly without Vulkan, but allocates ~200-300 MiB VRAM per selected GPU."),
                null, null);

            var feedbackBox = new Border { CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1), Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(0, 0, 0, 10), Visibility = Visibility.Collapsed };
            var feedbackTxt = new TextBlock { FontSize = 11.5, TextWrapping = TextWrapping.Wrap };
            feedbackBox.Child = feedbackTxt;
            sp.Children.Add(feedbackBox);

            var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var bRecheck = new Button { Content = T("Проверить снова", "Recheck"), Style = PrimaryBtnStyle(), Margin = new Thickness(0, 0, 8, 0) };
            var bCuda    = new Button { Content = T("CUDA для всех", "CUDA for all"), Style = SecondaryBtnStyle(), Margin = new Thickness(0, 0, 8, 0) };
            var bClose   = new Button { Content = T("Закрыть", "Close"), Style = SecondaryBtnStyle() };

            bRecheck.Click += (s, e) => {
                CheckVulkanNow();
                feedbackBox.Visibility = Visibility.Visible;
                if (_vkCheckStatus == "OK") {
                    feedbackBox.Background = GreenBg; feedbackBox.BorderBrush = GreenBorder; feedbackTxt.Foreground = GreenFg;
                    feedbackTxt.Text = T("Vulkan обнаружен! Окно закроется автоматически.", "Vulkan detected! Closing dialog.");
                    RefreshTextsAndState();
                    dlg.Close();
                } else {
                    feedbackBox.Background = RedBg; feedbackBox.BorderBrush = RedBorder; feedbackTxt.Foreground = RedFg;
                    feedbackTxt.Text = T("Vulkan всё ещё не обнаружен (" + _vkCheckStatus + ").", "Vulkan is still not detected (" + _vkCheckStatus + ").");
                }
            };
            bCuda.Click += (s, e) => {
                foreach (var g in _gpus) g.Backend = "cuda";
                RefreshTextsAndState();
                dlg.Close();
            };
            bClose.Click += (s, e) => dlg.Close();

            btns.Children.Add(bRecheck); btns.Children.Add(bCuda); btns.Children.Add(bClose);
            sp.Children.Add(btns);
            dlg.Content = sp;
            dlg.ShowDialog();
        }

        // ===================== Modal 2: Gen2 Unlock Info ('?') =====================
        void ShowGen2InfoModal() {
            CheckGen2Status();
            var dlg = new Window {
                Title = "CMP40HX-Unlock (PCIe Gen2 / EFI Unlock)",
                Width = 470, SizeToContent = SizeToContent.Height,
                Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize, Background = Br("#242424"), Foreground = FgPrimary,
                FontFamily = FontFamily, FontSize = 12.5
            };
            dlg.SourceInitialized += (s, e) => ApplyDarkTitleBar(dlg);

            var sp = new StackPanel { Margin = new Thickness(16) };

            var stDock = new DockPanel();
            var stBdg = new Border { CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1), Padding = new Thickness(8, 2, 8, 2) };
            var stTxt = new TextBlock { FontSize = 11.5, FontWeight = FontWeights.Medium };
            if (_gen2State == "absent") {
                stBdg.Background = BtnSecBg; stBdg.BorderBrush = BtnSecBorder; stTxt.Foreground = FgSecondary;
                stTxt.Text = T("Нет", "Not installed");
            } else if (_gen2State == "unsynced") {
                stBdg.Background = YellowBg; stBdg.BorderBrush = YellowBorder; stTxt.Foreground = YellowFg;
                stTxt.Text = T("Есть (не синхр.)", "Installed (unsynced)");
            } else {
                stBdg.Background = GreenBg; stBdg.BorderBrush = GreenBorder; stTxt.Foreground = GreenFg;
                stTxt.Text = T("Оптимизировано", "Optimized") + (string.IsNullOrEmpty(_gen2PcieSummary) ? "" : (" | " + _gen2PcieSummary));
            }
            stBdg.Child = stTxt;
            DockPanel.SetDock(stBdg, Dock.Right);
            stDock.Children.Add(stBdg);
            stDock.Children.Add(new TextBlock { Text = T("Статус в системе:", "System status:"), Foreground = FgMuted, VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(MakeCard(stDock, new Thickness(10, 8, 10, 8)));

            sp.Children.Add(new TextBlock {
                Text = T("CMP40HX-Unlock - сторонний открытый проект (PZH1gdmu/CMP40HX-Unlock) для разблокировки полных вычислений FP16/Tensor Cores, Resizable BAR и переключения PCIe с Gen1 на Gen2 на NVIDIA CMP 40HX.",
                         "CMP40HX-Unlock is an independent open-source project (PZH1gdmu/CMP40HX-Unlock) that unlocks full FP16/Tensor Core compute, Resizable BAR, and PCIe Gen2 speed on NVIDIA CMP 40HX cards."),
                Foreground = Br("#CCCCCC"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8)
            });
            sp.Children.Add(new TextBlock {
                Text = T("Синхронизация в NV-KeepAlive выполняет 3 вещи: ставит KeepAlive на паузу во время запуска 40HXInstaller -gen2, запускает наш фикс чипсетного PCIe-моста (дожимает Gen1 -> Gen2 на нижних слотах, где 40HXInstaller не находит Root Port) и восстанавливает Gen2 после пробуждения из сна.",
                         "NV-KeepAlive sync performs 3 steps: pauses KeepAlive while 40HXInstaller -gen2 runs, runs our built-in PCIe chipset-bridge retrain fix (lifts Gen1 -> Gen2 in secondary slots where 40HXInstaller misses the Root Port), and restores Gen2 after waking from sleep."),
                Foreground = FgSecondary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12)
            });

            var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var bInst = new Button { Content = T("Установить", "Install"), Style = PrimaryBtnStyle(), Margin = new Thickness(0, 0, 8, 0) };
            var bClose = new Button { Content = T("Закрыть", "Close"), Style = SecondaryBtnStyle() };
            bInst.Click += (s, e) => OpenUrl(UnlockerUrl);
            bClose.Click += (s, e) => dlg.Close();
            btns.Children.Add(bInst); btns.Children.Add(bClose);
            sp.Children.Add(btns);

            dlg.Content = sp;
            dlg.ShowDialog();
        }

        // ===================== Helpers =====================
        static void OpenUrl(string url) {
            try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); } catch {}
        }

        static int RunProcessCapture(string fileName, string args, int timeoutMs, out string output) {
            output = "";
            try {
                var psi = new ProcessStartInfo {
                    FileName = fileName, Arguments = args,
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8
                };
                using (var p = Process.Start(psi)) {
                    if (p == null) return -1;
                    string so = p.StandardOutput.ReadToEnd();
                    string se = p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch {} return -2; }
                    output = string.IsNullOrEmpty(so) ? se : so;
                    return p.ExitCode;
                }
            } catch (Exception ex) {
                output = ex.Message;
                return -1;
            }
        }

        static int RunPowerShell(string script, int timeoutMs, out string output) {
            string b64 = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            return RunProcessCapture("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -EncodedCommand " + b64, timeoutMs, out output);
        }
    }

    static class Program {
        static bool IsAdministrator() {
            try {
                using (var id = WindowsIdentity.GetCurrent())
                    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            } catch { return false; }
        }

        [STAThread]
        static int Main(string[] args) {
            bool noElevate = false;
            int autoCloseSec = 0;
            for (int i = 0; i < args.Length; i++) {
                if (args[i] == "--no-elevate") noElevate = true;
                else if (args[i] == "--autoclose" && i + 1 < args.Length) int.TryParse(args[++i], out autoCloseSec);
            }

            if (!noElevate && !IsAdministrator()) {
                try {
                    Process.Start(new ProcessStartInfo {
                        FileName = Process.GetCurrentProcess().MainModule.FileName,
                        UseShellExecute = true,
                        Verb = "runas"
                    });
                    return 0;
                } catch {
                    // User cancelled UAC; continue in read-only mode
                }
            }

            var app = new Application();
            var win = new MainWindow();
            if (autoCloseSec > 0) {
                var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(autoCloseSec) };
                t.Tick += (s, e) => { t.Stop(); win.Close(); };
                t.Start();
            }
            app.Run(win);
            return 0;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;

namespace NvKeepAliveUI {
    public class GpuItem {
        public int Index;
        public string PciBusId;
        public string Luid;
        public string Name;
        public string DeviceIdHex;
        public ulong TotalVramMiB;
        public bool IsMining;
        public bool IsCmp40hx;
        public bool HasVulkan;
        public bool HasCuda;

        // Live telemetry from nvidia-smi & Windows GPU Process Memory counter
        public string UsedVram = "0";
        public string LiveTotalVram = "";
        public double? DaemonVramMiB = null;
        public string TempC = "-";
        public string PowerW = "-";
        public string PcieText = "";

        // Draft UI selection
        public bool Selected = true;
        public string Backend = "vulkan"; // "vulkan" or "cuda"
    }

    public class ThemePalette {
        public bool IsDark;
        public Brush BgWindow, BgCard, BorderCard, BgRow, BgRowSel, BorderRowSel;
        public Brush BgSeg, BorderSeg, BgSubCard, BorderSubCard, BgLog, FgLog, BgModal;
        public Brush Accent, AccentFg, FgPrimary, FgSecondary, FgMuted, DividerFg;
        public Brush GreenBg, GreenBorder, GreenFg;
        public Brush YellowBg, YellowBorder, YellowFg;
        public Brush RedBg, RedBorder, RedFg;
        public Brush BtnSecBg, BtnSecBorder, MiningBg, MiningFg, BlueBadgeBg, NeutralBadgeBg;
        public Brush ToggleOffBg, ToggleOffBorder, ToggleOffThumb;
        public Brush ScrollTrack, ScrollThumb, ScrollThumbHover, ScrollThumbDrag;

        static SolidColorBrush Br(string hex) {
            var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex);
            b.Freeze();
            return b;
        }

        public static readonly ThemePalette Dark = new ThemePalette {
            IsDark = true,
            BgWindow = Br("#202020"), BgCard = Br("#2B2B2B"), BorderCard = Br("#353535"),
            BgRow = Br("#242424"), BgRowSel = Br("#25303A"), BorderRowSel = Br("#4A8CB0"),
            BgSeg = Br("#1B1B1B"), BorderSeg = Br("#3C3C3C"),
            BgSubCard = Br("#232323"), BorderSubCard = Br("#323232"),
            BgLog = Br("#181818"), FgLog = Br("#CCCCCC"), BgModal = Br("#242424"),
            Accent = Br("#60CDFF"), AccentFg = Br("#0B1620"),
            FgPrimary = Br("#F3F3F3"), FgSecondary = Br("#A5A5A5"), FgMuted = Br("#9D9D9D"), DividerFg = Br("#555555"),
            GreenBg = Br("#1D3A27"), GreenBorder = Br("#2C5C3B"), GreenFg = Br("#6CCB5F"),
            YellowBg = Br("#3E321A"), YellowBorder = Br("#614E25"), YellowFg = Br("#FCE100"),
            RedBg = Br("#362224"), RedBorder = Br("#5A3438"), RedFg = Br("#FF99A4"),
            BtnSecBg = Br("#333333"), BtnSecBorder = Br("#444444"),
            MiningBg = Br("#362C1E"), MiningFg = Br("#F5B951"),
            BlueBadgeBg = Br("#1D3548"), NeutralBadgeBg = Br("#2F2F2F"),
            ToggleOffBg = Br("#202020"), ToggleOffBorder = Br("#8C8C8C"), ToggleOffThumb = Br("#CCCCCC"),
            ScrollTrack = Br("#1A1A1A"), ScrollThumb = Br("#4A4A4A"), ScrollThumbHover = Br("#666666"), ScrollThumbDrag = Br("#808080")
        };

        public static readonly ThemePalette Light = new ThemePalette {
            IsDark = false,
            BgWindow = Br("#F3F3F3"), BgCard = Br("#FFFFFF"), BorderCard = Br("#DFDFDF"),
            BgRow = Br("#FAFAFA"), BgRowSel = Br("#EDF6FC"), BorderRowSel = Br("#0067C0"),
            BgSeg = Br("#ECECEC"), BorderSeg = Br("#D0D0D0"),
            BgSubCard = Br("#F7F7F7"), BorderSubCard = Br("#E2E2E2"),
            BgLog = Br("#FAFAFA"), FgLog = Br("#1E1E1E"), BgModal = Br("#F3F3F3"),
            Accent = Br("#0067C0"), AccentFg = Br("#FFFFFF"),
            FgPrimary = Br("#1A1A1A"), FgSecondary = Br("#555555"), FgMuted = Br("#666666"), DividerFg = Br("#CCCCCC"),
            GreenBg = Br("#DFF6DD"), GreenBorder = Br("#9FD89F"), GreenFg = Br("#107C10"),
            YellowBg = Br("#FFF4CE"), YellowBorder = Br("#E6C24C"), YellowFg = Br("#7A5400"),
            RedBg = Br("#FDE7E9"), RedBorder = Br("#E8A6AC"), RedFg = Br("#C42B1C"),
            BtnSecBg = Br("#FFFFFF"), BtnSecBorder = Br("#CCCCCC"),
            MiningBg = Br("#FFF3DC"), MiningFg = Br("#9A5B00"),
            BlueBadgeBg = Br("#E5F1FB"), NeutralBadgeBg = Br("#EFEFEF"),
            ToggleOffBg = Br("#FFFFFF"), ToggleOffBorder = Br("#767676"), ToggleOffThumb = Br("#555555"),
            ScrollTrack = Br("#EFEFEF"), ScrollThumb = Br("#BDBDBD"), ScrollThumbHover = Br("#9A9A9A"), ScrollThumbDrag = Br("#7A7A7A")
        };
    }

    public class MainWindow : Window {
        const string Version = "1.1.4";
        const string TaskName = "NV-KeepAlive";
        const string Gen2TaskName = "40HX PCIe Gen2 Bring-up";
        const string MutexName = "Global\\NvKeepAliveMutex";
        const string UnlockerUrl = "https://github.com/Cyridd/cmpunlocker/releases";
        const string UnlockerRepoUrl = "https://github.com/Cyridd/cmpunlocker";
        const string LunarGUrl = "https://vulkan.lunarg.com/sdk/home#windows";
        const string NvidiaDrvUrl = "https://www.nvidia.com/drivers";
        const string LatestReleaseApiUrl = "https://api.github.com/repos/D9eka/NV-KeepAlive/releases/latest";
        const string ReleasesPageUrl = "https://github.com/D9eka/NV-KeepAlive/releases";

        static readonly string InstallDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NV-KeepAlive");
        static readonly string InstalledExe = System.IO.Path.Combine(InstallDir, "NvKeepAlive.exe");
        static readonly string PauseFile = System.IO.Path.Combine(InstallDir, "keepalive.pause");
        static readonly string LogFile = System.IO.Path.Combine(InstallDir, "keepalive.log");
        static readonly string Gen2WrapperPath = System.IO.Path.Combine(InstallDir, "gen2-sync-wrapper.cmd");
        static readonly string Gen2OrigXmlPath = System.IO.Path.Combine(InstallDir, "gen2-orig.xml");
        static readonly string Gen2OrigCmdPath = System.IO.Path.Combine(InstallDir, "gen2-orig.txt");

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, UIntPtr wParam, string lParam, uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);

        static void ApplyTitleBarTheme(Window w, bool isDark) {
            try {
                IntPtr hwnd = new WindowInteropHelper(w).Handle;
                if (hwnd == IntPtr.Zero) return;
                int val = isDark ? 1 : 0;
                if (DwmSetWindowAttribute(hwnd, 20, ref val, sizeof(int)) != 0)
                    DwmSetWindowAttribute(hwnd, 19, ref val, sizeof(int));
            } catch {}
        }

        static bool IsWindowsLightTheme() {
            try {
                object v = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 0);
                if (v is int) return ((int)v) != 0;
            } catch {}
            return false;
        }

        // Theme & language state
        bool _isRu;
        string _themeMode = "auto"; // auto | dark | light
        ThemePalette _pal = ThemePalette.Dark;

        // App state
        string _daemonSourceExe;
        List<GpuItem> _gpus = new List<GpuItem>();
        Dictionary<int, string> _appliedTargets = new Dictionary<int, string>();
        bool _serviceInstalled = false;
        bool _serviceRunning = false;
        bool _servicePaused = false;
        DateTime _sessionStart = DateTime.Now;
        int _tdr5m = 0, _tdrStart = 0;

        // Live CPU & Daemon VRAM calculation
        int _lastPid = -1;
        TimeSpan _lastCpuTime = TimeSpan.Zero;
        DateTime _lastCpuWall = DateTime.MinValue;
        string _liveCpuText = "-";
        string _liveFixVramText = "-";
        int _vramCachePid = -1;
        Dictionary<string, PerformanceCounter> _vramCounters = new Dictionary<string, PerformanceCounter>(StringComparer.OrdinalIgnoreCase);

        // Probes & updates
        string _vkCheckStatus = "OK";
        string _gen2State = "absent";
        string _gen2PcieSummary = "";
        bool _coopmatApplied = false;
        bool _coopmatDraft = false;
        string _availableUpdateTag = "";

        // Polling & Test
        DispatcherTimer _pollTimer;
        DispatcherTimer _devChangeTimer;
        volatile bool _pollBusy = false;
        volatile bool _needGpuReprobe = false;
        volatile bool _testRunning = false;
        string _lastLogText = "";

        // UI Controls
        Border _titleDot, _langBox, _cardMain, _vkLegBox, _cuLegBox, _cuBdg;
        TextBlock _titleText, _dividerPipe, _lblCpu, _lblFixVram, _vkLegTitle, _cuLegTitle;
        Button _btnRu, _btnEn, _btnTheme, _btnUpdate;
        TextBlock _lblStatusTitle, _lblTdr5m, _valTdr5m, _lblTdrStart, _valTdrStart, _valCpu, _valFixVram;
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
        DockPanel _topBar, _logHead, _logSection;
        Grid _outerGrid, _bodyGrid;
        ColumnDefinition _bodyCol0, _bodyCol1;
        RowDefinition _bodyRow0, _bodyRow1;
        ScrollViewer _cardsScroll;
        StackPanel _cardsPanel;
        bool _isTwoCol = false;
        bool _initialLayoutDone = false;
        double _chromeH = 38;
        TextBlock _lblLogTitle; Button _btnOpenFolder; Border _logWrap; TextBox _logBox;

        static bool _scrollStylesInstalled = false;
        static void InstallScrollStyles() {
            if (_scrollStylesInstalled || Application.Current == null) return;
            _scrollStylesInstalled = true;
            try {
                const string sbXaml =
                    "<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='{x:Type ScrollBar}'>" +
                    "  <Setter Property='SnapsToDevicePixels' Value='True'/>" +
                    "  <Setter Property='OverridesDefaultStyle' Value='True'/>" +
                    "  <Style.Triggers>" +
                    "    <Trigger Property='Orientation' Value='Vertical'>" +
                    "      <Setter Property='Width' Value='10'/>" +
                    "      <Setter Property='MinWidth' Value='10'/>" +
                    "      <Setter Property='Height' Value='Auto'/>" +
                    "      <Setter Property='Template'><Setter.Value>" +
                    "        <ControlTemplate TargetType='{x:Type ScrollBar}'>" +
                    "          <Border Background='{DynamicResource ScrollTrackBrush}' Padding='2'>" +
                    "            <Track x:Name='PART_Track' IsDirectionReversed='True'>" +
                    "              <Track.DecreaseRepeatButton><RepeatButton Command='ScrollBar.PageUpCommand' Focusable='False' Opacity='0'/></Track.DecreaseRepeatButton>" +
                    "              <Track.Thumb><Thumb Focusable='False'><Thumb.Template><ControlTemplate TargetType='{x:Type Thumb}'>" +
                    "                <Border x:Name='th' Background='{DynamicResource ScrollThumbBrush}' CornerRadius='3' MinHeight='22'/>" +
                    "                <ControlTemplate.Triggers>" +
                    "                  <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='th' Property='Background' Value='{DynamicResource ScrollThumbHoverBrush}'/></Trigger>" +
                    "                  <Trigger Property='IsDragging' Value='True'><Setter TargetName='th' Property='Background' Value='{DynamicResource ScrollThumbDragBrush}'/></Trigger>" +
                    "                </ControlTemplate.Triggers>" +
                    "              </ControlTemplate></Thumb.Template></Thumb></Track.Thumb>" +
                    "              <Track.IncreaseRepeatButton><RepeatButton Command='ScrollBar.PageDownCommand' Focusable='False' Opacity='0'/></Track.IncreaseRepeatButton>" +
                    "            </Track>" +
                    "          </Border>" +
                    "        </ControlTemplate>" +
                    "      </Setter.Value></Setter>" +
                    "    </Trigger>" +
                    "    <Trigger Property='Orientation' Value='Horizontal'>" +
                    "      <Setter Property='Height' Value='10'/>" +
                    "      <Setter Property='MinHeight' Value='10'/>" +
                    "      <Setter Property='Width' Value='Auto'/>" +
                    "      <Setter Property='Template'><Setter.Value>" +
                    "        <ControlTemplate TargetType='{x:Type ScrollBar}'>" +
                    "          <Border Background='{DynamicResource ScrollTrackBrush}' Padding='2'>" +
                    "            <Track x:Name='PART_Track' IsDirectionReversed='False'>" +
                    "              <Track.DecreaseRepeatButton><RepeatButton Command='ScrollBar.PageLeftCommand' Focusable='False' Opacity='0'/></Track.DecreaseRepeatButton>" +
                    "              <Track.Thumb><Thumb Focusable='False'><Thumb.Template><ControlTemplate TargetType='{x:Type Thumb}'>" +
                    "                <Border x:Name='th' Background='{DynamicResource ScrollThumbBrush}' CornerRadius='3' MinWidth='22'/>" +
                    "                <ControlTemplate.Triggers>" +
                    "                  <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='th' Property='Background' Value='{DynamicResource ScrollThumbHoverBrush}'/></Trigger>" +
                    "                  <Trigger Property='IsDragging' Value='True'><Setter TargetName='th' Property='Background' Value='{DynamicResource ScrollThumbDragBrush}'/></Trigger>" +
                    "                </ControlTemplate.Triggers>" +
                    "              </ControlTemplate></Thumb.Template></Thumb></Track.Thumb>" +
                    "              <Track.IncreaseRepeatButton><RepeatButton Command='ScrollBar.PageRightCommand' Focusable='False' Opacity='0'/></Track.IncreaseRepeatButton>" +
                    "            </Track>" +
                    "          </Border>" +
                    "        </ControlTemplate>" +
                    "      </Setter.Value></Setter>" +
                    "    </Trigger>" +
                    "  </Style.Triggers>" +
                    "</Style>";

                const string svXaml =
                    "<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='{x:Type ScrollViewer}'>" +
                    "  <Setter Property='Template'><Setter.Value>" +
                    "    <ControlTemplate TargetType='{x:Type ScrollViewer}'>" +
                    "      <Grid Background='{TemplateBinding Background}'>" +
                    "        <Grid.ColumnDefinitions><ColumnDefinition Width='*'/><ColumnDefinition Width='Auto'/></Grid.ColumnDefinitions>" +
                    "        <Grid.RowDefinitions><RowDefinition Height='*'/><RowDefinition Height='Auto'/></Grid.RowDefinitions>" +
                    "        <ScrollContentPresenter x:Name='PART_ScrollContentPresenter' Grid.Row='0' Grid.Column='0' Margin='{TemplateBinding Padding}' Content='{TemplateBinding Content}' ContentTemplate='{TemplateBinding ContentTemplate}' CanContentScroll='{TemplateBinding CanContentScroll}'/>" +
                    "        <ScrollBar x:Name='PART_VerticalScrollBar' Grid.Row='0' Grid.Column='1' Orientation='Vertical' Value='{TemplateBinding VerticalOffset}' Maximum='{TemplateBinding ScrollableHeight}' ViewportSize='{TemplateBinding ViewportHeight}' Visibility='{TemplateBinding ComputedVerticalScrollBarVisibility}'/>" +
                    "        <ScrollBar x:Name='PART_HorizontalScrollBar' Grid.Row='1' Grid.Column='0' Orientation='Horizontal' Value='{TemplateBinding HorizontalOffset}' Maximum='{TemplateBinding ScrollableWidth}' ViewportSize='{TemplateBinding ViewportWidth}' Visibility='{TemplateBinding ComputedHorizontalScrollBarVisibility}'/>" +
                    "        <Border Grid.Row='1' Grid.Column='1' Background='{DynamicResource ScrollTrackBrush}'/>" +
                    "      </Grid>" +
                    "    </ControlTemplate>" +
                    "  </Setter.Value></Setter>" +
                    "</Style>";

                Application.Current.Resources[typeof(System.Windows.Controls.Primitives.ScrollBar)] = (Style)XamlReader.Parse(sbXaml);
                Application.Current.Resources[typeof(ScrollViewer)] = (Style)XamlReader.Parse(svXaml);
            } catch {}
        }

        public MainWindow(string initialThemeMode) {
            _isRu = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("ru", StringComparison.OrdinalIgnoreCase);
            if (initialThemeMode == "dark" || initialThemeMode == "light" || initialThemeMode == "auto")
                _themeMode = initialThemeMode;
            _pal = ResolvePalette();
            _daemonSourceExe = FindDaemonExe();

            Title = "NV-KeepAlive v" + Version;
            Width = 735;
            MinWidth = 735;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = new FontFamily("Segoe UI Variable, Segoe UI");
            FontSize = 13;

            InstallScrollStyles();

            SourceInitialized += (s, e) => {
                ApplyTitleBarTheme(this, _pal.IsDark);
                try {
                    var src = HwndSource.FromVisual(this) as HwndSource;
                    if (src != null) src.AddHook(WndProcHook);
                } catch {}
            };
            Loaded += OnLoaded;
            ContentRendered += OnFirstRendered;
            SizeChanged += (s, e) => { if (_initialLayoutDone) UpdateResponsiveLayout(false); };
            Closed += (s, e) => {
                if (_pollTimer != null) _pollTimer.Stop();
                if (_devChangeTimer != null) _devChangeTimer.Stop();
                try { SystemEvents.UserPreferenceChanged -= OnSystemPreferenceChanged; } catch {}
                DisposeVramCounters();
            };

            BuildUi();
            ApplyThemeToChrome();
        }

        IntPtr WndProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) {
            if (msg == 0x0219) { // WM_DEVICECHANGE
                Dispatcher.BeginInvoke((Action)(() => {
                    if (_devChangeTimer == null) {
                        _devChangeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
                        _devChangeTimer.Tick += (s, e) => {
                            _devChangeTimer.Stop();
                            _needGpuReprobe = true;
                            TriggerBackgroundPoll();
                        };
                    }
                    _devChangeTimer.Stop();
                    _devChangeTimer.Start();
                }));
            }
            return IntPtr.Zero;
        }

        void OnFirstRendered(object sender, EventArgs e) {
            if (_initialLayoutDone) return;
            _initialLayoutDone = true;
            UpdateLayout();
            double h = Math.Ceiling(ActualHeight);
            double innerH = _outerGrid.ActualHeight + _outerGrid.Margin.Top + _outerGrid.Margin.Bottom;
            if (h > innerH && (h - innerH) < 120) _chromeH = h - innerH;
            double maxWorkH = Math.Max(420, SystemParameters.WorkArea.Height - 48);
            SizeToContent = SizeToContent.Manual;
            Height = Math.Min(h, maxWorkH);
            UpdateResponsiveLayout(true);
        }

        void UpdateResponsiveLayout(bool force) {
            bool wantTwoCol = ActualWidth >= 1120;
            if (force || wantTwoCol != _isTwoCol) {
                _isTwoCol = wantTwoCol;
                if (_isTwoCol) {
                    _bodyCol0.Width = new GridLength(705);
                    _bodyCol1.Width = new GridLength(1, GridUnitType.Star);
                    _bodyRow0.Height = new GridLength(1, GridUnitType.Star);
                    _bodyRow1.Height = new GridLength(0);
                    Grid.SetRow(_cardsScroll, 0); Grid.SetColumn(_cardsScroll, 0);
                    _cardsScroll.VerticalAlignment = VerticalAlignment.Center;
                    Grid.SetRow(_logSection, 0); Grid.SetColumn(_logSection, 1);
                    _logSection.Margin = new Thickness(14, 0, 0, 0);
                } else {
                    _bodyCol0.Width = new GridLength(1, GridUnitType.Star);
                    _bodyCol1.Width = new GridLength(0);
                    _bodyRow0.Height = GridLength.Auto;
                    _bodyRow1.Height = new GridLength(1, GridUnitType.Star);
                    Grid.SetRow(_cardsScroll, 0); Grid.SetColumn(_cardsScroll, 0);
                    _cardsScroll.VerticalAlignment = VerticalAlignment.Top;
                    Grid.SetRow(_logSection, 1); Grid.SetColumn(_logSection, 0);
                    _logSection.Margin = new Thickness(0, 2, 0, 0);
                }
            }
            UpdateMinHeight();
        }

        void UpdateMinHeight() {
            if (!_initialLayoutDone) return;
            double maxWorkH = Math.Max(420, SystemParameters.WorkArea.Height - 48);
            double topH = _topBar.ActualHeight + _topBar.Margin.Top + _topBar.Margin.Bottom;
            double cardsH = _cardsPanel.ActualHeight;
            double marginsH = _outerGrid.Margin.Top + _outerGrid.Margin.Bottom + _chromeH;
            double minH;
            if (_isTwoCol) {
                minH = Math.Ceiling(topH + Math.Max(cardsH, 320) + marginsH);
            } else {
                double logHeadH = _logHead.ActualHeight + _logHead.Margin.Top + _logHead.Margin.Bottom + 4;
                minH = Math.Ceiling(topH + cardsH + logHeadH + 110 + marginsH);
            }
            MinHeight = Math.Min(minH, maxWorkH);
            if (WindowState == WindowState.Normal && Height < MinHeight)
                Height = MinHeight;
        }

        ThemePalette ResolvePalette() {
            if (_themeMode == "dark") return ThemePalette.Dark;
            if (_themeMode == "light") return ThemePalette.Light;
            return IsWindowsLightTheme() ? ThemePalette.Light : ThemePalette.Dark;
        }

        void OnSystemPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) {
            if (_themeMode != "auto") return;
            Dispatcher.BeginInvoke((Action)(() => {
                var next = ResolvePalette();
                if (next.IsDark != _pal.IsDark) {
                    _pal = next;
                    ApplyThemeToChrome();
                    RefreshTextsAndState();
                }
            }));
        }

        void CycleThemeMode() {
            if (_themeMode == "auto") _themeMode = "dark";
            else if (_themeMode == "dark") _themeMode = "light";
            else _themeMode = "auto";
            _pal = ResolvePalette();
            ApplyThemeToChrome();
            RefreshTextsAndState();
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
            trigHover.Setters.Add(new Setter(UIElement.OpacityProperty, 0.88));
            tpl.Triggers.Add(trigHover);

            var trigDis = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            trigDis.Setters.Add(new Setter(UIElement.OpacityProperty, 0.42));
            tpl.Triggers.Add(trigDis);

            st.Setters.Add(new Setter(Control.TemplateProperty, tpl));
            return st;
        }

        Style PrimaryBtnStyle() { return MakeButtonStyle(_pal.Accent, _pal.Accent, _pal.AccentFg, true); }
        Style SecondaryBtnStyle() { return MakeButtonStyle(_pal.BtnSecBg, _pal.BtnSecBorder, _pal.FgPrimary, false); }
        Style DangerBtnStyle() { return MakeButtonStyle(_pal.RedBg, _pal.RedBorder, _pal.RedFg, false); }

        Border MakeCard(UIElement child, Thickness pad) {
            return new Border {
                Background = _pal.BgCard,
                BorderBrush = _pal.BorderCard,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = pad,
                Margin = new Thickness(0, 0, 0, 10),
                Child = child
            };
        }

        void BuildUi() {
            _outerGrid = new Grid { MaxWidth = 1560, Margin = new Thickness(16, 12, 16, 14) };
            _outerGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _outerGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // Top bar (Title + optional Update badge + Theme toggle + RU/EN toggle)
            _topBar = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };

            var rightControls = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            _btnUpdate = new Button {
                Padding = new Thickness(8, 2, 8, 2), FontSize = 11, Margin = new Thickness(0, 0, 8, 0),
                Visibility = Visibility.Collapsed
            };
            _btnUpdate.Click += (s, e) => OpenUrl(ReleasesPageUrl);
            rightControls.Children.Add(_btnUpdate);

            _btnTheme = new Button { Padding = new Thickness(8, 2, 8, 2), FontSize = 11, Margin = new Thickness(0, 0, 8, 0) };
            _btnTheme.Click += (s, e) => CycleThemeMode();
            rightControls.Children.Add(_btnTheme);

            _langBox = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), Padding = new Thickness(2) };
            var langSp = new StackPanel { Orientation = Orientation.Horizontal };
            _btnRu = new Button { Content = "RU", Padding = new Thickness(8, 2, 8, 2), FontSize = 11 };
            _btnEn = new Button { Content = "EN", Padding = new Thickness(8, 2, 8, 2), FontSize = 11, Margin = new Thickness(2, 0, 0, 0) };
            _btnRu.Click += (s, e) => { _isRu = true; RefreshTextsAndState(); };
            _btnEn.Click += (s, e) => { _isRu = false; RefreshTextsAndState(); };
            langSp.Children.Add(_btnRu);
            langSp.Children.Add(_btnEn);
            _langBox.Child = langSp;
            rightControls.Children.Add(_langBox);

            DockPanel.SetDock(rightControls, Dock.Right);
            _topBar.Children.Add(rightControls);

            var titleRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            _titleDot = new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 8, 0) };
            _titleText = new TextBlock { Text = "NV-KeepAlive v" + Version, FontWeight = FontWeights.SemiBold, FontSize = 14 };
            titleRow.Children.Add(_titleDot);
            titleRow.Children.Add(_titleText);
            _topBar.Children.Add(titleRow);
            Grid.SetRow(_topBar, 0);
            _outerGrid.Children.Add(_topBar);

            _cardsPanel = new StackPanel();
            _cardsPanel.SizeChanged += (s, e) => { if (_initialLayoutDone) UpdateMinHeight(); };

            // ===== Block 1: GPUs, Backends, Live CPU & Daemon VRAM & Short-Window TDR =====
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
            _lblStatusTitle = new TextBlock { FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            _dividerPipe = new TextBlock { Text = "|", Margin = new Thickness(0, 0, 8, 0), FontSize = 11 };
            _lblTdr5m = new TextBlock { FontSize = 11.5, Margin = new Thickness(0, 0, 3, 0) };
            _valTdr5m = new TextBlock { FontSize = 11.5, FontFamily = new FontFamily("Consolas"), FontWeight = FontWeights.Bold, Text = "0", Margin = new Thickness(0, 0, 8, 0) };
            _lblTdrStart = new TextBlock { FontSize = 11.5, Margin = new Thickness(0, 0, 3, 0) };
            _valTdrStart = new TextBlock { FontSize = 11.5, FontFamily = new FontFamily("Consolas"), FontWeight = FontWeights.Bold, Text = "0", Margin = new Thickness(0, 0, 8, 0) };
            _lblCpu = new TextBlock { FontSize = 11.5, Text = "CPU: " };
            _valCpu = new TextBlock { FontSize = 11.5, FontFamily = new FontFamily("Consolas"), FontWeight = FontWeights.Bold, Text = "-", Margin = new Thickness(0, 0, 8, 0) };
            _lblFixVram = new TextBlock { FontSize = 11.5, Text = "VRAM: " };
            _valFixVram = new TextBlock { FontSize = 11.5, FontFamily = new FontFamily("Consolas"), FontWeight = FontWeights.Bold, Text = "-" };
            metricsSp.Children.Add(_lblStatusTitle); metricsSp.Children.Add(_dividerPipe);
            Func<UIElement, UIElement, StackPanel> pair = (a, b) => {
                var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                sp.Children.Add(a); sp.Children.Add(b);
                return sp;
            };
            metricsSp.Children.Add(pair(_lblTdr5m, _valTdr5m));
            metricsSp.Children.Add(pair(_lblTdrStart, _valTdrStart));
            metricsSp.Children.Add(pair(_lblCpu, _valCpu));
            metricsSp.Children.Add(pair(_lblFixVram, _valFixVram));
            b1Head.Children.Add(metricsSp);
            b1.Children.Add(b1Head);

            _gpuListPanel = new StackPanel();
            b1.Children.Add(new ScrollViewer {
                MaxHeight = 260,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = _gpuListPanel
            });

            _pendingHintBox = new Border {
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5),
                Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed
            };
            _pendingHintText = new TextBlock { FontSize = 11.5, TextWrapping = TextWrapping.Wrap };
            _pendingHintBox.Child = _pendingHintText;
            b1.Children.Add(_pendingHintBox);

            // Compact backend legend
            var legGrid = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            legGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            legGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            legGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            _vkLegBox = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), Padding = new Thickness(8, 5, 8, 5) };
            var vkLegSp = new StackPanel();
            var vkTop = new StackPanel { Orientation = Orientation.Horizontal };
            _vkLegTitle = new TextBlock { Text = "Vulkan", FontWeight = FontWeights.SemiBold, FontSize = 11.5 };
            _vkBadge = new Border { CornerRadius = new CornerRadius(3), Padding = new Thickness(5, 0, 5, 1), Margin = new Thickness(6, 0, 0, 0), Cursor = Cursors.Hand };
            _vkBadgeText = new TextBlock { FontSize = 10.5 };
            _vkBadge.Child = _vkBadgeText;
            _vkBadge.MouseLeftButtonUp += (s, e) => { if (_vkCheckStatus != "OK") ShowVulkanGuideModal(); };
            vkTop.Children.Add(_vkLegTitle); vkTop.Children.Add(_vkBadge);
            _legendVk = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
            vkLegSp.Children.Add(vkTop); vkLegSp.Children.Add(_legendVk);
            _vkLegBox.Child = vkLegSp;
            Grid.SetColumn(_vkLegBox, 0);
            legGrid.Children.Add(_vkLegBox);

            _cuLegBox = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), Padding = new Thickness(8, 5, 8, 5) };
            var cuLegSp = new StackPanel();
            var cuTop = new StackPanel { Orientation = Orientation.Horizontal };
            _cuLegTitle = new TextBlock { Text = "CUDA", FontWeight = FontWeights.SemiBold, FontSize = 11.5 };
            _cuBdg = new Border { CornerRadius = new CornerRadius(3), Padding = new Thickness(5, 0, 5, 1), Margin = new Thickness(6, 0, 0, 0) };
            _cudaBadgeText = new TextBlock { FontSize = 10.5 };
            _cuBdg.Child = _cudaBadgeText;
            cuTop.Children.Add(_cuLegTitle); cuTop.Children.Add(_cuBdg);
            _legendCuda = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
            cuLegSp.Children.Add(cuTop); cuLegSp.Children.Add(_legendCuda);
            _cuLegBox.Child = cuLegSp;
            Grid.SetColumn(_cuLegBox, 2);
            legGrid.Children.Add(_cuLegBox);

            b1.Children.Add(legGrid);
            _cardMain = MakeCard(b1, new Thickness(12, 10, 12, 10));
            _cardsPanel.Children.Add(_cardMain);

            // ===== Block 2: PCIe Gen2 Unlock Sync + '?' =====
            var g2Dock = new DockPanel();
            _btnGen2Sync = new Button { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            _btnGen2Sync.Click += OnGen2SyncClick;
            DockPanel.SetDock(_btnGen2Sync, Dock.Right);
            g2Dock.Children.Add(_btnGen2Sync);

            var g2Left = new StackPanel();
            var g2Top = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
            _lblGen2Title = new TextBlock { FontWeight = FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center };
            _btnGen2Help = new Button {
                Content = "?", Width = 19, Height = 19, Padding = new Thickness(0), Margin = new Thickness(7, 0, 7, 0),
                FontSize = 11, FontWeight = FontWeights.Bold
            };
            _btnGen2Help.Click += (s, e) => ShowGen2InfoModal();
            _gen2Badge = new Border { CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1), Padding = new Thickness(7, 1, 7, 1), VerticalAlignment = VerticalAlignment.Center };
            _gen2BadgeText = new TextBlock { FontSize = 11, FontWeight = FontWeights.Medium };
            _gen2Badge.Child = _gen2BadgeText;
            g2Top.Children.Add(_lblGen2Title); g2Top.Children.Add(_btnGen2Help); g2Top.Children.Add(_gen2Badge);
            _descGen2 = new TextBlock { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
            g2Left.Children.Add(g2Top); g2Left.Children.Add(_descGen2);
            g2Dock.Children.Add(g2Left);
            _gen2Card = MakeCard(g2Dock, new Thickness(12, 10, 12, 10));
            _cardsPanel.Children.Add(_gen2Card);

            // ===== Block 3: GGML_VK_DISABLE_COOPMAT2 Toggle + Apply =====
            var cmDock = new DockPanel();
            var cmRight = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            _toggleTrack = new Border {
                Width = 40, Height = 20, CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(1), Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center
            };
            _toggleThumb = new Ellipse { Width = 12, Height = 12, Margin = new Thickness(3, 0, 3, 0), VerticalAlignment = VerticalAlignment.Center };
            _toggleTrack.Child = _toggleThumb;
            _toggleTrack.MouseLeftButtonUp += (s, e) => { _coopmatDraft = !_coopmatDraft; RenderCoopmat(); };
            _coopmatSwitchLbl = new TextBlock { FontSize = 12, Width = 34, Margin = new Thickness(6, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            _btnApplyCoopmat = new Button { VerticalAlignment = VerticalAlignment.Center };
            _btnApplyCoopmat.Click += OnApplyCoopmatClick;
            cmRight.Children.Add(_toggleTrack); cmRight.Children.Add(_coopmatSwitchLbl); cmRight.Children.Add(_btnApplyCoopmat);
            DockPanel.SetDock(cmRight, Dock.Right);
            cmDock.Children.Add(cmRight);

            var cmLeft = new StackPanel();
            var cmTop = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
            _lblCoopmat = new TextBlock { Text = "GGML_VK_DISABLE_COOPMAT2", FontWeight = FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            _coopmatAppliedBadge = new Border { CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), Padding = new Thickness(6, 1, 6, 1), VerticalAlignment = VerticalAlignment.Center };
            _coopmatAppliedText = new TextBlock { FontSize = 10.5, FontFamily = new FontFamily("Consolas") };
            _coopmatAppliedBadge.Child = _coopmatAppliedText;
            cmTop.Children.Add(_lblCoopmat); cmTop.Children.Add(_coopmatAppliedBadge);
            _descCoopmat = new TextBlock { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
            cmLeft.Children.Add(cmTop); cmLeft.Children.Add(_descCoopmat);
            cmDock.Children.Add(cmLeft);
            _coopmatCard = MakeCard(cmDock, new Thickness(12, 10, 12, 10));
            _cardsPanel.Children.Add(_coopmatCard);

            // ===== Block 4: Action Buttons =====
            var actGrid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            for (int i = 0; i < 4; i++) {
                if (i > 0) actGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
                actGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            }
            _btnInstall = new Button { Padding = new Thickness(8, 8, 8, 8) };
            _btnPause   = new Button { Padding = new Thickness(8, 8, 8, 8) };
            _btnTest    = new Button { Padding = new Thickness(8, 8, 8, 8) };
            _btnRemove  = new Button { Padding = new Thickness(8, 8, 8, 8) };

            _btnInstall.Click += OnInstallClick;
            _btnPause.Click   += OnPauseClick;
            _btnTest.Click    += OnTestClick;
            _btnRemove.Click  += OnRemoveClick;

            Grid.SetColumn(_btnInstall, 0); actGrid.Children.Add(_btnInstall);
            Grid.SetColumn(_btnPause, 2);   actGrid.Children.Add(_btnPause);
            Grid.SetColumn(_btnTest, 4);    actGrid.Children.Add(_btnTest);
            Grid.SetColumn(_btnRemove, 6);  actGrid.Children.Add(_btnRemove);
            _cardsPanel.Children.Add(actGrid);

            // Test Progress Bar
            _testProgressWrap = new StackPanel { Margin = new Thickness(0, 0, 0, 10), Visibility = Visibility.Collapsed };
            var tpTop = new DockPanel { Margin = new Thickness(0, 0, 0, 3) };
            _testProgressPct = new TextBlock { FontSize = 11.5, FontFamily = new FontFamily("Consolas") };
            DockPanel.SetDock(_testProgressPct, Dock.Right);
            tpTop.Children.Add(_testProgressPct);
            _testProgressLbl = new TextBlock { FontSize = 11.5 };
            tpTop.Children.Add(_testProgressLbl);
            _testProgressBar = new ProgressBar { Height = 6, Minimum = 0, Maximum = 100, BorderThickness = new Thickness(0) };
            _testProgressWrap.Children.Add(tpTop);
            _testProgressWrap.Children.Add(_testProgressBar);
            _cardsPanel.Children.Add(_testProgressWrap);

            // ===== Block 5: Event Log (stretches vertically in 1-col, right column in 2-col) =====
            _logSection = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 2, 0, 0) };
            _logHead = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            _btnOpenFolder = new Button { Padding = new Thickness(2, 0, 2, 0), FontSize = 11.5 };
            _btnOpenFolder.Click += (s, e) => {
                try { Directory.CreateDirectory(InstallDir); Process.Start("explorer.exe", InstallDir); } catch {}
            };
            DockPanel.SetDock(_btnOpenFolder, Dock.Right);
            _logHead.Children.Add(_btnOpenFolder);
            _lblLogTitle = new TextBlock { FontSize = 11.5, FontWeight = FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center };
            _logHead.Children.Add(_lblLogTitle);
            DockPanel.SetDock(_logHead, Dock.Top);
            _logSection.Children.Add(_logHead);

            _logBox = new TextBox {
                IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
                VerticalAlignment = VerticalAlignment.Stretch, HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                BorderThickness = new Thickness(0),
                FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 11, Padding = new Thickness(8, 6, 8, 6)
            };
            _logWrap = new Border {
                MinHeight = 110, VerticalAlignment = VerticalAlignment.Stretch,
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
                Padding = new Thickness(1), Child = _logBox
            };
            _logSection.Children.Add(_logWrap);

            _cardsScroll = new ScrollViewer {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalAlignment = VerticalAlignment.Top,
                Content = _cardsPanel
            };

            _bodyGrid = new Grid();
            _bodyCol0 = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
            _bodyCol1 = new ColumnDefinition { Width = new GridLength(0) };
            _bodyRow0 = new RowDefinition { Height = GridLength.Auto };
            _bodyRow1 = new RowDefinition { Height = new GridLength(1, GridUnitType.Star) };
            _bodyGrid.ColumnDefinitions.Add(_bodyCol0);
            _bodyGrid.ColumnDefinitions.Add(_bodyCol1);
            _bodyGrid.RowDefinitions.Add(_bodyRow0);
            _bodyGrid.RowDefinitions.Add(_bodyRow1);

            Grid.SetRow(_cardsScroll, 0); Grid.SetColumn(_cardsScroll, 0);
            Grid.SetRow(_logSection, 1);  Grid.SetColumn(_logSection, 0);
            _bodyGrid.Children.Add(_cardsScroll);
            _bodyGrid.Children.Add(_logSection);

            Grid.SetRow(_bodyGrid, 1);
            _outerGrid.Children.Add(_bodyGrid);
            Content = _outerGrid;
        }

        void ApplyThemeToChrome() {
            ApplyTitleBarTheme(this, _pal.IsDark);
            Background = _pal.BgWindow;
            Foreground = _pal.FgPrimary;
            if (Application.Current != null) {
                Application.Current.Resources["ScrollTrackBrush"] = _pal.ScrollTrack;
                Application.Current.Resources["ScrollThumbBrush"] = _pal.ScrollThumb;
                Application.Current.Resources["ScrollThumbHoverBrush"] = _pal.ScrollThumbHover;
                Application.Current.Resources["ScrollThumbDragBrush"] = _pal.ScrollThumbDrag;
            }

            _titleDot.Background = _pal.Accent;
            _titleText.Foreground = _pal.FgPrimary;
            _langBox.Background = _pal.BgCard;
            _langBox.BorderBrush = _pal.BorderSeg;

            _cardMain.Background = _pal.BgCard; _cardMain.BorderBrush = _pal.BorderCard;
            _gen2Card.Background = _pal.BgCard; _gen2Card.BorderBrush = _pal.BorderCard;
            _coopmatCard.Background = _pal.BgCard; _coopmatCard.BorderBrush = _pal.BorderCard;

            _lblStatusTitle.Foreground = _pal.FgMuted;
            _dividerPipe.Foreground = _pal.DividerFg;
            _lblTdr5m.Foreground = _pal.FgMuted;
            _lblTdrStart.Foreground = _pal.FgMuted;
            _lblCpu.Foreground = _pal.FgMuted;
            _valCpu.Foreground = _pal.FgPrimary;
            _lblFixVram.Foreground = _pal.FgMuted;
            _valFixVram.Foreground = _pal.Accent;

            _pendingHintBox.Background = _pal.YellowBg; _pendingHintBox.BorderBrush = _pal.YellowBorder;
            _pendingHintText.Foreground = _pal.YellowFg;

            _vkLegBox.Background = _pal.BgSubCard; _vkLegBox.BorderBrush = _pal.BorderSubCard;
            _vkLegTitle.Foreground = _pal.FgPrimary; _legendVk.Foreground = _pal.FgSecondary;
            _cuLegBox.Background = _pal.BgSubCard; _cuLegBox.BorderBrush = _pal.BorderSubCard;
            _cuLegTitle.Foreground = _pal.FgPrimary; _legendCuda.Foreground = _pal.FgSecondary;
            _cuBdg.Background = _pal.GreenBg; _cudaBadgeText.Foreground = _pal.GreenFg;

            _lblGen2Title.Foreground = _pal.FgPrimary; _descGen2.Foreground = _pal.FgSecondary;
            _btnGen2Help.Style = MakeButtonStyle(_pal.BtnSecBg, _pal.BtnSecBorder, _pal.FgPrimary, true);
            _lblCoopmat.Foreground = _pal.FgPrimary; _descCoopmat.Foreground = _pal.FgSecondary;
            _coopmatSwitchLbl.Foreground = _pal.FgPrimary;
            _btnApplyCoopmat.Style = PrimaryBtnStyle();

            _btnPause.Style = SecondaryBtnStyle();
            _btnRemove.Style = DangerBtnStyle();
            _btnTheme.Style = SecondaryBtnStyle();
            _btnUpdate.Style = MakeButtonStyle(_pal.BlueBadgeBg, _pal.BorderRowSel, _pal.Accent, true);

            _testProgressPct.Foreground = _pal.FgSecondary;
            _testProgressBar.Background = _pal.BtnSecBg;
            _testProgressBar.Foreground = _pal.Accent;

            _lblLogTitle.Foreground = _pal.FgMuted;
            _btnOpenFolder.Style = MakeButtonStyle(Brushes.Transparent, Brushes.Transparent, _pal.Accent, false);
            _logWrap.Background = _pal.BgLog;
            _logWrap.BorderBrush = _pal.BorderSubCard;
            _logBox.Background = _pal.BgLog;
            _logBox.Foreground = _pal.FgLog;
            _logBox.CaretBrush = _pal.FgLog;
            _logBox.SelectionBrush = _pal.Accent;
        }

        // ===================== Startup, Update Check & Live Polling =====================
        void OnLoaded(object sender, RoutedEventArgs e) {
            try { SystemEvents.UserPreferenceChanged += OnSystemPreferenceChanged; } catch {}
            LoadCoopmatState();
            ProbeInitialHardwareAndService();
            RefreshTextsAndState();

            _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _pollTimer.Tick += (s, ev) => TriggerBackgroundPoll();
            _pollTimer.Start();
            TriggerBackgroundPoll();
            CheckForUpdatesAsync();
        }

        void CheckForUpdatesAsync() {
            ThreadPool.QueueUserWorkItem(_ => {
                try {
                    ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; // TLS 1.2
                    var req = (HttpWebRequest)WebRequest.Create(LatestReleaseApiUrl);
                    req.UserAgent = "NV-KeepAlive/" + Version;
                    req.Timeout = 3000;
                    req.ReadWriteTimeout = 3000;
                    using (var resp = (HttpWebResponse)req.GetResponse())
                    using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) {
                        string body = sr.ReadToEnd();
                        var m = Regex.Match(body, "\"tag_name\"\\s*:\\s*\"v?([0-9]+\\.[0-9]+\\.[0-9]+)\"");
                        if (m.Success) {
                            Version latest, current;
                            if (System.Version.TryParse(m.Groups[1].Value, out latest) &&
                                System.Version.TryParse(Version, out current) &&
                                latest > current) {
                                string tag = "v" + m.Groups[1].Value;
                                Dispatcher.BeginInvoke((Action)(() => {
                                    _availableUpdateTag = tag;
                                    RefreshTextsAndState();
                                }));
                            }
                        }
                    }
                } catch {}
            });
        }

        void LoadCoopmatState() {
            string val = Environment.GetEnvironmentVariable("GGML_VK_DISABLE_COOPMAT2", EnvironmentVariableTarget.Machine);
            _coopmatApplied = (val == "1");
            _coopmatDraft = _coopmatApplied;
        }

        List<GpuItem> ProbeGpusList(string vkStatus, Dictionary<int, string> applied, Dictionary<string, GpuItem> prevByPci) {
            var list = new List<GpuItem>();
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
                        Luid = (p.Length >= 10 && p[9] != "-") ? p[9] : "",
                        Name = p[2],
                        DeviceIdHex = p[3],
                        TotalVramMiB = vram,
                        IsMining = p[5] == "1",
                        IsCmp40hx = p[6] == "1",
                        HasVulkan = p[7] == "1",
                        HasCuda = p[8] == "1",
                        Backend = (p[7] == "1" && vkStatus == "OK") ? "vulkan" : "cuda"
                    };
                    list.Add(g);
                }
            }

            if (applied != null && applied.Count > 0) {
                foreach (var g in list) {
                    if (applied.ContainsKey(g.Index)) {
                        g.Selected = true;
                        g.Backend = applied[g.Index];
                    } else {
                        g.Selected = (list.Count == 1);
                    }
                }
            } else if (list.Count == 1) {
                list[0].Selected = true;
            } else if (list.Count > 1) {
                bool anyMining = false;
                foreach (var g in list) if (g.IsMining) anyMining = true;
                foreach (var g in list) g.Selected = anyMining ? g.IsMining : true;
            }

            if (prevByPci != null && prevByPci.Count > 0) {
                foreach (var g in list) {
                    GpuItem old;
                    if (!string.IsNullOrEmpty(g.PciBusId) && prevByPci.TryGetValue(g.PciBusId, out old)) {
                        g.Selected = old.Selected;
                        g.Backend = old.Backend;
                    }
                }
            }
            return list;
        }

        void ProbeInitialHardwareAndService() {
            CheckVulkanNow();
            ReadInstalledServiceConfig();
            _gpus = ProbeGpusList(_vkCheckStatus, _appliedTargets, null);
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

            var mArg = Regex.Match(xmlOut, @"<Arguments>(.*?)</Arguments>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (!mArg.Success) return;
            string args = WebUtility.HtmlDecode(mArg.Groups[1].Value);

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
            if (rc != 0 || string.IsNullOrEmpty(xmlOut)) { _gen2State = "absent"; return; }
            bool pointsToWrapper = (xmlOut.IndexOf("gen2-sync-wrapper.cmd", StringComparison.OrdinalIgnoreCase) >= 0);
            _gen2State = (pointsToWrapper && File.Exists(Gen2WrapperPath)) ? "synced" : "unsynced";
        }

        void DisposeVramCounters() {
            foreach (var kv in _vramCounters) { try { kv.Value.Dispose(); } catch {} }
            _vramCounters.Clear();
            _vramCachePid = -1;
        }

        Dictionary<string, double> ReadDaemonVramByLuid(int pid, List<string> luids) {
            var res = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            if (pid <= 0 || luids == null || luids.Count == 0) { DisposeVramCounters(); return res; }
            if (_vramCachePid != pid) { DisposeVramCounters(); _vramCachePid = pid; }
            foreach (string luid in luids) {
                if (string.IsNullOrEmpty(luid)) continue;
                try {
                    PerformanceCounter pc;
                    if (!_vramCounters.TryGetValue(luid, out pc)) {
                        string instName = string.Format("pid_{0}_{1}_phys_0", pid, luid);
                        pc = new PerformanceCounter("GPU Process Memory", "Dedicated Usage", instName, true);
                        _vramCounters[luid] = pc;
                    }
                    double mib = pc.NextValue() / (1024.0 * 1024.0);
                    if (mib < 0) mib = 0;
                    res[luid] = mib;
                } catch {
                    PerformanceCounter bad;
                    if (_vramCounters.TryGetValue(luid, out bad)) {
                        try { bad.Dispose(); } catch {}
                        _vramCounters.Remove(luid);
                    }
                }
            }
            return res;
        }

        void TriggerBackgroundPoll() {
            if (_pollBusy) return;
            _pollBusy = true;
            var gpuLuids = new List<string>();
            var prevByPci = new Dictionary<string, GpuItem>(StringComparer.OrdinalIgnoreCase);
            int prevGpuCount = _gpus.Count;
            var appliedSnap = new Dictionary<int, string>(_appliedTargets);
            foreach (var g in _gpus) {
                if (!string.IsNullOrEmpty(g.Luid)) gpuLuids.Add(g.Luid);
                if (!string.IsNullOrEmpty(g.PciBusId)) prevByPci[g.PciBusId] = g;
            }
            ThreadPool.QueueUserWorkItem(_ => {
                try {
                    bool running = false;
                    int daemonPid = -1;
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
                            daemonPid = p.Id;
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

                    var daemonVramMap = running ? ReadDaemonVramByLuid(daemonPid, gpuLuids) : ReadDaemonVramByLuid(-1, null);
                    string fixVramTxt = "-";
                    if (running) {
                        if (paused) {
                            fixVramTxt = "0.0 MiB";
                        } else if (daemonVramMap.Count > 0) {
                            double sumMiB = 0;
                            foreach (var kv in daemonVramMap) sumMiB += kv.Value;
                            fixVramTxt = sumMiB.ToString("F1", CultureInfo.InvariantCulture) + " MiB";
                        }
                    }

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

                    // Hot-refresh GPU list if Device Manager enabled/disabled an NVIDIA GPU
                    bool topoChanged = _needGpuReprobe || (smiList.Count != prevGpuCount);
                    if (!topoChanged && smiMap.Count > 0) {
                        foreach (string bus in smiMap.Keys) {
                            if (!prevByPci.ContainsKey(bus)) { topoChanged = true; break; }
                        }
                    }
                    List<GpuItem> reprobedGpus = null;
                    string reprobedVk = null;
                    if (topoChanged) {
                        _needGpuReprobe = false;
                        string vkOut;
                        int vkRc = RunProcessCapture(_daemonSourceExe, "--check-vulkan", 5000, out vkOut);
                        reprobedVk = (vkRc == 0 || (vkOut != null && vkOut.StartsWith("OK"))) ? "OK"
                            : ((vkRc == 2 || (vkOut != null && vkOut.Contains("NO_LOADER"))) ? "NO_LOADER" : "NO_NVIDIA_ICD");
                        reprobedGpus = ProbeGpusList(reprobedVk, appliedSnap, prevByPci);
                        if (reprobedGpus.Count > 0 && running) {
                            var newLuids = new List<string>();
                            foreach (var rg in reprobedGpus) if (!string.IsNullOrEmpty(rg.Luid)) newLuids.Add(rg.Luid);
                            daemonVramMap = ReadDaemonVramByLuid(daemonPid, newLuids);
                            if (!paused && daemonVramMap.Count > 0) {
                                double sumMiB = 0;
                                foreach (var kv in daemonVramMap) sumMiB += kv.Value;
                                fixVramTxt = sumMiB.ToString("F1", CultureInfo.InvariantCulture) + " MiB";
                            }
                        } else if (reprobedGpus.Count == 0 && running) {
                            fixVramTxt = "0.0 MiB";
                        }
                    } else if (prevGpuCount == 0 && running) {
                        fixVramTxt = "0.0 MiB";
                    }

                    DateTime startRef = procStart ?? _sessionStart;
                    int tdr5 = CountNvlddmkmSince(DateTime.Now.AddMinutes(-5));
                    int tdrS = CountNvlddmkmSince(startRef);

                    string logTxt = "";
                    if (File.Exists(LogFile)) {
                        try {
                            string[] all = File.ReadAllLines(LogFile, Encoding.UTF8);
                            int from = Math.Max(0, all.Length - 60);
                            logTxt = string.Join(Environment.NewLine, all, from, all.Length - from);
                        } catch {}
                    }

                    Dispatcher.BeginInvoke((Action)(() => {
                        if (reprobedGpus != null) {
                            _vkCheckStatus = reprobedVk;
                            _gpus = reprobedGpus;
                            if (_gpus.Count == 0) _gen2PcieSummary = "";
                            CheckGen2Status();
                        }
                        _serviceRunning = running;
                        _servicePaused = paused;
                        _liveCpuText = cpuTxt;
                        _liveFixVramText = fixVramTxt;
                        _tdr5m = tdr5;
                        _tdrStart = tdrS;

                        string pcie40 = "";
                        for (int i = 0; i < _gpus.Count; i++) {
                            var g = _gpus[i];
                            double dv;
                            if (running && !paused && !string.IsNullOrEmpty(g.Luid) && daemonVramMap.TryGetValue(g.Luid, out dv))
                                g.DaemonVramMiB = dv;
                            else if (running && paused)
                                g.DaemonVramMiB = 0.0;
                            else
                                g.DaemonVramMiB = null;

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
            } catch { return 0; }
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
            if (_gpus.Count == 0) return false;
            return _serviceInstalled && !string.Equals(DraftTargetsSpec(), AppliedTargetsSpec(), StringComparison.OrdinalIgnoreCase);
        }

        void RefreshTextsAndState() {
            _btnRu.Style = _isRu ? PrimaryBtnStyle() : MakeButtonStyle(Brushes.Transparent, Brushes.Transparent, _pal.FgSecondary, false);
            _btnEn.Style = !_isRu ? PrimaryBtnStyle() : MakeButtonStyle(Brushes.Transparent, Brushes.Transparent, _pal.FgSecondary, false);

            if (_themeMode == "auto") _btnTheme.Content = T("Тема: Авто", "Theme: Auto");
            else if (_themeMode == "dark") _btnTheme.Content = T("Тема: Тёмная", "Theme: Dark");
            else _btnTheme.Content = T("Тема: Светлая", "Theme: Light");

            if (!string.IsNullOrEmpty(_availableUpdateTag)) {
                _btnUpdate.Content = T("Обновление " + _availableUpdateTag, "Update " + _availableUpdateTag);
                _btnUpdate.Visibility = Visibility.Visible;
            } else {
                _btnUpdate.Visibility = Visibility.Collapsed;
            }

            _lblStatusTitle.Text = T("ВИДЕОКАРТЫ NVIDIA И ДВИЖКИ", "NVIDIA GPUS & BACKENDS");
            _lblTdr5m.Text = T("TDR (5 мин):", "TDR (5m):");
            _lblTdrStart.Text = T("С запуска:", "Since start:");
            _valTdr5m.Text = _tdr5m.ToString();
            _valTdr5m.Foreground = _tdr5m == 0 ? _pal.GreenFg : _pal.RedFg;
            _valTdrStart.Text = _tdrStart.ToString();
            _valTdrStart.Foreground = _tdrStart == 0 ? _pal.GreenFg : _pal.RedFg;
            _valCpu.Text = _liveCpuText;
            _valFixVram.Text = _liveFixVramText;

            var appParts = new List<string>();
            var appKeys = new List<int>(_appliedTargets.Keys); appKeys.Sort();
            foreach (int k in appKeys) {
                string bName = _appliedTargets[k] == "cuda" ? "CUDA" : "Vulkan";
                appParts.Add((_gpus.Count > 1 ? ("GPU" + k + ":") : "") + bName);
            }
            string appSum = appParts.Count > 0 ? string.Join(", ", appParts.ToArray()) : "-";

            if (_serviceRunning && !_servicePaused && _gpus.Count == 0) {
                _statusPill.Background = _pal.YellowBg; _statusPill.BorderBrush = _pal.YellowBorder;
                _statusDot.Fill = _pal.YellowFg; _statusText.Foreground = _pal.YellowFg;
                _statusText.Text = T("Ожидание GPU", "Standby (no GPU)");
                _btnPause.Content = T("Пауза", "Pause");
            } else if (_serviceRunning && !_servicePaused) {
                _statusPill.Background = _pal.GreenBg; _statusPill.BorderBrush = _pal.GreenBorder;
                _statusDot.Fill = _pal.GreenFg; _statusText.Foreground = _pal.GreenFg;
                _statusText.Text = T("Работает (" + appSum + ")", "Running (" + appSum + ")");
                _btnPause.Content = T("Пауза", "Pause");
            } else if (_serviceRunning && _servicePaused) {
                _statusPill.Background = _pal.YellowBg; _statusPill.BorderBrush = _pal.YellowBorder;
                _statusDot.Fill = _pal.YellowFg; _statusText.Foreground = _pal.YellowFg;
                _statusText.Text = T("На паузе", "Paused");
                _btnPause.Content = T("Возобновить", "Resume");
            } else {
                _statusPill.Background = _pal.RedBg; _statusPill.BorderBrush = _pal.RedBorder;
                _statusDot.Fill = _pal.RedFg; _statusText.Foreground = _pal.RedFg;
                _statusText.Text = _serviceInstalled ? T("Остановлена", "Stopped") : T("Не установлена", "Not installed");
                _btnPause.Content = T("Пауза", "Pause");
            }

            if (_vkCheckStatus == "OK") {
                _vkBadge.Background = _pal.GreenBg; _vkBadgeText.Foreground = _pal.GreenFg;
                _vkBadgeText.Text = T("Доступен", "Available");
            } else {
                _vkBadge.Background = _pal.YellowBg; _vkBadgeText.Foreground = _pal.YellowFg;
                _vkBadgeText.Text = T("Не найден (гайд)", "Missing (guide)");
            }
            _cudaBadgeText.Text = T("Доступен", "Available");
            _legendVk.Text = T("Минимальный расход VRAM (~5-20 MiB). Рекомендуется для большинства систем.",
                               "Minimal VRAM footprint (~5-20 MiB). Recommended for most NVIDIA systems.");
            _legendCuda.Text = T("Повышенный расход VRAM (~200-300 MiB). Если нет Vulkan или на карте он нестабилен.",
                                 "Higher VRAM footprint (~200-300 MiB). Use if Vulkan is missing or unstable on a GPU.");

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
                    Foreground = _pal.RedFg, FontSize = 12, Margin = new Thickness(2, 4, 2, 4)
                });
                return;
            }

            bool multi = _gpus.Count > 1;
            foreach (var gpu in _gpus) {
                var g = gpu;
                bool isSel = multi ? g.Selected : true;
                var row = new Border {
                    Background = isSel ? _pal.BgRowSel : _pal.BgRow,
                    BorderBrush = isSel ? _pal.BorderRowSel : _pal.BorderCard,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(10, 8, 10, 8),
                    Margin = new Thickness(0, 0, 0, 6)
                };

                var rowGrid = new Grid();
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var segBorder = new Border {
                    Background = _pal.BgSeg, BorderBrush = _pal.BorderSeg, BorderThickness = new Thickness(1),
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
                        : MakeButtonStyle(Brushes.Transparent, Brushes.Transparent, vkMissing ? _pal.YellowFg : _pal.FgSecondary, false)
                };
                var bCu = new Button {
                    Content = "CUDA", Margin = new Thickness(2, 0, 0, 0),
                    Padding = new Thickness(8, 3, 8, 3), FontSize = 11,
                    Style = (g.Backend == "cuda")
                        ? PrimaryBtnStyle()
                        : MakeButtonStyle(Brushes.Transparent, Brushes.Transparent, _pal.FgSecondary, false)
                };
                bVk.Click += (s, e) => {
                    g.Selected = true; g.Backend = "vulkan";
                    if (_vkCheckStatus != "OK") ShowVulkanGuideModal();
                    RefreshTextsAndState();
                };
                bCu.Click += (s, e) => {
                    g.Selected = true; g.Backend = "cuda";
                    RefreshTextsAndState();
                };
                segSp.Children.Add(bVk); segSp.Children.Add(bCu);
                segBorder.Child = segSp;
                Grid.SetColumn(segBorder, 1);
                rowGrid.Children.Add(segBorder);

                var leftDock = new DockPanel { VerticalAlignment = VerticalAlignment.Center };
                if (multi) {
                    var cb = new CheckBox { IsChecked = g.Selected, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
                    cb.Checked   += (s, e) => { g.Selected = true; RefreshTextsAndState(); };
                    cb.Unchecked += (s, e) => { g.Selected = false; RefreshTextsAndState(); };
                    DockPanel.SetDock(cb, Dock.Left);
                    leftDock.Children.Add(cb);
                }

                var infoSp = new StackPanel();
                var nameRow = new StackPanel { Orientation = Orientation.Horizontal };
                if (multi) {
                    nameRow.Children.Add(new TextBlock {
                        Text = "[GPU " + g.Index + "] ", FontFamily = new FontFamily("Consolas"), FontSize = 12,
                        Foreground = _pal.Accent, VerticalAlignment = VerticalAlignment.Center
                    });
                }
                nameRow.Children.Add(new TextBlock { Text = g.Name, FontWeight = FontWeights.Medium, Foreground = _pal.FgPrimary, VerticalAlignment = VerticalAlignment.Center });
                if (g.IsMining) {
                    var mBdg = new Border { Background = _pal.MiningBg, CornerRadius = new CornerRadius(3), Padding = new Thickness(5, 0, 5, 1), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                    mBdg.Child = new TextBlock { Text = "CMP / Mining", FontSize = 10, Foreground = _pal.MiningFg };
                    nameRow.Children.Add(mBdg);
                }
                infoSp.Children.Add(nameRow);

                string totVram = !string.IsNullOrEmpty(g.LiveTotalVram) ? g.LiveTotalVram : g.TotalVramMiB.ToString();
                string pciPart = !string.IsNullOrEmpty(g.PciBusId) ? g.PciBusId : g.DeviceIdHex;
                string pciePart = !string.IsNullOrEmpty(g.PcieText) ? (" | " + g.PcieText) : "";
                string fixPart = g.DaemonVramMiB.HasValue
                    ? string.Format(" ({0}: {1} MiB)", T("фикс", "fix"), g.DaemonVramMiB.Value.ToString("F1", CultureInfo.InvariantCulture))
                    : "";
                infoSp.Children.Add(new TextBlock {
                    Text = string.Format("{0}{1} | {2}/{3} MiB{4} | {5} C | {6} W", pciPart, pciePart, g.UsedVram, totVram, fixPart, g.TempC, g.PowerW),
                    FontFamily = new FontFamily("Consolas"), FontSize = 11, Foreground = _pal.FgMuted,
                    Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis
                });
                leftDock.Children.Add(infoSp);
                Grid.SetColumn(leftDock, 0);
                rowGrid.Children.Add(leftDock);

                row.Child = rowGrid;
                _gpuListPanel.Children.Add(row);
            }
        }

        void RenderGen2Block() {
            bool show = false;
            foreach (var g in SelectedGpus()) if (g.IsCmp40hx) { show = true; break; }
            _gen2Card.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (!show) return;

            _lblGen2Title.Text = T("Синхронизация с PCIe Gen2 Unlock", "PCIe Gen2 Unlock Sync");
            _descGen2.Text = T("Чинит Gen2 на чипсетных/нижних слотах (PCIe Bridge Retrain), восстанавливает Gen2 после сна и держит KeepAlive активным при запуске 40HXInstaller -gen2.",
                               "Fixes Gen2 on chipset/secondary PCIe slots (Bridge Retrain), restores Gen2 after sleep, and keeps KeepAlive active during 40HXInstaller -gen2.");

            if (_gen2State == "absent") {
                _gen2Badge.Background = _pal.BtnSecBg; _gen2Badge.BorderBrush = _pal.BtnSecBorder; _gen2BadgeText.Foreground = _pal.FgSecondary;
                _gen2BadgeText.Text = T("Нет", "Not installed");
                _btnGen2Sync.Content = T("Установить", "Install");
                _btnGen2Sync.Style = SecondaryBtnStyle();
            } else if (_gen2State == "unsynced") {
                _gen2Badge.Background = _pal.YellowBg; _gen2Badge.BorderBrush = _pal.YellowBorder; _gen2BadgeText.Foreground = _pal.YellowFg;
                _gen2BadgeText.Text = T("Есть (не синхр.)", "Installed (unsynced)");
                _btnGen2Sync.Content = T("Синхронизировать", "Sync now");
                _btnGen2Sync.Style = PrimaryBtnStyle();
            } else {
                _gen2Badge.Background = _pal.GreenBg; _gen2Badge.BorderBrush = _pal.GreenBorder; _gen2BadgeText.Foreground = _pal.GreenFg;
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
            _toggleTrack.Background = _coopmatDraft ? _pal.Accent : _pal.ToggleOffBg;
            _toggleTrack.BorderBrush = _coopmatDraft ? _pal.Accent : _pal.ToggleOffBorder;
            _toggleThumb.Fill = _coopmatDraft ? _pal.AccentFg : _pal.ToggleOffThumb;
            _toggleThumb.HorizontalAlignment = _coopmatDraft ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            _coopmatSwitchLbl.Text = _coopmatDraft ? T("Вкл", "On") : T("Выкл", "Off");
            _btnApplyCoopmat.Content = T("Применить", "Apply");

            bool dirty = (_coopmatDraft != _coopmatApplied);
            _btnApplyCoopmat.IsEnabled = dirty;
            if (dirty) {
                _coopmatAppliedBadge.Background = _pal.YellowBg; _coopmatAppliedBadge.BorderBrush = _pal.YellowBorder;
                _coopmatAppliedText.Foreground = _pal.YellowFg;
                _coopmatAppliedText.Text = T("Есть изменения", "Unsaved change");
            } else if (_coopmatApplied) {
                _coopmatAppliedBadge.Background = _pal.BlueBadgeBg; _coopmatAppliedBadge.BorderBrush = _pal.BorderRowSel;
                _coopmatAppliedText.Foreground = _pal.Accent;
                _coopmatAppliedText.Text = T("В системе: Вкл", "Applied: On");
            } else {
                _coopmatAppliedBadge.Background = _pal.NeutralBadgeBg; _coopmatAppliedBadge.BorderBrush = _pal.BorderCard;
                _coopmatAppliedText.Foreground = _pal.FgSecondary;
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
            if (needsVulkan && _vkCheckStatus != "OK") { ShowVulkanGuideModal(); return; }
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
                    "$t3 = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Minutes 1);" +
                    "$pr = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest;" +
                    "$st = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Days 0) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1);" +
                    "Register-ScheduledTask -TaskName '" + TaskName + "' -Action $act -Trigger @($t1,$t2,$t3) -Principal $pr -Settings $st -Force | Out-Null;" +
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
            _testProgressLbl.Foreground = _pal.FgSecondary;
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
                        _testProgressLbl.Foreground = errs == 0 ? _pal.GreenFg : _pal.RedFg;
                        _testProgressLbl.Text = errs == 0
                            ? T("Тест пройден: 0 сбоев nvlddmkm за 25с (защита работает)", "Test passed: 0 nvlddmkm errors in 25s (protection active)")
                            : T("Обнаружено сбоев nvlddmkm: " + errs, "Detected nvlddmkm errors: " + errs);
                        AppendUiLog(string.Format("25s anti-TDR test finished (targets={0}): {1} nvlddmkm errors.", targets, errs));
                    }));
                } catch (Exception ex) {
                    Dispatcher.BeginInvoke((Action)(() => {
                        _testRunning = false;
                        _btnTest.IsEnabled = true;
                        _testProgressLbl.Foreground = _pal.RedFg;
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
            if (_gen2State == "absent") { ShowGen2InfoModal(); return; }
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

            try {
                Directory.CreateDirectory(InstallDir);
                if (!File.Exists(InstalledExe) && File.Exists(_daemonSourceExe)) File.Copy(_daemonSourceExe, InstalledExe, true);

                string xmlOut;
                if (RunProcessCapture("schtasks.exe", "/Query /TN \"" + Gen2TaskName + "\" /XML", 5000, out xmlOut) != 0 || string.IsNullOrEmpty(xmlOut))
                    throw new Exception("Cannot read scheduled task '" + Gen2TaskName + "'.");

                var mCmd = Regex.Match(xmlOut, @"<Command>(.*?)</Command>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                var mArg = Regex.Match(xmlOut, @"<Arguments>(.*?)</Arguments>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                if (!mCmd.Success) throw new Exception("Task XML has no <Command> element.");

                string origExe = WebUtility.HtmlDecode(mCmd.Groups[1].Value).Trim().Trim('"');
                string origArgs = mArg.Success ? WebUtility.HtmlDecode(mArg.Groups[1].Value).Trim() : "-gen2 -silent";

                if (origArgs.IndexOf("gen2-sync-wrapper.cmd", StringComparison.OrdinalIgnoreCase) >= 0) {
                    string recovered = null;
                    if (File.Exists(Gen2OrigCmdPath)) {
                        string[] lines = File.ReadAllLines(Gen2OrigCmdPath, Encoding.UTF8);
                        if (lines.Length >= 1 && File.Exists(lines[0].Trim())) {
                            recovered = lines[0].Trim();
                            if (lines.Length >= 2) origArgs = lines[1].Trim();
                        }
                    }
                    if (string.IsNullOrEmpty(recovered)) {
                        string[] roots = new[] {
                            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
                            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
                        };
                        foreach (string r in roots) {
                            if (!Directory.Exists(r)) continue;
                            try {
                                string[] hits = Directory.GetFiles(r, "40HXInstaller.exe", SearchOption.AllDirectories);
                                if (hits.Length > 0) { recovered = hits[0]; origArgs = "-gen2 -silent"; break; }
                            } catch {}
                        }
                    }
                    if (string.IsNullOrEmpty(recovered))
                        throw new Exception("gen2-sync-wrapper.cmd is missing and 40HXInstaller.exe could not be located automatically.");
                    origExe = recovered;
                } else {
                    if (!File.Exists(Gen2OrigXmlPath)) File.WriteAllText(Gen2OrigXmlPath, xmlOut, Encoding.Unicode);
                    File.WriteAllText(Gen2OrigCmdPath, origExe + Environment.NewLine + origArgs, Encoding.UTF8);
                }

                string wrapper =
                    "@echo off\r\n" +
                    "setlocal\r\n" +
                    "set \"KA_DIR=" + InstallDir + "\"\r\n" +
                    "if exist \"" + origExe + "\" (\r\n" +
                    "    \"" + origExe + "\" " + origArgs + "\r\n" +
                    ")\r\n" +
                    "if exist \"%KA_DIR%\\NvKeepAlive.exe\" (\r\n" +
                    "    \"%KA_DIR%\\NvKeepAlive.exe\" --retrain-gen2 --wait 20 --log \"%KA_DIR%\\keepalive.log\"\r\n" +
                    ")\r\n" +
                    "endlocal\r\n";
                File.WriteAllText(Gen2WrapperPath, wrapper, Encoding.ASCII);

                string ps =
                    "$ErrorActionPreference='Stop';" +
                    "$t = Get-ScheduledTask -TaskName '" + Gen2TaskName + "';" +
                    "$act = New-ScheduledTaskAction -Execute 'cmd.exe' -Argument '/c \"" + Gen2WrapperPath.Replace("'", "''") + "\"' -WorkingDirectory '" + InstallDir.Replace("'", "''") + "';" +
                    "$hasWake = $false; foreach ($tr in $t.Triggers) { if ($tr.Subscription -and $tr.Subscription -match 'Power-Troubleshooter') { $hasWake = $true } };" +
                    "if ($hasWake) { $trigs = @($t.Triggers) } else {" +
                    "  $cim = Get-CimClass -ClassName MSFT_TaskEventTrigger -Namespace Root/Microsoft/Windows/TaskScheduler;" +
                    "  $wake = New-CimInstance -CimClass $cim -ClientOnly; $wake.Enabled = $true; $wake.Delay = 'PT5S';" +
                    "  $wake.Subscription = '<QueryList><Query Id=\"0\" Path=\"System\"><Select Path=\"System\">*[System[Provider[@Name=''Microsoft-Windows-Power-Troubleshooter''] and EventID=1]]</Select></Query></QueryList>';" +
                    "  $trigs = @($t.Triggers) + @($wake);" +
                    "};" +
                    "Set-ScheduledTask -TaskName '" + Gen2TaskName + "' -Action $act -Trigger $trigs | Out-Null;";

                string psOut;
                int rc = RunPowerShell(ps, 12000, out psOut);
                if (rc != 0) throw new Exception(psOut);

                CheckGen2Status();
                AppendUiLog("Synced '" + Gen2TaskName + "' with NV-KeepAlive pause + built-in PCIe Gen2 chipset-bridge retrain + wake trigger.");

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
            for (int i = 0; i < 12; i++) {
                var rem = Process.GetProcessesByName("NvKeepAlive");
                bool any = rem.Length > 0;
                foreach (var r in rem) r.Dispose();
                if (!any) break;
                Thread.Sleep(200);
            }
        }

        // ===================== Modal 1: Vulkan Missing Installation Guide =====================
        void ShowVulkanGuideModal() {
            var dlg = new Window {
                Title = T("Vulkan Runtime не обнаружен", "Vulkan Runtime Not Found"),
                Width = 510, SizeToContent = SizeToContent.Height,
                Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize, Background = _pal.BgModal, Foreground = _pal.FgPrimary,
                FontFamily = FontFamily, FontSize = 12.5
            };
            dlg.SourceInitialized += (s, e) => ApplyTitleBarTheme(dlg, _pal.IsDark);

            var sp = new StackPanel { Margin = new Thickness(16) };
            string reason = (_vkCheckStatus == "NO_LOADER")
                ? T("Причина: отсутствует системная библиотека vulkan-1.dll (Vulkan Loader).", "Reason: system vulkan-1.dll (Vulkan Loader) is missing.")
                : T("Причина: vulkan-1.dll найден, но драйвер NVIDIA не зарегистрировал Vulkan ICD.", "Reason: vulkan-1.dll found, but NVIDIA driver has no Vulkan ICD registered.");
            sp.Children.Add(new TextBlock { Text = reason, Foreground = _pal.YellowFg, FontFamily = new FontFamily("Consolas"), FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
            sp.Children.Add(new TextBlock {
                Text = T("Для работы лёгкого движка Vulkan Compute (~5-20 MiB VRAM) требуется системный Vulkan Runtime и поддержка Vulkan в драйвере NVIDIA. Выберите один из вариантов:",
                         "The lightweight Vulkan Compute backend (~5-20 MiB VRAM) requires the system Vulkan Runtime and NVIDIA Vulkan ICD. Choose one of the options below:"),
                Foreground = _pal.FgPrimary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10)
            });

            Action<string, string, string, string> addStep = (title, body, linkLbl, url) => {
                var c = new StackPanel();
                c.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Foreground = _pal.FgPrimary, Margin = new Thickness(0, 0, 0, 2) });
                c.Children.Add(new TextBlock { Text = body, Foreground = _pal.FgSecondary, FontSize = 11.5, TextWrapping = TextWrapping.Wrap });
                if (!string.IsNullOrEmpty(url)) {
                    var lnk = new Button {
                        Content = linkLbl, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0),
                        Padding = new Thickness(0), Style = MakeButtonStyle(Brushes.Transparent, Brushes.Transparent, _pal.Accent, true)
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
                    feedbackBox.Background = _pal.GreenBg; feedbackBox.BorderBrush = _pal.GreenBorder; feedbackTxt.Foreground = _pal.GreenFg;
                    feedbackTxt.Text = T("Vulkan обнаружен! Окно закроется автоматически.", "Vulkan detected! Closing dialog.");
                    RefreshTextsAndState();
                    dlg.Close();
                } else {
                    feedbackBox.Background = _pal.RedBg; feedbackBox.BorderBrush = _pal.RedBorder; feedbackTxt.Foreground = _pal.RedFg;
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
                ResizeMode = ResizeMode.NoResize, Background = _pal.BgModal, Foreground = _pal.FgPrimary,
                FontFamily = FontFamily, FontSize = 12.5
            };
            dlg.SourceInitialized += (s, e) => ApplyTitleBarTheme(dlg, _pal.IsDark);

            var sp = new StackPanel { Margin = new Thickness(16) };

            var stDock = new DockPanel();
            var stBdg = new Border { CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1), Padding = new Thickness(8, 2, 8, 2) };
            var stTxt = new TextBlock { FontSize = 11.5, FontWeight = FontWeights.Medium };
            if (_gen2State == "absent") {
                stBdg.Background = _pal.BtnSecBg; stBdg.BorderBrush = _pal.BtnSecBorder; stTxt.Foreground = _pal.FgSecondary;
                stTxt.Text = T("Нет", "Not installed");
            } else if (_gen2State == "unsynced") {
                stBdg.Background = _pal.YellowBg; stBdg.BorderBrush = _pal.YellowBorder; stTxt.Foreground = _pal.YellowFg;
                stTxt.Text = T("Есть (не синхр.)", "Installed (unsynced)");
            } else {
                stBdg.Background = _pal.GreenBg; stBdg.BorderBrush = _pal.GreenBorder; stTxt.Foreground = _pal.GreenFg;
                stTxt.Text = T("Оптимизировано", "Optimized") + (string.IsNullOrEmpty(_gen2PcieSummary) ? "" : (" | " + _gen2PcieSummary));
            }
            stBdg.Child = stTxt;
            DockPanel.SetDock(stBdg, Dock.Right);
            stDock.Children.Add(stBdg);
            stDock.Children.Add(new TextBlock { Text = T("Статус в системе:", "System status:"), Foreground = _pal.FgMuted, VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(MakeCard(stDock, new Thickness(10, 8, 10, 8)));

            sp.Children.Add(new TextBlock {
                Text = T("CMP40HX-Unlock (Cyridd/cmpunlocker) - сторонний открытый проект для NVIDIA CMP 40HX (DEV=0x1F0B): разблокирует полные вычисления FP16/Tensor Cores, 8 GiB Resizable BAR и переключение PCIe с Gen1 на Gen2.",
                         "CMP40HX-Unlock (Cyridd/cmpunlocker) is an independent open-source project for NVIDIA CMP 40HX (DEV=0x1F0B) that unlocks full FP16/Tensor Core compute, 8 GiB Resizable BAR, and PCIe Gen2 speed."),
                Foreground = _pal.FgPrimary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8)
            });
            sp.Children.Add(new TextBlock {
                Text = T("Синхронизация в NV-KeepAlive выполняет 3 вещи: запускает наш фикс чипсетного PCIe-моста после 40HXInstaller -gen2 (дожимает Gen1 -> Gen2 на нижних слотах, где анлокер не находит Root Port), держит KeepAlive активным для защиты EFI-анлока от сброса PnP и восстанавливает Gen2 после пробуждения из сна.",
                         "NV-KeepAlive sync performs 3 steps: runs our built-in PCIe chipset-bridge retrain fix after 40HXInstaller -gen2 (lifts Gen1 -> Gen2 in secondary slots where the unlocker misses the Root Port), keeps KeepAlive active to shield EFI-unlocked cards from PnP resets, and restores Gen2 after waking from sleep."),
                Foreground = _pal.FgSecondary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12)
            });

            var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            bool isInstalled = (_gen2State != "absent");
            var bInst = new Button {
                Content = isInstalled ? T("Посетить", "Visit GitHub") : T("Установить", "Install"),
                Style = isInstalled ? SecondaryBtnStyle() : PrimaryBtnStyle(),
                Margin = new Thickness(0, 0, 8, 0)
            };
            var bClose = new Button { Content = T("Закрыть", "Close"), Style = SecondaryBtnStyle() };
            bInst.Click += (s, e) => OpenUrl(isInstalled ? UnlockerRepoUrl : UnlockerUrl);
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
            string themeMode = "auto";
            for (int i = 0; i < args.Length; i++) {
                if (args[i] == "--no-elevate") noElevate = true;
                else if (args[i] == "--autoclose" && i + 1 < args.Length) int.TryParse(args[++i], out autoCloseSec);
                else if (args[i] == "--theme" && i + 1 < args.Length) themeMode = args[++i].ToLowerInvariant();
            }

            if (!noElevate && !IsAdministrator()) {
                try {
                    Process.Start(new ProcessStartInfo {
                        FileName = Process.GetCurrentProcess().MainModule.FileName,
                        UseShellExecute = true,
                        Verb = "runas"
                    });
                    return 0;
                } catch {}
            }

            var app = new Application();
            var win = new MainWindow(themeMode);
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

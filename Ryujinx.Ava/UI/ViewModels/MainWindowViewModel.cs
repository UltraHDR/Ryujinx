using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using DynamicData;
using DynamicData.Binding;
using LibHac.Fs;
using LibHac.FsSystem;
using Ryujinx.Ava.Common;
using Ryujinx.Ava.Common.Locale;
using Ryujinx.Ava.Input;
using Ryujinx.Ava.UI.Controls;
using Ryujinx.Ava.UI.Helpers;
using Ryujinx.Ava.UI.Models;
using Ryujinx.Ava.UI.Windows;
using Ryujinx.Common;
using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using Ryujinx.Cpu;
using Ryujinx.HLE;
using Ryujinx.HLE.FileSystem;
using Ryujinx.HLE.HOS;
using Ryujinx.HLE.HOS.Services.Account.Acc;
using Ryujinx.HLE.Ui;
using Ryujinx.Ui.App.Common;
using Ryujinx.Ui.Common;
using Ryujinx.Ui.Common.Configuration;
using Ryujinx.Ui.Common.Helper;
using SixLabors.ImageSharp.PixelFormats;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Path = System.IO.Path;
using ShaderCacheLoadingState = Ryujinx.Graphics.Gpu.Shader.ShaderCacheState;
using UserId = LibHac.Fs.UserId;

namespace Ryujinx.Ava.UI.ViewModels
{
    public class MainWindowViewModel : BaseModel
    {
        private const int HotKeyPressDelayMs = 500;

        private ObservableCollection<ApplicationData> _applications;
        private string _aspectStatusText;

        private string _loadHeading;
        private string _cacheLoadStatus;
        private string _searchText;
        private Timer _searchTimer;
        private string _dockedStatusText;
        private string _fifoStatusText;
        private string _gameStatusText;
        private string _volumeStatusText;
        private string _gpuStatusText;
        private bool _isAmiiboRequested;
        private bool _isGameRunning;
        private bool _isFullScreen;
        private int _progressMaximum;
        private int _progressValue;
        private long _lastFullscreenToggle = Environment.TickCount64;
        private bool _showLoadProgress;
        private bool _showMenuAndStatusBar = true;
        private bool _showStatusSeparator;
        private Brush _progressBarForegroundColor;
        private Brush _progressBarBackgroundColor;
        private Brush _vsyncColor;
        private byte[] _selectedIcon;
        private bool _isAppletMenuActive;
        private int _statusBarProgressMaximum;
        private int _statusBarProgressValue;
        private bool _isPaused;
        private bool _showContent = true;
        private bool _isLoadingIndeterminate = true;
        private bool _showAll;
        private string _lastScannedAmiiboId;
        private bool _statusBarVisible;
        private ReadOnlyObservableCollection<ApplicationData> _appsObservableList;

        private string _showUiKey = "F4";
        private string _pauseKey = "F5";
        private string _screenshotKey = "F8";
        private float _volume;
        private string _backendText;

        private bool _canUpdate;
        private Cursor _cursor;
        private string _title;
        private string _currentEmulatedGamePath;
        private AutoResetEvent _rendererWaitEvent;
        private WindowState _windowState;
        private bool _isActive;

        public ApplicationData ListSelectedApplication;
        public ApplicationData GridSelectedApplication;

        public event Action ReloadGameList;

        private string TitleName { get; set; }
        internal AppHost AppHost { get; set; }

        public MainWindowViewModel()
        {
            Applications = new ObservableCollection<ApplicationData>();

            Applications.ToObservableChangeSet()
                .Filter(Filter)
                .Sort(GetComparer())
                .Bind(out _appsObservableList).AsObservableList();

            _rendererWaitEvent = new AutoResetEvent(false);

            if (Program.PreviewerDetached)
            {
                LoadConfigurableHotKeys();

                Volume = ConfigurationState.Instance.System.AudioVolume;
            }
        }

        public void Initialize(
            ContentManager contentManager,
            ApplicationLibrary applicationLibrary,
            VirtualFileSystem virtualFileSystem,
            AccountManager accountManager,
            Ryujinx.Input.HLE.InputManager inputManager,
            UserChannelPersistence userChannelPersistence,
            LibHacHorizonManager libHacHorizonManager,
            IHostUiHandler uiHandler,
            Action<bool> showLoading,
            Action<bool> switchToGameControl,
            Action<Control> setMainContent,
            TopLevel topLevel)
        {
            ContentManager = contentManager;
            ApplicationLibrary = applicationLibrary;
            VirtualFileSystem = virtualFileSystem;
            AccountManager = accountManager;
            InputManager = inputManager;
            UserChannelPersistence = userChannelPersistence;
            LibHacHorizonManager = libHacHorizonManager;
            UiHandler = uiHandler;

            ShowLoading = showLoading;
            SwitchToGameControl = switchToGameControl;
            SetMainContent = setMainContent;
            TopLevel = topLevel;
        }

#region Properties

        public string SearchText
        {
            get => _searchText;
            set
            {
                _searchText = value;

                _searchTimer?.Dispose();

                _searchTimer = new Timer(TimerCallback, null, 1000, 0);
            }
        }

        private void TimerCallback(object obj)
        {
            RefreshView();

            _searchTimer.Dispose();
            _searchTimer = null;
        }

        public bool CanUpdate
        {
            get => _canUpdate;
            set
            {
                _canUpdate = value;

                OnPropertyChanged();
            }
        }

        public Cursor Cursor
        {
            get => _cursor;
            set
            {
                _cursor = value;
                OnPropertyChanged();
            }
        }

        public ReadOnlyObservableCollection<ApplicationData> AppsObservableList
        {
            get => _appsObservableList;
            set
            {
                _appsObservableList = value;

                OnPropertyChanged();
            }
        }

        public bool IsPaused
        {
            get => _isPaused;
            set
            {
                _isPaused = value;

                OnPropertyChanged();
            }
        }

        public long LastFullscreenToggle
        {
            get => _lastFullscreenToggle;
            set
            {
                _lastFullscreenToggle = value;

                OnPropertyChanged();
            }
        }

        public bool StatusBarVisible
        {
            get => _statusBarVisible && EnableNonGameRunningControls;
            set
            {
                _statusBarVisible = value;

                OnPropertyChanged();
            }
        }

        public bool EnableNonGameRunningControls => !IsGameRunning;

        public bool ShowFirmwareStatus => !ShowLoadProgress;

        public bool IsGameRunning
        {
            get => _isGameRunning;
            set
            {
                _isGameRunning = value;

                if (!value)
                {
                    ShowMenuAndStatusBar = false;
                }

                OnPropertyChanged();
                OnPropertyChanged(nameof(EnableNonGameRunningControls));
                OnPropertyChanged(nameof(StatusBarVisible));
                OnPropertyChanged(nameof(ShowFirmwareStatus));
            }
        }

        public bool IsAmiiboRequested
        {
            get => _isAmiiboRequested && _isGameRunning;
            set
            {
                _isAmiiboRequested = value;

                OnPropertyChanged();
            }
        }

        public bool ShowLoadProgress
        {
            get => _showLoadProgress;
            set
            {
                _showLoadProgress = value;

                OnPropertyChanged();
                OnPropertyChanged(nameof(ShowFirmwareStatus));
            }
        }

        public string GameStatusText
        {
            get => _gameStatusText;
            set
            {
                _gameStatusText = value;

                OnPropertyChanged();
            }
        }

        public bool IsFullScreen
        {
            get => _isFullScreen;
            set
            {
                _isFullScreen = value;

                OnPropertyChanged();
            }
        }

        public bool ShowAll
        {
            get => _showAll;
            set
            {
                _showAll = value;

                OnPropertyChanged();
            }
        }

        public string LastScannedAmiiboId
        {
            get => _lastScannedAmiiboId;
            set
            {
                _lastScannedAmiiboId = value;

                OnPropertyChanged();
            }
        }

        public ApplicationData SelectedApplication
        {
            get
            {
                return Glyph switch
                {
                    Glyph.List => ListSelectedApplication,
                    Glyph.Grid => GridSelectedApplication,
                    _ => null,
                };
            }
        }

        public string LoadHeading
        {
            get => _loadHeading;
            set
            {
                _loadHeading = value;

                OnPropertyChanged();
            }
        }

        public string CacheLoadStatus
        {
            get => _cacheLoadStatus;
            set
            {
                _cacheLoadStatus = value;

                OnPropertyChanged();
            }
        }

        public Brush ProgressBarBackgroundColor
        {
            get => _progressBarBackgroundColor;
            set
            {
                _progressBarBackgroundColor = value;

                OnPropertyChanged();
            }
        }

        public Brush ProgressBarForegroundColor
        {
            get => _progressBarForegroundColor;
            set
            {
                _progressBarForegroundColor = value;

                OnPropertyChanged();
            }
        }

        public Brush VsyncColor
        {
            get => _vsyncColor;
            set
            {
                _vsyncColor = value;

                OnPropertyChanged();
            }
        }

        public byte[] SelectedIcon
        {
            get => _selectedIcon;
            set
            {
                _selectedIcon = value;

                OnPropertyChanged();
            }
        }

        public int ProgressMaximum
        {
            get => _progressMaximum;
            set
            {
                _progressMaximum = value;

                OnPropertyChanged();
            }
        }

        public int ProgressValue
        {
            get => _progressValue;
            set
            {
                _progressValue = value;

                OnPropertyChanged();
            }
        }

        public int StatusBarProgressMaximum
        {
            get => _statusBarProgressMaximum;
            set
            {
                _statusBarProgressMaximum = value;

                OnPropertyChanged();
            }
        }

        public int StatusBarProgressValue
        {
            get => _statusBarProgressValue;
            set
            {
                _statusBarProgressValue = value;

                OnPropertyChanged();
            }
        }

        public string FifoStatusText
        {
            get => _fifoStatusText;
            set
            {
                _fifoStatusText = value;

                OnPropertyChanged();
            }
        }

        public string GpuNameText
        {
            get => _gpuStatusText;
            set
            {
                _gpuStatusText = value;

                OnPropertyChanged();
            }
        }

        public string BackendText
        {
            get => _backendText;
            set
            {
                _backendText = value;

                OnPropertyChanged();
            }
        }

        public string DockedStatusText
        {
            get => _dockedStatusText;
            set
            {
                _dockedStatusText = value;

                OnPropertyChanged();
            }
        }

        public string AspectRatioStatusText
        {
            get => _aspectStatusText;
            set
            {
                _aspectStatusText = value;

                OnPropertyChanged();
            }
        }

        public string VolumeStatusText
        {
            get => _volumeStatusText;
            set
            {
                _volumeStatusText = value;

                OnPropertyChanged();
            }
        }

        public bool VolumeMuted => _volume == 0;

        public float Volume
        {
            get => _volume;
            set
            {
                _volume = value;

                if (_isGameRunning)
                {
                    AppHost.Device.SetVolume(_volume);
                }

                OnPropertyChanged(nameof(VolumeStatusText));
                OnPropertyChanged(nameof(VolumeMuted));
                OnPropertyChanged();
            }
        }

        public bool ShowStatusSeparator
        {
            get => _showStatusSeparator;
            set
            {
                _showStatusSeparator = value;

                OnPropertyChanged();
            }
        }

        public bool ShowMenuAndStatusBar
        {
            get => _showMenuAndStatusBar;
            set
            {
                _showMenuAndStatusBar = value;

                OnPropertyChanged();
            }
        }

        public bool IsLoadingIndeterminate
        {
            get => _isLoadingIndeterminate;
            set
            {
                _isLoadingIndeterminate = value;

                OnPropertyChanged();
            }
        }

        public bool IsActive
        {
            get => _isActive;
            set
            {
                _isActive = value;

                OnPropertyChanged();
            }
        }


        public bool ShowContent
        {
            get => _showContent;
            set
            {
                _showContent = value;

                OnPropertyChanged();
            }
        }

        public bool IsAppletMenuActive
        {
            get => _isAppletMenuActive && EnableNonGameRunningControls;
            set
            {
                _isAppletMenuActive = value;

                OnPropertyChanged();
            }
        }

        public WindowState WindowState
        {
            get => _windowState;
            internal set
            {
                _windowState = value;

                OnPropertyChanged();
            }
        }

        public bool IsGrid => Glyph == Glyph.Grid;
        public bool IsList => Glyph == Glyph.List;

        internal void Sort(bool isAscending)
        {
            IsAscending = isAscending;

            RefreshView();
        }

        internal void Sort(ApplicationSort sort)
        {
            SortMode = sort;

            RefreshView();
        }

        public bool StartGamesInFullscreen
        {
            get => ConfigurationState.Instance.Ui.StartFullscreen;
            set
            {
                ConfigurationState.Instance.Ui.StartFullscreen.Value = value;

                ConfigurationState.Instance.ToFileFormat().SaveConfig(Program.ConfigurationPath);

                OnPropertyChanged();
            }
        }

        public bool ShowConsole
        {
            get => ConfigurationState.Instance.Ui.ShowConsole;
            set
            {
                ConfigurationState.Instance.Ui.ShowConsole.Value = value;

                ConfigurationState.Instance.ToFileFormat().SaveConfig(Program.ConfigurationPath);

                OnPropertyChanged();
            }
        }

        public string Title
        {
            get => _title;
            set
            {
                _title = value;

                OnPropertyChanged();
            }
        }

        public bool ShowConsoleVisible
        {
            get => ConsoleHelper.SetConsoleWindowStateSupported;
        }

        public ObservableCollection<ApplicationData> Applications
        {
            get => _applications;
            set
            {
                _applications = value;

                OnPropertyChanged();
            }
        }

        public Glyph Glyph
        {
            get => (Glyph)ConfigurationState.Instance.Ui.GameListViewMode.Value;
            set
            {
                ConfigurationState.Instance.Ui.GameListViewMode.Value = (int)value;

                OnPropertyChanged();
                OnPropertyChanged(nameof(IsGrid));
                OnPropertyChanged(nameof(IsList));

                ConfigurationState.Instance.ToFileFormat().SaveConfig(Program.ConfigurationPath);
            }
        }

        public bool ShowNames
        {
            get => ConfigurationState.Instance.Ui.ShowNames && ConfigurationState.Instance.Ui.GridSize > 1; set
            {
                ConfigurationState.Instance.Ui.ShowNames.Value = value;

                OnPropertyChanged();
                OnPropertyChanged(nameof(GridSizeScale));
                OnPropertyChanged(nameof(GridItemSelectorSize));

                ConfigurationState.Instance.ToFileFormat().SaveConfig(Program.ConfigurationPath);
            }
        }

        internal ApplicationSort SortMode
        {
            get => (ApplicationSort)ConfigurationState.Instance.Ui.ApplicationSort.Value;
            private set
            {
                ConfigurationState.Instance.Ui.ApplicationSort.Value = (int)value;

                OnPropertyChanged();
                OnPropertyChanged(nameof(SortName));

                ConfigurationState.Instance.ToFileFormat().SaveConfig(Program.ConfigurationPath);
            }
        }

        public int ListItemSelectorSize
        {
            get
            {
                switch (ConfigurationState.Instance.Ui.GridSize)
                {
                    case 1:
                        return 78;
                    case 2:
                        return 100;
                    case 3:
                        return 120;
                    case 4:
                        return 140;
                    default:
                        return 16;
                }
            }
        }

        public int GridItemSelectorSize
        {
            get
            {
                switch (ConfigurationState.Instance.Ui.GridSize)
                {
                    case 1:
                        return 120;
                    case 2:
                        return ShowNames ? 210 : 150;
                    case 3:
                        return ShowNames ? 240 : 180;
                    case 4:
                        return ShowNames ? 280 : 220;
                    default:
                        return 16;
                }
            }
        }

        public int GridSizeScale
        {
            get => ConfigurationState.Instance.Ui.GridSize;
            set
            {
                ConfigurationState.Instance.Ui.GridSize.Value = value;

                if (value < 2)
                {
                    ShowNames = false;
                }

                OnPropertyChanged();
                OnPropertyChanged(nameof(IsGridSmall));
                OnPropertyChanged(nameof(IsGridMedium));
                OnPropertyChanged(nameof(IsGridLarge));
                OnPropertyChanged(nameof(IsGridHuge));
                OnPropertyChanged(nameof(ListItemSelectorSize));
                OnPropertyChanged(nameof(GridItemSelectorSize));
                OnPropertyChanged(nameof(ShowNames));

                ConfigurationState.Instance.ToFileFormat().SaveConfig(Program.ConfigurationPath);
            }
        }

        public string SortName
        {
            get
            {
                return SortMode switch
                {
                    ApplicationSort.Title => LocaleManager.Instance[LocaleKeys.GameListHeaderApplication],
                    ApplicationSort.Developer => LocaleManager.Instance[LocaleKeys.GameListHeaderDeveloper],
                    ApplicationSort.LastPlayed => LocaleManager.Instance[LocaleKeys.GameListHeaderLastPlayed],
                    ApplicationSort.TotalTimePlayed => LocaleManager.Instance[LocaleKeys.GameListHeaderTimePlayed],
                    ApplicationSort.FileType => LocaleManager.Instance[LocaleKeys.GameListHeaderFileExtension],
                    ApplicationSort.FileSize => LocaleManager.Instance[LocaleKeys.GameListHeaderFileSize],
                    ApplicationSort.Path => LocaleManager.Instance[LocaleKeys.GameListHeaderPath],
                    ApplicationSort.Favorite => LocaleManager.Instance[LocaleKeys.CommonFavorite],
                    _ => string.Empty,
                };
            }
        }

        public bool IsAscending
        {
            get => ConfigurationState.Instance.Ui.IsAscendingOrder;
            private set
            {
                ConfigurationState.Instance.Ui.IsAscendingOrder.Value = value;

                OnPropertyChanged();
                OnPropertyChanged(nameof(SortMode));
                OnPropertyChanged(nameof(SortName));

                ConfigurationState.Instance.ToFileFormat().SaveConfig(Program.ConfigurationPath);
            }
        }

        public KeyGesture ShowUiKey
        {
            get => KeyGesture.Parse(_showUiKey);
            set
            {
                _showUiKey = value.ToString();

                OnPropertyChanged();
            }
        }

        public KeyGesture ScreenshotKey
        {
            get => KeyGesture.Parse(_screenshotKey);
            set
            {
                _screenshotKey = value.ToString();

                OnPropertyChanged();
            }
        }

        public KeyGesture PauseKey
        {
            get => KeyGesture.Parse(_pauseKey); set
            {
                _pauseKey = value.ToString();

                OnPropertyChanged();
            }
        }

        public ContentManager ContentManager { get; private set; }
        public ApplicationLibrary ApplicationLibrary { get; private set; }
        public VirtualFileSystem VirtualFileSystem { get; private set; }
        public AccountManager AccountManager { get; private set; }
        public Ryujinx.Input.HLE.InputManager InputManager { get; private set; }
        public UserChannelPersistence UserChannelPersistence { get; private set; }
        public Action<bool> ShowLoading { get; private set; }
        public Action<bool> SwitchToGameControl { get; private set; }
        public Action<Control> SetMainContent { get; private set; }
        public TopLevel TopLevel { get; private set; }
        public RendererHost RendererControl { get; private set; }
        public bool IsClosing { get; set; }
        public LibHacHorizonManager LibHacHorizonManager { get; internal set; }
        public IHostUiHandler UiHandler { get; internal set; }
        public bool IsSortedByFavorite => SortMode == ApplicationSort.Favorite;
        public bool IsSortedByTitle => SortMode == ApplicationSort.Title;
        public bool IsSortedByDeveloper => SortMode == ApplicationSort.Developer;
        public bool IsSortedByLastPlayed => SortMode == ApplicationSort.LastPlayed;
        public bool IsSortedByTimePlayed => SortMode == ApplicationSort.TotalTimePlayed;
        public bool IsSortedByType => SortMode == ApplicationSort.FileType;
        public bool IsSortedBySize => SortMode == ApplicationSort.FileSize;
        public bool IsSortedByPath => SortMode == ApplicationSort.Path;
        public bool IsGridSmall => ConfigurationState.Instance.Ui.GridSize == 1;
        public bool IsGridMedium => ConfigurationState.Instance.Ui.GridSize == 2;
        public bool IsGridLarge => ConfigurationState.Instance.Ui.GridSize == 3;
        public bool IsGridHuge => ConfigurationState.Instance.Ui.GridSize == 4;

#endregion

#region PrivateMethods

        private IComparer<ApplicationData> GetComparer()
        {
            return SortMode switch
            {
                ApplicationSort.LastPlayed      => new Models.Generic.LastPlayedSortComparer(IsAscending),
                ApplicationSort.FileSize        => IsAscending  ? SortExpressionComparer<ApplicationData>.Ascending(app => app.FileSizeBytes)
                                                                : SortExpressionComparer<ApplicationData>.Descending(app => app.FileSizeBytes),
                ApplicationSort.TotalTimePlayed => IsAscending  ? SortExpressionComparer<ApplicationData>.Ascending(app => app.TimePlayedNum)
                                                                : SortExpressionComparer<ApplicationData>.Descending(app => app.TimePlayedNum),
                ApplicationSort.Title           => IsAscending  ? SortExpressionComparer<ApplicationData>.Ascending(app => app.TitleName)
                                                                : SortExpressionComparer<ApplicationData>.Descending(app => app.TitleName),
                ApplicationSort.Favorite        => !IsAscending ? SortExpressionComparer<ApplicationData>.Ascending(app => app.Favorite)
                                                                : SortExpressionComparer<ApplicationData>.Descending(app => app.Favorite),
                ApplicationSort.Developer       => IsAscending  ? SortExpressionComparer<ApplicationData>.Ascending(app => app.Developer)
                                                                : SortExpressionComparer<ApplicationData>.Descending(app => app.Developer),
                ApplicationSort.FileType        => IsAscending  ? SortExpressionComparer<ApplicationData>.Ascending(app => app.FileExtension)
                                                                : SortExpressionComparer<ApplicationData>.Descending(app => app.FileExtension),
                ApplicationSort.Path            => IsAscending  ? SortExpressionComparer<ApplicationData>.Ascending(app => app.Path)
                                                                : SortExpressionComparer<ApplicationData>.Descending(app => app.Path),
                _ => null,
            };
        }

        private void RefreshView()
        {
            RefreshGrid();
        }

        private void RefreshGrid()
        {
            Applications.ToObservableChangeSet()
                .Filter(Filter)
                .Sort(GetComparer())
                .Bind(out _appsObservableList).AsObservableList();

            OnPropertyChanged(nameof(AppsObservableList));
        }

        private bool Filter(object arg)
        {
            if (arg is ApplicationData app)
            {
                return string.IsNullOrWhiteSpace(_searchText) || app.TitleName.ToLower().Contains(_searchText.ToLower());
            }

            return false;
        }

        private async Task HandleFirmwareInstallation(string filename)
        {
            try
            {
                SystemVersion firmwareVersion = ContentManager.VerifyFirmwarePackage(filename);

                if (firmwareVersion == null)
                {
                    await ContentDialogHelper.CreateErrorDialog(string.Format(LocaleManager.Instance[LocaleKeys.DialogFirmwareInstallerFirmwareNotFoundErrorMessage], filename));

                    return;
                }

                string dialogTitle = string.Format(LocaleManager.Instance[LocaleKeys.DialogFirmwareInstallerFirmwareInstallTitle], firmwareVersion.VersionString);

                SystemVersion currentVersion = ContentManager.GetCurrentFirmwareVersion();

                string dialogMessage = string.Format(LocaleManager.Instance[LocaleKeys.DialogFirmwareInstallerFirmwareInstallMessage], firmwareVersion.VersionString);

                if (currentVersion != null)
                {
                    dialogMessage += string.Format(LocaleManager.Instance[LocaleKeys.DialogFirmwareInstallerFirmwareInstallSubMessage], currentVersion.VersionString);
                }

                dialogMessage += LocaleManager.Instance[LocaleKeys.DialogFirmwareInstallerFirmwareInstallConfirmMessage];

                UserResult result = await ContentDialogHelper.CreateConfirmationDialog(
                    dialogTitle,
                    dialogMessage,
                    LocaleManager.Instance[LocaleKeys.InputDialogYes],
                    LocaleManager.Instance[LocaleKeys.InputDialogNo],
                    LocaleManager.Instance[LocaleKeys.RyujinxConfirm]);

                UpdateWaitWindow waitingDialog = ContentDialogHelper.CreateWaitingDialog(dialogTitle, LocaleManager.Instance[LocaleKeys.DialogFirmwareInstallerFirmwareInstallWaitMessage]);

                if (result == UserResult.Yes)
                {
                    Logger.Info?.Print(LogClass.Application, $"Installing firmware {firmwareVersion.VersionString}");

                    Thread thread = new(() =>
                    {
                        Dispatcher.UIThread.InvokeAsync(delegate
                        {
                            waitingDialog.Show();
                        });

                        try
                        {
                            ContentManager.InstallFirmware(filename);

                            Dispatcher.UIThread.InvokeAsync(async delegate
                            {
                                waitingDialog.Close();

                                string message = string.Format(LocaleManager.Instance[LocaleKeys.DialogFirmwareInstallerFirmwareInstallSuccessMessage], firmwareVersion.VersionString);

                                await ContentDialogHelper.CreateInfoDialog(dialogTitle, message, LocaleManager.Instance[LocaleKeys.InputDialogOk], "", LocaleManager.Instance[LocaleKeys.RyujinxInfo]);

                                Logger.Info?.Print(LogClass.Application, message);

                                // Purge Applet Cache.

                                DirectoryInfo miiEditorCacheFolder = new DirectoryInfo(Path.Combine(AppDataManager.GamesDirPath, "0100000000001009", "cache"));

                                if (miiEditorCacheFolder.Exists)
                                {
                                    miiEditorCacheFolder.Delete(true);
                                }
                            });
                        }
                        catch (Exception ex)
                        {
                            Dispatcher.UIThread.InvokeAsync(async () =>
                            {
                                waitingDialog.Close();

                                await ContentDialogHelper.CreateErrorDialog(ex.Message);
                            });
                        }
                        finally
                        {
                            RefreshFirmwareStatus();
                        }
                    }) { Name = "GUI.FirmwareInstallerThread" };

                    thread.Start();
                }
            }
            catch (LibHac.Common.Keys.MissingKeyException ex)
            {
                if (Avalonia.Application.Current.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                {
                    Logger.Error?.Print(LogClass.Application, ex.ToString());

                    async void Action() => await UserErrorDialog.ShowUserErrorDialog(UserError.NoKeys, (desktop.MainWindow as MainWindow));

                    Dispatcher.UIThread.Post(Action);
                }
            }
            catch (Exception ex)
            {
                await ContentDialogHelper.CreateErrorDialog(ex.Message);
            }
        }

        private void ProgressHandler<T>(T state, int current, int total) where T : Enum
        {
            Dispatcher.UIThread.Post((() =>
            {
                ProgressMaximum = total;
                ProgressValue = current;

                switch (state)
                {
                    case LoadState ptcState:
                        CacheLoadStatus = $"{current} / {total}";
                        switch (ptcState)
                        {
                            case LoadState.Unloaded:
                            case LoadState.Loading:
                                LoadHeading = LocaleManager.Instance[LocaleKeys.CompilingPPTC];
                                IsLoadingIndeterminate = false;
                                break;
                            case LoadState.Loaded:
                                LoadHeading = string.Format(LocaleManager.Instance[LocaleKeys.LoadingHeading], TitleName);
                                IsLoadingIndeterminate = true;
                                CacheLoadStatus = "";
                                break;
                        }
                        break;
                    case ShaderCacheLoadingState shaderCacheState:
                        CacheLoadStatus = $"{current} / {total}";
                        switch (shaderCacheState)
                        {
                            case ShaderCacheLoadingState.Start:
                            case ShaderCacheLoadingState.Loading:
                                LoadHeading = LocaleManager.Instance[LocaleKeys.CompilingShaders];
                                IsLoadingIndeterminate = false;
                                break;
                            case ShaderCacheLoadingState.Loaded:
                                LoadHeading = string.Format(LocaleManager.Instance[LocaleKeys.LoadingHeading], TitleName);
                                IsLoadingIndeterminate = true;
                                CacheLoadStatus = "";
                                break;
                        }
                        break;
                    default:
                        throw new ArgumentException($"Unknown Progress Handler type {typeof(T)}");
                }
            }));
        }

        private void OpenSaveDirectory(in SaveDataFilter filter, ApplicationData data, ulong titleId)
        {
            ApplicationHelper.OpenSaveDir(in filter, titleId, data.ControlHolder, data.TitleName);
        }

        private async void ExtractLogo()
        {
            var selection = SelectedApplication;
            if (selection != null)
            {
                await ApplicationHelper.ExtractSection(NcaSectionType.Logo, selection.Path);
            }
        }

        private async void ExtractRomFs()
        {
            var selection = SelectedApplication;
            if (selection != null)
            {
                await ApplicationHelper.ExtractSection(NcaSectionType.Data, selection.Path);
            }
        }

        private async void ExtractExeFs()
        {
            var selection = SelectedApplication;
            if (selection != null)
            {
                await ApplicationHelper.ExtractSection(NcaSectionType.Code, selection.Path);
            }
        }

        private void PrepareLoadScreen()
        {
            using MemoryStream stream = new(SelectedIcon);
            using var gameIconBmp = SixLabors.ImageSharp.Image.Load<Bgra32>(stream);

            var dominantColor = IconColorPicker.GetFilteredColor(gameIconBmp).ToPixel<Bgra32>();

            const float colorMultiple = 0.5f;

            Color progressFgColor = Color.FromRgb(dominantColor.R, dominantColor.G, dominantColor.B);
            Color progressBgColor = Color.FromRgb(
                (byte)(dominantColor.R * colorMultiple),
                (byte)(dominantColor.G * colorMultiple),
                (byte)(dominantColor.B * colorMultiple));

            ProgressBarForegroundColor = new SolidColorBrush(progressFgColor);
            ProgressBarBackgroundColor = new SolidColorBrush(progressBgColor);
        }

        private void InitializeGame()
        {
            RendererControl.RendererInitialized += GlRenderer_Created;

            AppHost.StatusUpdatedEvent += Update_StatusBar;
            AppHost.AppExit += AppHost_AppExit;

            _rendererWaitEvent.WaitOne();

            AppHost?.Start();

            AppHost?.DisposeContext();
        }

        private void HandleRelaunch()
        {
            if (UserChannelPersistence.PreviousIndex != -1 && UserChannelPersistence.ShouldRestart)
            {
                UserChannelPersistence.ShouldRestart = false;

                Dispatcher.UIThread.Post(() =>
                {
                    LoadApplication(_currentEmulatedGamePath);
                });
            }
            else
            {
                // Otherwise, clear state.
                UserChannelPersistence = new UserChannelPersistence();
                _currentEmulatedGamePath = null;
            }
        }

        private void Update_StatusBar(object sender, StatusUpdatedEventArgs args)
        {
            if (ShowMenuAndStatusBar && !ShowLoadProgress)
            {
                Dispatcher.UIThread.InvokeAsync(() =>
                {
                    Avalonia.Application.Current.Styles.TryGetResource(args.VSyncEnabled
                        ? "VsyncEnabled"
                        : "VsyncDisabled", out object color);

                    if (color is not null)
                    {
                        VsyncColor = new SolidColorBrush((Color)color);
                    }

                    DockedStatusText = args.DockedMode;
                    AspectRatioStatusText = args.AspectRatio;
                    GameStatusText = args.GameStatus;
                    VolumeStatusText = args.VolumeStatus;
                    FifoStatusText = args.FifoStatus;
                    GpuNameText = args.GpuName;
                    BackendText = args.GpuBackend;

                    ShowStatusSeparator = true;
                });
            }
        }

        private void GlRenderer_Created(object sender, EventArgs e)
        {
            ShowLoading(false);

            _rendererWaitEvent.Set();
        }

#endregion

#region PublicMethods

        public void SetUIProgressHandlers(Switch emulationContext)
        {
            if (emulationContext.Application.DiskCacheLoadState != null)
            {
                emulationContext.Application.DiskCacheLoadState.StateChanged -= ProgressHandler;
                emulationContext.Application.DiskCacheLoadState.StateChanged += ProgressHandler;
            }

            emulationContext.Gpu.ShaderCacheStateChanged -= ProgressHandler;
            emulationContext.Gpu.ShaderCacheStateChanged += ProgressHandler;
        }

        public void LoadConfigurableHotKeys()
        {
            if (AvaloniaKeyboardMappingHelper.TryGetAvaKey((Ryujinx.Input.Key)ConfigurationState.Instance.Hid.Hotkeys.Value.ShowUi, out var showUiKey))
            {
                ShowUiKey = new KeyGesture(showUiKey);
            }

            if (AvaloniaKeyboardMappingHelper.TryGetAvaKey((Ryujinx.Input.Key)ConfigurationState.Instance.Hid.Hotkeys.Value.Screenshot, out var screenshotKey))
            {
                ScreenshotKey = new KeyGesture(screenshotKey);
            }

            if (AvaloniaKeyboardMappingHelper.TryGetAvaKey((Ryujinx.Input.Key)ConfigurationState.Instance.Hid.Hotkeys.Value.Pause, out var pauseKey))
            {
                PauseKey = new KeyGesture(pauseKey);
            }
        }

        public void TakeScreenshot()
        {
            AppHost.ScreenshotRequested = true;
        }

        public void HideUi()
        {
            ShowMenuAndStatusBar = false;
        }

        public void SetListMode()
        {
            Glyph = Glyph.List;
        }

        public void SetGridMode()
        {
            Glyph = Glyph.Grid;
        }

        public async void InstallFirmwareFromFile()
        {
            if (Avalonia.Application.Current.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                OpenFileDialog dialog = new() { AllowMultiple = false };
                dialog.Filters.Add(new FileDialogFilter { Name = LocaleManager.Instance[LocaleKeys.FileDialogAllTypes], Extensions = { "xci", "zip" } });
                dialog.Filters.Add(new FileDialogFilter { Name = "XCI",                                                 Extensions = { "xci" } });
                dialog.Filters.Add(new FileDialogFilter { Name = "ZIP",                                                 Extensions = { "zip" } });

                string[] file = await dialog.ShowAsync(desktop.MainWindow);

                if (file != null && file.Length > 0)
                {
                    await HandleFirmwareInstallation(file[0]);
                }
            }
        }

        public async void InstallFirmwareFromFolder()
        {
            if (Avalonia.Application.Current.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                OpenFolderDialog dialog = new();

                string folder = await dialog.ShowAsync(desktop.MainWindow);

                if (!string.IsNullOrEmpty(folder))
                {
                    await HandleFirmwareInstallation(folder);
                }
            }
        }

        public static void OpenRyujinxFolder()
        {
            OpenHelper.OpenFolder(AppDataManager.BaseDirPath);
        }

        public static void OpenLogsFolder()
        {
            string logPath = Path.Combine(ReleaseInformation.GetBaseApplicationDirectory(), "Logs");

            new DirectoryInfo(logPath).Create();

            OpenHelper.OpenFolder(logPath);
        }

        public void ToggleDockMode()
        {
            if (IsGameRunning)
            {
                ConfigurationState.Instance.System.EnableDockedMode.Value = !ConfigurationState.Instance.System.EnableDockedMode.Value;
            }
        }

        public async void ExitCurrentState()
        {
            if (WindowState == WindowState.FullScreen)
            {
                ToggleFullscreen();
            }
            else if (IsGameRunning)
            {
                await Task.Delay(100);

                AppHost?.ShowExitPrompt();
            }
        }

        public void ChangeLanguage(object obj)
        {
            LocaleManager.Instance.LoadDefaultLanguage();
            LocaleManager.Instance.LoadLanguage((string)obj);
        }

        public async void ManageProfiles()
        {
            await NavigationDialogHost.Show(AccountManager, ContentManager, VirtualFileSystem, LibHacHorizonManager.RyujinxClient);
        }

        public void OpenPtcDirectory()
        {
            ApplicationData selection = SelectedApplication;
            if (selection != null)
            {
                string ptcDir = Path.Combine(AppDataManager.GamesDirPath, selection.TitleId, "cache", "cpu");
                string mainPath = Path.Combine(ptcDir, "0");
                string backupPath = Path.Combine(ptcDir, "1");

                if (!Directory.Exists(ptcDir))
                {
                    Directory.CreateDirectory(ptcDir);
                    Directory.CreateDirectory(mainPath);
                    Directory.CreateDirectory(backupPath);
                }

                OpenHelper.OpenFolder(ptcDir);
            }
        }

        public async void PurgePtcCache()
        {
            ApplicationData selection = SelectedApplication;
            if (selection != null)
            {
                DirectoryInfo mainDir = new(Path.Combine(AppDataManager.GamesDirPath, selection.TitleId, "cache", "cpu", "0"));
                DirectoryInfo backupDir = new(Path.Combine(AppDataManager.GamesDirPath, selection.TitleId, "cache", "cpu", "1"));

                // FIXME: Found a way to reproduce the bold effect on the title name (fork?).
                UserResult result = await ContentDialogHelper.CreateConfirmationDialog(LocaleManager.Instance[LocaleKeys.DialogWarning],
                                                                                       string.Format(LocaleManager.Instance[LocaleKeys.DialogPPTCDeletionMessage], selection.TitleName),
                                                                                       LocaleManager.Instance[LocaleKeys.InputDialogYes],
                                                                                       LocaleManager.Instance[LocaleKeys.InputDialogNo],
                                                                                       LocaleManager.Instance[LocaleKeys.RyujinxConfirm]);

                List<FileInfo> cacheFiles = new();

                if (mainDir.Exists)
                {
                    cacheFiles.AddRange(mainDir.EnumerateFiles("*.cache"));
                }

                if (backupDir.Exists)
                {
                    cacheFiles.AddRange(backupDir.EnumerateFiles("*.cache"));
                }

                if (cacheFiles.Count > 0 && result == UserResult.Yes)
                {
                    foreach (FileInfo file in cacheFiles)
                    {
                        try
                        {
                            file.Delete();
                        }
                        catch (Exception e)
                        {
                            await ContentDialogHelper.CreateErrorDialog(string.Format(LocaleManager.Instance[LocaleKeys.DialogPPTCDeletionErrorMessage], file.Name, e));
                        }
                    }
                }
            }
        }

        public void OpenShaderCacheDirectory()
        {
            ApplicationData selection = SelectedApplication;
            if (selection != null)
            {
                string shaderCacheDir = Path.Combine(AppDataManager.GamesDirPath, selection.TitleId, "cache", "shader");

                if (!Directory.Exists(shaderCacheDir))
                {
                    Directory.CreateDirectory(shaderCacheDir);
                }

                OpenHelper.OpenFolder(shaderCacheDir);
            }
        }

        public void SimulateWakeUpMessage()
        {
            AppHost.Device.System.SimulateWakeUpMessage();
        }

        public async void PurgeShaderCache()
        {
            ApplicationData selection = SelectedApplication;
            if (selection != null)
            {
                DirectoryInfo shaderCacheDir = new(Path.Combine(AppDataManager.GamesDirPath, selection.TitleId, "cache", "shader"));

                // FIXME: Found a way to reproduce the bold effect on the title name (fork?).
                UserResult result = await ContentDialogHelper.CreateConfirmationDialog(LocaleManager.Instance[LocaleKeys.DialogWarning],
                                                                                       string.Format(LocaleManager.Instance[LocaleKeys.DialogShaderDeletionMessage], selection.TitleName),
                                                                                       LocaleManager.Instance[LocaleKeys.InputDialogYes],
                                                                                       LocaleManager.Instance[LocaleKeys.InputDialogNo],
                                                                                       LocaleManager.Instance[LocaleKeys.RyujinxConfirm]);

                List<DirectoryInfo> oldCacheDirectories = new();
                List<FileInfo> newCacheFiles = new();

                if (shaderCacheDir.Exists)
                {
                    oldCacheDirectories.AddRange(shaderCacheDir.EnumerateDirectories("*"));
                    newCacheFiles.AddRange(shaderCacheDir.GetFiles("*.toc"));
                    newCacheFiles.AddRange(shaderCacheDir.GetFiles("*.data"));
                }

                if ((oldCacheDirectories.Count > 0 || newCacheFiles.Count > 0) && result == UserResult.Yes)
                {
                    foreach (DirectoryInfo directory in oldCacheDirectories)
                    {
                        try
                        {
                            directory.Delete(true);
                        }
                        catch (Exception e)
                        {
                            await ContentDialogHelper.CreateErrorDialog(string.Format(LocaleManager.Instance[LocaleKeys.DialogPPTCDeletionErrorMessage], directory.Name, e));
                        }
                    }
                }

                foreach (FileInfo file in newCacheFiles)
                {
                    try
                    {
                        file.Delete();
                    }
                    catch (Exception e)
                    {
                        await ContentDialogHelper.CreateErrorDialog(string.Format(LocaleManager.Instance[LocaleKeys.ShaderCachePurgeError], file.Name, e));
                    }
                }
            }
        }

        public void OpenDeviceSaveDirectory()
        {
            ApplicationData selection = SelectedApplication;
            if (selection != null)
            {
                Task.Run(() =>
                {
                    if (!ulong.TryParse(selection.TitleId, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong titleIdNumber))
                    {
                        async void Action()
                        {
                            await ContentDialogHelper.CreateErrorDialog(LocaleManager.Instance[LocaleKeys.DialogRyujinxErrorMessage], LocaleManager.Instance[LocaleKeys.DialogInvalidTitleIdErrorMessage]);
                        }

                        Dispatcher.UIThread.Post(Action);

                        return;
                    }

                    var saveDataFilter = SaveDataFilter.Make(titleIdNumber, SaveDataType.Device, userId: default, saveDataId: default, index: default);
                    OpenSaveDirectory(in saveDataFilter, selection, titleIdNumber);
                });
            }
        }

        public void OpenBcatSaveDirectory()
        {
            ApplicationData selection = SelectedApplication;
            if (selection != null)
            {
                Task.Run(() =>
                {
                    if (!ulong.TryParse(selection.TitleId, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong titleIdNumber))
                    {
                        async void Action()
                        {
                            await ContentDialogHelper.CreateErrorDialog(LocaleManager.Instance[LocaleKeys.DialogRyujinxErrorMessage], LocaleManager.Instance[LocaleKeys.DialogInvalidTitleIdErrorMessage]);
                        }

                        Dispatcher.UIThread.Post(Action);

                        return;
                    }

                    var saveDataFilter = SaveDataFilter.Make(titleIdNumber, SaveDataType.Bcat, userId: default, saveDataId: default, index: default);
                    OpenSaveDirectory(in saveDataFilter, selection, titleIdNumber);
                });
            }
        }

        public void ToggleFavorite()
        {
            ApplicationData selection = SelectedApplication;
            if (selection != null)
            {
                selection.Favorite = !selection.Favorite;

                ApplicationLibrary.LoadAndSaveMetaData(selection.TitleId, appMetadata =>
                {
                    appMetadata.Favorite = selection.Favorite;
                });

                RefreshView();
            }
        }

        public void OpenUserSaveDirectory()
        {
            ApplicationData selection = SelectedApplication;
            if (selection != null)
            {
                Task.Run(() =>
                {
                    if (!ulong.TryParse(selection.TitleId, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong titleIdNumber))
                    {
                        async void Action()
                        {
                            await ContentDialogHelper.CreateErrorDialog(LocaleManager.Instance[LocaleKeys.DialogRyujinxErrorMessage], LocaleManager.Instance[LocaleKeys.DialogInvalidTitleIdErrorMessage]);
                        }

                        Dispatcher.UIThread.Post(Action);

                        return;
                    }

                    UserId         userId         = new((ulong)AccountManager.LastOpenedUser.UserId.High, (ulong)AccountManager.LastOpenedUser.UserId.Low);
                    SaveDataFilter saveDataFilter = SaveDataFilter.Make(titleIdNumber, saveType: default, userId, saveDataId: default, index: default);
                    OpenSaveDirectory(in saveDataFilter, selection, titleIdNumber);
                });
            }
        }

        public void OpenModsDirectory()
        {
            ApplicationData selection = SelectedApplication;
            if (selection != null)
            {
                string modsBasePath  = VirtualFileSystem.ModLoader.GetModsBasePath();
                string titleModsPath = VirtualFileSystem.ModLoader.GetTitleDir(modsBasePath, selection.TitleId);

                OpenHelper.OpenFolder(titleModsPath);
            }
        }

        public void OpenSdModsDirectory()
        {
            ApplicationData selection = SelectedApplication;

            if (selection != null)
            {
                string sdModsBasePath = VirtualFileSystem.ModLoader.GetSdModsBasePath();
                string titleModsPath  = VirtualFileSystem.ModLoader.GetTitleDir(sdModsBasePath, selection.TitleId);

                OpenHelper.OpenFolder(titleModsPath);
            }
        }

        public async void OpenTitleUpdateManager()
        {
            ApplicationData selection = SelectedApplication;
            if (selection != null)
            {
                if (Avalonia.Application.Current.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                {
                    await new TitleUpdateWindow(VirtualFileSystem, ulong.Parse(selection.TitleId, NumberStyles.HexNumber), selection.TitleName).ShowDialog(desktop.MainWindow);
                }
            }
        }

        public async void OpenDownloadableContentManager()
        {
            ApplicationData selection = SelectedApplication;
            if (selection != null)
            {
                if (Avalonia.Application.Current.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                {
                    await new DownloadableContentManagerWindow(VirtualFileSystem, ulong.Parse(selection.TitleId, NumberStyles.HexNumber), selection.TitleName).ShowDialog(desktop.MainWindow);
                }
            }
        }

        public async void OpenCheatManager()
        {
            ApplicationData selection = SelectedApplication;
            if (selection != null)
            {
                if (Avalonia.Application.Current.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                {
                    await new CheatWindow(VirtualFileSystem, selection.TitleId, selection.TitleName).ShowDialog(desktop.MainWindow);
                }
            }
        }

        public async void LoadApplications()
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Applications.Clear();

                StatusBarVisible         = true;
                StatusBarProgressMaximum = 0;
                StatusBarProgressValue   = 0;

                LocaleManager.Instance.UpdateDynamicValue(LocaleKeys.StatusBarGamesLoaded, 0, 0);
            });

            ReloadGameList?.Invoke();
        }

        public async void OpenFile()
        {
            if (Avalonia.Application.Current.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                OpenFileDialog dialog = new()
                {
                    Title = LocaleManager.Instance[LocaleKeys.OpenFileDialogTitle]
                };

                dialog.Filters.Add(new FileDialogFilter
                {
                    Name = LocaleManager.Instance[LocaleKeys.AllSupportedFormats],
                    Extensions =
                    {
                        "nsp",
                        "pfs0",
                        "xci",
                        "nca",
                        "nro",
                        "nso"
                    }
                });

                dialog.Filters.Add(new FileDialogFilter { Name = "NSP",  Extensions = { "nsp" } });
                dialog.Filters.Add(new FileDialogFilter { Name = "PFS0", Extensions = { "pfs0" } });
                dialog.Filters.Add(new FileDialogFilter { Name = "XCI",  Extensions = { "xci" } });
                dialog.Filters.Add(new FileDialogFilter { Name = "NCA",  Extensions = { "nca" } });
                dialog.Filters.Add(new FileDialogFilter { Name = "NRO",  Extensions = { "nro" } });
                dialog.Filters.Add(new FileDialogFilter { Name = "NSO",  Extensions = { "nso" } });

                string[] files = await dialog.ShowAsync(desktop.MainWindow);

                if (files != null && files.Length > 0)
                {
                    LoadApplication(files[0]);
                }
            }
        }

        public async void OpenFolder()
        {
            if (Avalonia.Application.Current.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                OpenFolderDialog dialog = new()
                {
                    Title = LocaleManager.Instance[LocaleKeys.OpenFolderDialogTitle]
                };

                string folder = await dialog.ShowAsync(desktop.MainWindow);

                if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
                {
                    LoadApplication(folder);
                }
            }
        }

        public async void LoadApplication(string path, bool startFullscreen = false, string titleName = "")
        {
            if (AppHost != null)
            {
                await ContentDialogHelper.CreateInfoDialog(
                    LocaleManager.Instance[LocaleKeys.DialogLoadAppGameAlreadyLoadedMessage],
                    LocaleManager.Instance[LocaleKeys.DialogLoadAppGameAlreadyLoadedSubMessage],
                    LocaleManager.Instance[LocaleKeys.InputDialogOk],
                    "",
                    LocaleManager.Instance[LocaleKeys.RyujinxInfo]);

                return;
            }

#if RELEASE
            await PerformanceCheck();
#endif

            Logger.RestartTime();

            if (SelectedIcon == null)
            {
                SelectedIcon = ApplicationLibrary.GetApplicationIcon(path);
            }

            PrepareLoadScreen();

            RendererControl = new RendererHost(ConfigurationState.Instance.Logger.GraphicsDebugLevel);
            if (ConfigurationState.Instance.Graphics.GraphicsBackend.Value == GraphicsBackend.OpenGl)
            {
                RendererControl.CreateOpenGL();
            }
            else
            {
                RendererControl.CreateVulkan();
            }

            AppHost = new AppHost(
                RendererControl,
                InputManager,
                path,
                VirtualFileSystem,
                ContentManager,
                AccountManager,
                UserChannelPersistence,
                this,
                TopLevel);

            async void Action()
            {
                if (!await AppHost.LoadGuestApplication())
                {
                    AppHost.DisposeContext();
                    AppHost = null;

                    return;
                }

                CanUpdate = false;
                LoadHeading = string.IsNullOrWhiteSpace(titleName) ? string.Format(LocaleManager.Instance[LocaleKeys.LoadingHeading], AppHost.Device.Application.TitleName) : titleName;
                TitleName = string.IsNullOrWhiteSpace(titleName) ? AppHost.Device.Application.TitleName : titleName;

                SwitchToRenderer(startFullscreen);

                _currentEmulatedGamePath = path;

                Thread gameThread = new(InitializeGame) { Name = "GUI.WindowThread" };
                gameThread.Start();
            }

            Dispatcher.UIThread.Post(Action);
        }

        public void SwitchToRenderer(bool startFullscreen)
        {
            Dispatcher.UIThread.Post(() =>
            {
                SwitchToGameControl(startFullscreen);

                SetMainContent(RendererControl);

                RendererControl.Focus();
            });
        }

        public void UpdateGameMetadata(string titleId)
        {
            ApplicationLibrary.LoadAndSaveMetaData(titleId, appMetadata =>
            {
                if (DateTime.TryParse(appMetadata.LastPlayed, out DateTime lastPlayedDateTime))
                {
                    double sessionTimePlayed = DateTime.UtcNow.Subtract(lastPlayedDateTime).TotalSeconds;

                    appMetadata.TimePlayed += Math.Round(sessionTimePlayed, MidpointRounding.AwayFromZero);
                }
            });
        }

        public void RefreshFirmwareStatus()
        {
            SystemVersion version = null;
            try
            {
                version = ContentManager.GetCurrentFirmwareVersion();
            }
            catch (Exception) { }

            bool hasApplet = false;

            if (version != null)
            {
                LocaleManager.Instance.UpdateDynamicValue(LocaleKeys.StatusBarSystemVersion,
                    version.VersionString);

                hasApplet = version.Major > 3;
            }
            else
            {
                LocaleManager.Instance.UpdateDynamicValue(LocaleKeys.StatusBarSystemVersion, "0.0");
            }

            IsAppletMenuActive = hasApplet;
        }

        public void AppHost_AppExit(object sender, EventArgs e)
        {
            if (IsClosing)
            {
                return;
            }

            IsGameRunning = false;

            Dispatcher.UIThread.InvokeAsync(() =>
            {
                ShowMenuAndStatusBar = true;
                ShowContent = true;
                ShowLoadProgress = false;
                IsLoadingIndeterminate = false;
                CanUpdate = true;
                Cursor = Cursor.Default;

                SetMainContent(null);

                AppHost = null;

                HandleRelaunch();
            });

            RendererControl.RendererInitialized -= GlRenderer_Created;
            RendererControl = null;

            SelectedIcon = null;

            Dispatcher.UIThread.InvokeAsync(() =>
            {
                Title = $"Ryujinx {Program.Version}";
            });
        }

        public void ToggleFullscreen()
        {
            if (Environment.TickCount64 - LastFullscreenToggle < HotKeyPressDelayMs)
            {
                return;
            }

            LastFullscreenToggle = Environment.TickCount64;

            if (WindowState == WindowState.FullScreen)
            {
                WindowState = WindowState.Normal;

                if (IsGameRunning)
                {
                    ShowMenuAndStatusBar = true;
                }
            }
            else
            {
                WindowState = WindowState.FullScreen;

                if (IsGameRunning)
                {
                    ShowMenuAndStatusBar = false;
                }
            }

            IsFullScreen = WindowState == WindowState.FullScreen;
        }

        public static void SaveConfig()
        {
            ConfigurationState.Instance.ToFileFormat().SaveConfig(Program.ConfigurationPath);
        }

        public static async Task PerformanceCheck()
        {
            if (ConfigurationState.Instance.Logger.EnableTrace.Value)
            {
                string mainMessage = LocaleManager.Instance[LocaleKeys.DialogPerformanceCheckLoggingEnabledMessage];
                string secondaryMessage = LocaleManager.Instance[LocaleKeys.DialogPerformanceCheckLoggingEnabledConfirmMessage];

                UserResult result = await ContentDialogHelper.CreateConfirmationDialog(
                    mainMessage,
                    secondaryMessage,
                    LocaleManager.Instance[LocaleKeys.InputDialogYes],
                    LocaleManager.Instance[LocaleKeys.InputDialogNo],
                    LocaleManager.Instance[LocaleKeys.RyujinxConfirm]);

                if (result != UserResult.Yes)
                {
                    ConfigurationState.Instance.Logger.EnableTrace.Value = false;

                    SaveConfig();
                }
            }

            if (!string.IsNullOrWhiteSpace(ConfigurationState.Instance.Graphics.ShadersDumpPath.Value))
            {
                string mainMessage = LocaleManager.Instance[LocaleKeys.DialogPerformanceCheckShaderDumpEnabledMessage];
                string secondaryMessage = LocaleManager.Instance[LocaleKeys.DialogPerformanceCheckShaderDumpEnabledConfirmMessage];

                UserResult result = await ContentDialogHelper.CreateConfirmationDialog(
                    mainMessage,
                    secondaryMessage,
                    LocaleManager.Instance[LocaleKeys.InputDialogYes],
                    LocaleManager.Instance[LocaleKeys.InputDialogNo],
                    LocaleManager.Instance[LocaleKeys.RyujinxConfirm]);

                if (result != UserResult.Yes)
                {
                    ConfigurationState.Instance.Graphics.ShadersDumpPath.Value = "";

                    SaveConfig();
                }
            }
        }

#endregion
    }
}
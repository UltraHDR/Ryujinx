using Avalonia;
using Avalonia.Threading;
using Ryujinx.Ava.UI.Helper;
using Ryujinx.Ava.UI.Windows;
using Ryujinx.Common;
using Ryujinx.Common.Configuration;
using Ryujinx.Common.GraphicsDriver;
using Ryujinx.Common.Logging;
using Ryujinx.Common.SystemInfo;
using Ryujinx.Common.SystemInterop;
using Ryujinx.Modules;
using Ryujinx.SDL2.Common;
using Ryujinx.Ui.Common;
using Ryujinx.Ui.Common.Configuration;
using Ryujinx.Ui.Common.Helper;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Tasks;

namespace Ryujinx.Ava
{
    internal partial class Program
    {
        public static double WindowScaleFactor  { get; set; }
        public static double DesktopScaleFactor { get; set; } = 1.0;
        public static string Version            { get; private set; }
        public static string ConfigurationPath  { get; private set; }
        public static bool   PreviewerDetached {  get; private set; }

        [LibraryImport("user32.dll", SetLastError = true)]
        public static partial int MessageBoxA(IntPtr hWnd, [MarshalAs(UnmanagedType.LPStr)] string text, [MarshalAs(UnmanagedType.LPStr)] string caption, uint type);

        private const uint MB_ICONWARNING = 0x30;

        [SupportedOSPlatform("linux")]
        static void RegisterMimeTypes()
        {
            if (ReleaseInformation.IsFlatHubBuild())
            {
                return;
            }

            string mimeDbPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "mime");

            if (!File.Exists(Path.Combine(mimeDbPath, "packages", "Ryujinx.xml")))
            {
                string mimeTypesFile = Path.Combine(ReleaseInformation.GetBaseApplicationDirectory(), "mime", "Ryujinx.xml");
                using Process mimeProcess = new();

                mimeProcess.StartInfo.FileName = "xdg-mime";
                mimeProcess.StartInfo.Arguments = $"install --novendor --mode user {mimeTypesFile}";

                mimeProcess.Start();
                mimeProcess.WaitForExit();

                if (mimeProcess.ExitCode != 0)
                {
                    Logger.Error?.PrintMsg(LogClass.Application, $"Unable to install mime types. Make sure xdg-utils is installed. Process exited with code: {mimeProcess.ExitCode}");
                    return;
                }

                using Process updateMimeProcess = new();

                updateMimeProcess.StartInfo.FileName = "update-mime-database";
                updateMimeProcess.StartInfo.Arguments = mimeDbPath;

                updateMimeProcess.Start();
                updateMimeProcess.WaitForExit();

                if (updateMimeProcess.ExitCode != 0)
                {
                    Logger.Error?.PrintMsg(LogClass.Application, $"Could not update local mime database. Process exited with code: {updateMimeProcess.ExitCode}");
                }
            }
        }

        public static void Main(string[] args)
        {
            Version = ReleaseInformation.GetVersion();

            if (OperatingSystem.IsWindows() && !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17134))
            {
                _ = MessageBoxA(IntPtr.Zero, "You are running an outdated version of Windows.\n\nStarting on June 1st 2022, Ryujinx will only support Windows 10 1803 and newer.\n", $"Ryujinx {Version}", MB_ICONWARNING);
            }

            PreviewerDetached = true;

            Initialize(args);

            LoggerAdapter.Register();

            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }

        public static AppBuilder BuildAvaloniaApp()
        {
            return AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .With(new X11PlatformOptions
                {
                    EnableMultiTouch = true,
                    EnableIme        = true,
                    UseEGL           = false,
                    UseGpu           = true
                })
                .With(new Win32PlatformOptions
                {
                    EnableMultitouch                = true,
                    UseWgl                          = false,
                    AllowEglInitialization          = false,
                    CompositionBackdropCornerRadius = 8.0f,
                })
                .UseSkia();
        }

        private static void Initialize(string[] args)
        {
            // Parse arguments
            CommandLineState.ParseArguments(args);

            // Delete backup files after updating.
            Task.Run(Updater.CleanupUpdate);

            Console.Title = $"Ryujinx Console {Version}";

            // Hook unhandled exception and process exit events.
            AppDomain.CurrentDomain.UnhandledException += (sender, e) => ProcessUnhandledException(e.ExceptionObject as Exception, e.IsTerminating);
            AppDomain.CurrentDomain.ProcessExit        += (sender, e) => Exit();

            // Setup base data directory.
            AppDataManager.Initialize(CommandLineState.BaseDirPathArg);

            // Initialize the configuration.
            ConfigurationState.Initialize();

            // Initialize the logger system.
            LoggerModule.Initialize();

            // Register mime types on linux.
            if (OperatingSystem.IsLinux())
            {
                RegisterMimeTypes();
            }

            // Initialize Discord integration.
            DiscordIntegrationModule.Initialize();

            // Initialize SDL2 driver
            SDL2Driver.MainThreadDispatcher = action => Dispatcher.UIThread.InvokeAsync(action, DispatcherPriority.Input);

            ReloadConfig();

            ForceDpiAware.Windows();

            WindowScaleFactor = ForceDpiAware.GetWindowScaleFactor();

            // Logging system information.
            PrintSystemInfo();

            // Enable OGL multithreading on the driver, when available.
            DriverUtilities.ToggleOGLThreading(ConfigurationState.Instance.Graphics.BackendThreading == BackendThreading.Off);

            // Check if keys exists.
            if (!File.Exists(Path.Combine(AppDataManager.KeysDirPath, "prod.keys")))
            {
                if (!(AppDataManager.Mode == AppDataManager.LaunchMode.UserProfile && File.Exists(Path.Combine(AppDataManager.KeysDirPathUser, "prod.keys"))))
                {
                    MainWindow.ShowKeyErrorOnLoad = true;
                }
            }

            if (CommandLineState.LaunchPathArg != null)
            {
                MainWindow.DeferLoadApplication(CommandLineState.LaunchPathArg, CommandLineState.StartFullscreenArg);
            }
        }

        public static void ReloadConfig()
        {
            string localConfigurationPath   = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config.json");
            string appDataConfigurationPath = Path.Combine(AppDataManager.BaseDirPath, "Config.json");

            // Now load the configuration as the other subsystems are now registered
            if (File.Exists(localConfigurationPath))
            {
                ConfigurationPath = localConfigurationPath;
            }
            else if (File.Exists(appDataConfigurationPath))
            {
                ConfigurationPath = appDataConfigurationPath;
            }

            if (ConfigurationPath == null)
            {
                // No configuration, we load the default values and save it to disk
                ConfigurationPath = appDataConfigurationPath;

                ConfigurationState.Instance.LoadDefault();
                ConfigurationState.Instance.ToFileFormat().SaveConfig(ConfigurationPath);
            }
            else
            {
                if (ConfigurationFileFormat.TryLoad(ConfigurationPath, out ConfigurationFileFormat configurationFileFormat))
                {
                    ConfigurationState.Instance.Load(configurationFileFormat, ConfigurationPath);
                }
                else
                {
                    ConfigurationState.Instance.LoadDefault();

                    Logger.Warning?.PrintMsg(LogClass.Application, $"Failed to load config! Loading the default config instead.\nFailed config location {ConfigurationPath}");
                }
            }

            // Check if graphics backend was overridden
            if (CommandLineState.OverrideGraphicsBackend != null)
            {
                if (CommandLineState.OverrideGraphicsBackend.ToLower() == "opengl")
                {
                    ConfigurationState.Instance.Graphics.GraphicsBackend.Value = GraphicsBackend.OpenGl;
                }
                else if (CommandLineState.OverrideGraphicsBackend.ToLower() == "vulkan")
                {
                    ConfigurationState.Instance.Graphics.GraphicsBackend.Value = GraphicsBackend.Vulkan;
                }
            }

            // Check if docked mode was overriden.
            if (CommandLineState.OverrideDockedMode.HasValue)
            {
                ConfigurationState.Instance.System.EnableDockedMode.Value = CommandLineState.OverrideDockedMode.Value;
            }
        }

        private static void PrintSystemInfo()
        {
            Logger.Notice.Print(LogClass.Application, $"Ryujinx Version: {Version}");
            SystemInfo.Gather().Print();

            Logger.Notice.Print(LogClass.Application, $"Logs Enabled: {(Logger.GetEnabledLevels().Count == 0 ? "<None>" : string.Join(", ", Logger.GetEnabledLevels()))}");

            if (AppDataManager.Mode == AppDataManager.LaunchMode.Custom)
            {
                Logger.Notice.Print(LogClass.Application, $"Launch Mode: Custom Path {AppDataManager.BaseDirPath}");
            }
            else
            {
                Logger.Notice.Print(LogClass.Application, $"Launch Mode: {AppDataManager.Mode}");
            }
        }

        private static void ProcessUnhandledException(Exception ex, bool isTerminating)
        {
            string message = $"Unhandled exception caught: {ex}";

            Logger.Error?.PrintMsg(LogClass.Application, message);

            if (Logger.Error == null)
            {
                Logger.Notice.PrintMsg(LogClass.Application, message);
            }

            if (isTerminating)
            {
                Exit();
            }
        }

        public static void Exit()
        {
            DiscordIntegrationModule.Exit();

            Logger.Shutdown();
        }
    }
}
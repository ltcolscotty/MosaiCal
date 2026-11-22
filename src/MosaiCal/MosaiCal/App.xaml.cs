using Microsoft.UI.Xaml;
using MosaiCal.Services;
using MosaiCal.ViewWindows.WidgetView;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace MosaiCal
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        private const string ControllerMutexName = @"Local\MosaiCal.Controller";
        private const string ControllerWindowTitle = "MosaiCal Widget";
        private Window? _window;
        private Mutex? _controllerMutex;

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            InitializeComponent();
            try
            {
                AppDataPaths.EnsureCreated();
            }
            catch (Exception exception)
            {
                ShowStartupError(
                    IntPtr.Zero,
                    $"MosaiCal cannot start because it could not initialize its required app-data folders."
                    + $"{Environment.NewLine}{Environment.NewLine}{exception.Message}"
                    + $"{Environment.NewLine}{Environment.NewLine}Details:{Environment.NewLine}{exception}",
                    "MosaiCal startup failed",
                    0x10);
                throw;
            }

            UnhandledException += OnUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        }

        [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int ShowStartupError(IntPtr hWnd, string text, string caption, uint type);

        private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            LoggerService.LogException("Unhandled application exception", e.Exception);
        }

        private static void OnDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e)
        {
            string context = $"Unhandled non-UI exception (terminating={e.IsTerminating})";
            if (e.ExceptionObject is Exception exception)
            {
                LoggerService.LogException(context, exception);
            }
            else
            {
                LoggerService.Log($"{context}: {e.ExceptionObject}", LoggerStatusEnum.EXCEPTION);
            }
        }

        private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            LoggerService.LogException("Unobserved task exception", e.Exception);
            e.SetObserved();
        }

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            if (StartupLaunchService.IsStartupLaunch(args.Arguments))
            {
                if (!SettingsRetriever.GetPrimaryViewEnabled())
                {
                    LoggerService.Log("App: Startup launch skipped because the calendar view is disabled.");
                    Exit();
                    return;
                }

                ViewWindowManager.PrimaryViewClosedByUser += OnStartupPrimaryViewClosedByUser;
                ViewWindowManager.OpenConfiguredPrimaryViewOnStartup();
                return;
            }

            if (!TryAcquireControllerMutex())
            {
                bool activated = Platform.Win32Interop.TryActivateWindowByTitle(ControllerWindowTitle);
                LoggerService.Log(
                    $"App: Another controller is already open; activatedExisting={activated}. Exiting this launch.");
                Exit();
                return;
            }

            _window = new WidgetWindow();
            _window.Closed += (_, _) => ReleaseControllerMutex();
            _window.Activate();
            await StartupLaunchService.RequestConsentIfNeededAsync(_window);
        }

        private bool TryAcquireControllerMutex()
        {
            var mutex = new Mutex(initiallyOwned: false, ControllerMutexName);
            bool acquired;
            try
            {
                acquired = mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            if (!acquired)
            {
                mutex.Dispose();
                return false;
            }

            _controllerMutex = mutex;
            return true;
        }

        private void ReleaseControllerMutex()
        {
            if (_controllerMutex is null)
            {
                return;
            }

            _controllerMutex.ReleaseMutex();
            _controllerMutex.Dispose();
            _controllerMutex = null;
        }

        private void OnStartupPrimaryViewClosedByUser()
        {
            ViewWindowManager.PrimaryViewClosedByUser -= OnStartupPrimaryViewClosedByUser;
            SettingsRetriever.SetPrimaryViewEnabled(false);
        }
    }
}

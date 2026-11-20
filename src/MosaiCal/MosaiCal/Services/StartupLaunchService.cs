using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.AppLifecycle;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StartupTask = Windows.ApplicationModel.StartupTask;
using StartupTaskState = Windows.ApplicationModel.StartupTaskState;

namespace MosaiCal.Services;

/// <summary>
/// What the "Start at sign-in" switch should show.
/// </summary>
/// <param name="IsOn">Whether MosaiCal will launch at sign-in.</param>
/// <param name="CanChange">False when Windows, policy, or an unpackaged build owns the setting.</param>
/// <param name="Message">Explanation shown under the switch, or null for none.</param>
internal sealed record StartupStatus(bool IsOn, bool CanChange, string? Message);

/// <summary>
/// Start-at-sign-in, backed by the packaged app's Windows startup task (see Package.appxmanifest).
/// Windows owns the real on/off state, so turning MosaiCal off in Settings › Apps › Startup or Task
/// Manager is respected and reflected back into the app. The startup launch itself exits right away
/// when the calendar view is disabled (see App.OnLaunched).
/// </summary>
internal static class StartupLaunchService
{
    /// Manual override for testing the startup path: MosaiCal.exe --startup
    public const string StartupArgument = "--startup";

    // Must match uap5:StartupTask TaskId in Package.appxmanifest.
    private const string StartupTaskId = "MosaiCalStartup";

    private static readonly SemaphoreSlim syncLock = new(1, 1);

    public static StartupStatus CurrentStatus { get; private set; } = new(false, false, null);

    public static event Action<StartupStatus>? StatusChanged;

    public static bool IsStartupLaunch(string? activationArguments)
    {
        if (string.Equals(activationArguments?.Trim(), StartupArgument, StringComparison.OrdinalIgnoreCase) ||
            Environment.GetCommandLineArgs().Skip(1).Any(argument =>
                string.Equals(argument, StartupArgument, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (!AppDataPaths.IsPackaged)
        {
            return false;
        }

        try
        {
            return AppInstance.GetCurrent().GetActivatedEventArgs().Kind == ExtendedActivationKind.StartupTask;
        }
        catch (Exception exception)
        {
            LoggerService.LogException("StartupLaunchService: Failed to read activation kind.", exception);
            return false;
        }
    }

    public static async Task RequestConsentIfNeededAsync(Window owner)
    {
        try
        {
            if (SettingsRetriever.GetStartupAtLoginConsent() is null && AppDataPaths.IsPackaged)
            {
                if (owner.Content is not FrameworkElement content)
                {
                    throw new InvalidOperationException("The configuration window has no content to host the startup consent dialog.");
                }

                // Activate() returns before the content is in the visual tree, so XamlRoot
                // is still null at that point. Wait for Loaded instead of failing.
                await WaitForLoadedAsync(content);

                if (content.XamlRoot is null)
                {
                    throw new InvalidOperationException("The configuration window is not ready to show the startup consent dialog.");
                }

                ContentDialog dialog = new()
                {
                    Title = "Start MosaiCal at sign-in?",
                    Content = "When enabled, your calendar view opens automatically when you sign in. You can change this anytime in General settings.",
                    PrimaryButtonText = "Allow",
                    CloseButtonText = "Not now",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = content.XamlRoot
                };

                ContentDialogResult result = await dialog.ShowAsync();
                bool consent = result == ContentDialogResult.Primary;
                LoggerService.Log($"StartupLaunchService: User startup consent recorded as {consent}.");
                await SetEnabledAsync(consent);
                return;
            }
        }
        catch (Exception exception)
        {
            LoggerService.LogException("StartupLaunchService: Failed to request startup consent.", exception);
        }

        await SyncWithSettingsAsync();
    }

    /// <summary>Turns start-at-sign-in on or off at the user's request (the switch or the first-run dialog).</summary>
    public static async Task SetEnabledAsync(bool enabled)
    {
        SettingsRetriever.SetStartupAtLoginConsent(enabled);

        if (AppDataPaths.IsPackaged)
        {
            await syncLock.WaitAsync();
            try
            {
                StartupTask task = await StartupTask.GetAsync(StartupTaskId);
                if (enabled)
                {
                    StartupTaskState state = await task.RequestEnableAsync();
                    LoggerService.Log($"StartupLaunchService: Requested startup task enable; state is now {state}.");
                }
                else
                {
                    task.Disable();
                    LoggerService.Log("StartupLaunchService: Startup task disabled.");
                }
            }
            catch (Exception exception)
            {
                LoggerService.LogException("StartupLaunchService: Failed to change the startup task.", exception);
            }
            finally
            {
                syncLock.Release();
            }
        }

        await SyncWithSettingsAsync();
    }

    /// <summary>
    /// Reconciles the saved preference with Windows' startup task and publishes the switch state.
    /// Call when the window opens or the calendar view setting changes.
    /// </summary>
    public static async Task SyncWithSettingsAsync()
    {
        await syncLock.WaitAsync();
        StartupStatus status;
        try
        {
            status = AppDataPaths.IsPackaged
                ? await ReconcileWithStartupTaskAsync()
                : new StartupStatus(false, false, "Start at sign-in is only available in the installed version of MosaiCal.");
        }
        catch (Exception exception)
        {
            LoggerService.LogException("StartupLaunchService: Failed to synchronize the startup task.", exception);
            status = new StartupStatus(false, false, "Couldn't read the Windows startup setting.");
        }
        finally
        {
            syncLock.Release();
        }

        CurrentStatus = status;
        StatusChanged?.Invoke(status);
    }

    private static async Task<StartupStatus> ReconcileWithStartupTaskAsync()
    {
        StartupTask task = await StartupTask.GetAsync(StartupTaskId);
        bool? consent = SettingsRetriever.GetStartupAtLoginConsent();
        StartupTaskState state = task.State;

        if (state == StartupTaskState.Disabled && consent == true)
        {
            // Preference is on but the task isn't, e.g. the package was reinstalled and the task reset.
            state = await task.RequestEnableAsync();
            LoggerService.Log($"StartupLaunchService: Enabled startup task to match saved preference; state is now {state}.");
        }

        // Mirror changes the user made outside the app (Windows Settings or Task Manager).
        bool windowsSaysOn = state is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
        if (state != StartupTaskState.Disabled && consent != windowsSaysOn)
        {
            SettingsRetriever.SetStartupAtLoginConsent(windowsSaysOn);
            LoggerService.Log($"StartupLaunchService: Adopted startup state {state} from Windows.");
        }

        string? viewNote = windowsSaysOn && !SettingsRetriever.GetPrimaryViewEnabled()
            ? "Takes effect once the calendar view is turned on."
            : null;

        return state switch
        {
            StartupTaskState.Enabled => new StartupStatus(true, true, viewNote),
            StartupTaskState.Disabled => new StartupStatus(false, true, null),
            StartupTaskState.DisabledByUser => new StartupStatus(false, false,
                "Turned off in Windows Settings › Apps › Startup. Turn MosaiCal back on there to use this."),
            StartupTaskState.DisabledByPolicy => new StartupStatus(false, false, "Turned off by your organization."),
            StartupTaskState.EnabledByPolicy => new StartupStatus(true, false, viewNote ?? "Turned on by your organization."),
            _ => new StartupStatus(windowsSaysOn, false, null)
        };
    }

    private static Task WaitForLoadedAsync(FrameworkElement element)
    {
        if (element.IsLoaded && element.XamlRoot is not null)
        {
            return Task.CompletedTask;
        }

        TaskCompletionSource loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnLoaded(object sender, RoutedEventArgs e)
        {
            element.Loaded -= OnLoaded;
            loaded.TrySetResult();
        }

        element.Loaded += OnLoaded;
        return loaded.Task;
    }
}

using MessagePack;
using MosaiCal.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MosaiCal.Services;

internal static class SettingsRetriever
{
    private static ConfigOptions options;
    private static string ETCSettingsFile;
    private static string dataFile;
    private static string imageDataFolder;
    private static Dictionary<string, EventTypeConfig> sourceConfigs = new();
    private static bool initializedAsyncStatus = false;
    private static readonly SemaphoreSlim sourceConfigLoadLock = new(1, 1);
    private static FileSystemWatcher? sourceConfigWatcher;
    private static Timer? sourceConfigReloadTimer;
    private static List<string> activeSources = [];
    private static readonly string settingsFile;
    private static readonly object optionsLock = new();

    public static event Action<int>? WindowTransparencyChanged;

    static SettingsRetriever() 
    {
        string appDataLocalFolder = AppDataPaths.LocalFolderPath;
        settingsFile = Path.Combine(appDataLocalFolder, "Config.json");
        imageDataFolder = Path.Combine(appDataLocalFolder, "SourceImages");
        dataFile = Path.Combine(appDataLocalFolder, "MainData.msgpack");
        ETCSettingsFile = Path.Combine(appDataLocalFolder, "ETCSettings.msgpack");
        Directory.CreateDirectory(appDataLocalFolder);
        Directory.CreateDirectory(imageDataFolder);
        sourceConfigReloadTimer = new Timer(_ => _ = ReloadSourceConfigsFromDiskAsync(), null, Timeout.Infinite, Timeout.Infinite);
        sourceConfigWatcher = new FileSystemWatcher(appDataLocalFolder, Path.GetFileName(ETCSettingsFile))
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
        };
        sourceConfigWatcher.Changed += (_, _) => QueueSourceConfigReload();
        sourceConfigWatcher.Created += (_, _) => QueueSourceConfigReload();
        sourceConfigWatcher.Renamed += (_, _) => QueueSourceConfigReload();
        sourceConfigWatcher.EnableRaisingEvents = true;
        LoggerService.Log($"SettingsRetriever: Watching source customization file '{ETCSettingsFile}'.");

        using (FileStream settingsLock = AcquireSettingsFileLock())
        {
            if (TryReadOptionsFromDisk(out ConfigOptions? diskOptions))
            {
                options = diskOptions;
            }
            else
            {
                options = new ConfigOptions();
                if (!File.Exists(settingsFile))
                {
                    WriteOptionsToDisk(options);
                }
            }
        }
    }

    public static BackgroundSettingEnum GetBackgroundSetting() { return options.BackgroundSetting; }

    public static BackgroundTypeEnum GetBackgroundType() { return options.BackgroundType; }

    public static int GetWindowTransparency() { return Math.Clamp(options.BackgroundTransparency, 0, 100); }

    public static void SetWindowTransparency(int transparencyPercentage)
    {
        UpdateOptions(o => o.BackgroundTransparency = Math.Clamp(transparencyPercentage, 0, 100));
        WindowTransparencyChanged?.Invoke(options.BackgroundTransparency);
    }

    public static async Task InitializeAsync(bool forceRefresh = false, bool notifyViews = true)
    {
        if (!forceRefresh && initializedAsyncStatus) { return; }

        await sourceConfigLoadLock.WaitAsync();
        bool loaded = false;
        try
        {
            if (!forceRefresh && initializedAsyncStatus) { return; }

            sourceConfigs = await LoadEventTypeConfigs(ETCSettingsFile);
            foreach (string sourceKey in sourceConfigs
                .Where(source => !source.Value.Enabled)
                .Select(source => source.Key)
                .ToList())
            {
                sourceConfigs.Remove(sourceKey);
            }

            initializedAsyncStatus = true;
            loaded = true;
            LoggerService.Log(
                $"SettingsRetriever: Loaded {sourceConfigs.Count} enabled source customizations from '{ETCSettingsFile}' (forceRefresh={forceRefresh}).");
        }
        finally
        {
            sourceConfigLoadLock.Release();
        }

        if (loaded && notifyViews)
        {
            EventAggService.NotifyViewsChanged();
        }
    }

    public static Dictionary<string, EventTypeConfig> GetSourceConfigs() { 
        _ = InitializeAsync();
        return sourceConfigs; 
    }

    private static void QueueSourceConfigReload()
    {
        LoggerService.Log("SettingsRetriever: Source customization file changed; debounce reload queued.");
        sourceConfigReloadTimer?.Change(150, Timeout.Infinite);
    }

    private static async Task ReloadSourceConfigsFromDiskAsync()
    {
        try
        {
            await InitializeAsync(forceRefresh: true);
        }
        catch (Exception exception)
        {
            LoggerService.LogException("SettingsRetriever: Failed to reload source customizations.", exception);
        }
    }

    public static bool SetCanvasICSLink(string newLink)
    {
        // NOTE: may need to change to only HTTPS? - Consider later
        if  (!(Uri.TryCreate(newLink, UriKind.Absolute, out Uri? uriResult)
            && (uriResult.Scheme == Uri.UriSchemeHttp 
                || uriResult.Scheme == Uri.UriSchemeHttps))
            )
        {
            LoggerService.Log($"Invalid url {newLink}", LoggerStatusEnum.WARNING);
            return false;
        }
        UpdateOptions(o => o.CanvasICSLink = newLink);
        return true;
    }

    public static DateTimeOffset getLastUpdateTime() { return options.lastUpdateTime; }
    public static void setLastUpdateTime(DateTimeOffset newTime)
    {
        UpdateOptions(o => o.lastUpdateTime = newTime);
    }

    public static string GetCanvasICSLink() { return options.CanvasICSLink; }
    public static bool GetCanvasEnabled() { return options.CanvasEnabled; }
    public static void SetCanvasEnabled(bool enabled)
    {
        UpdateOptions(o => o.CanvasEnabled = enabled);
    }
    public static bool GetPinnedViewEnabled() { return options.PinnedViewEnabled; }
    public static void SetPinnedViewEnabled(bool enabled)
    {
        UpdateOptions(o => o.PinnedViewEnabled = enabled);
    }
    public static bool GetPrimaryViewEnabled() { return options.PrimaryViewEnabled; }
    public static void SetPrimaryViewEnabled(bool enabled)
    {
        UpdateOptions(o => o.PrimaryViewEnabled = enabled);
    }
    public static bool? GetStartupAtLoginConsent() { return options.StartupAtLoginConsent; }
    public static void SetStartupAtLoginConsent(bool consent)
    {
        UpdateOptions(o => o.StartupAtLoginConsent = consent);
    }
    public static PrimaryViewEnum GetPrimaryView() { return options.PrimaryView; }
    public static void SetPrimaryView(PrimaryViewEnum primaryView)
    {
        UpdateOptions(o => o.PrimaryView = primaryView);
    }
    public static WindowPosition? GetWindowPosition(bool pinned)
    {
        return pinned ? options.PinnedViewPosition : options.PrimaryViewPosition;
    }
    public static void SetWindowPosition(bool pinned, int x, int y)
    {
        WindowPosition position = new() { X = x, Y = y };
        UpdateOptions(o =>
        {
            if (pinned)
            {
                o.PinnedViewPosition = position;
            }
            else
            {
                o.PrimaryViewPosition = position;
            }
        });
    }
    public static WindowSize? GetWindowSize(bool pinned)
    {
        return pinned ? options.PinnedViewSize : options.PrimaryViewSize;
    }
    public static void SetWindowSize(bool pinned, int width, int height)
    {
        WindowSize size = new() { Width = width, Height = height };
        UpdateOptions(o =>
        {
            if (pinned)
            {
                o.PinnedViewSize = size;
            }
            else
            {
                o.PrimaryViewSize = size;
            }
        });
    }
    public static void SetGoogleEnabled(bool enabled)
    {
        UpdateOptions(o => o.GoogleEnabled = enabled);
    }
    public static bool GetGoogleEnabled() { return options.GoogleEnabled; }

    public static void SetBackgroundType(BackgroundTypeEnum backgroundType)
    {
        UpdateOptions(o => o.BackgroundType = backgroundType);
    }
    public static GoogleCalOptions GetGoogleCalOptions()
    {
        return new GoogleCalOptions
        {
            ClientId = options.GoogleClientId,
            ClientSecret = options.GoogleClientSecret,
            TokenPath = Path.Combine(Path.GetDirectoryName(settingsFile)!, "GoogleToken", "token.json"),
            DefaultColor = options.BackgroundColor,
            MaximumEventsPerRefresh = options.GoogleMaxEventsPerRefresh
        };
    }

    public static void SetGoogleCredentials(string clientId, string clientSecret)
    {
        UpdateOptions(o =>
        {
            o.GoogleClientId = clientId.Trim();
            o.GoogleClientSecret = clientSecret.Trim();
        });
    }

    public static void ReloadOptionsFromDisk()
    {
        lock (optionsLock)
        {
            using FileStream settingsLock = AcquireSettingsFileLock();
            if (TryReadOptionsFromDisk(out ConfigOptions? diskOptions))
            {
                options = diskOptions;
            }
        }
    }

    private static void UpdateOptions(Action<ConfigOptions> applyChange)
    {
        lock (optionsLock)
        {
            using FileStream settingsLock = AcquireSettingsFileLock();
            if (TryReadOptionsFromDisk(out ConfigOptions? diskOptions))
            {
                options = diskOptions;
            }

            applyChange(options);
            WriteOptionsToDisk(options);
        }
    }

    private static bool TryReadOptionsFromDisk([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ConfigOptions? diskOptions)
    {
        diskOptions = null;
        if (!File.Exists(settingsFile))
        {
            return false;
        }

        try
        {
            diskOptions = JsonSerializer.Deserialize<ConfigOptions>(File.ReadAllText(settingsFile));
            diskOptions?.Normalize();
            return diskOptions != null;
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            LoggerService.LogException("SettingsRetriever: Failed to read settings from disk; keeping in-memory settings.", exception);
            return false;
        }
    }

    private static void WriteOptionsToDisk(ConfigOptions value)
    {
        string temporaryPath = $"{settingsFile}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(value, new JsonSerializerOptions
            {
                WriteIndented = true
            }));
            File.Move(temporaryPath, settingsFile, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static FileStream AcquireSettingsFileLock()
    {
        string lockPath = settingsFile + ".lock";
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException exception) when ((exception.HResult & 0xFFFF) is 32 or 33 && attempt < 200)
            {
                Thread.Sleep(25);
            }
        }
    }

    public static string GetSchoolName() { return options.School ?? string.Empty; }

    public static void SetSchoolName(string school)
    {
        UpdateOptions(o => o.School = school?.Trim() ?? string.Empty);
    }
    public static string GetMainDataPath() { return dataFile; }
    public static bool GetInitializedStatus() { return initializedAsyncStatus; }
    public static int GetTrackedDays() { return options.TrackedDays; }
    public static int GetUpdateDurationMins() { return options.updateDurationMins; }
    public static string GetImageDataFolder() { return SourceImageService.Instance.ManagedImageDirectory; }

    private static async Task<Dictionary<string, EventTypeConfig>> LoadEventTypeConfigs(string inputPath)
    {
        byte[] bytes;
        if (!File.Exists(inputPath))
        {
            Dictionary<string, EventTypeConfig> configDict = [];
            bytes = MessagePackSerializer.Serialize(configDict);
            await File.WriteAllBytesAsync(inputPath, bytes);
        }
        else
        {
            bytes = await File.ReadAllBytesAsync(inputPath);
        }

        return MessagePackSerializer.Deserialize<Dictionary<string, EventTypeConfig>>(bytes);
    }

    public static List<string> GetActiveLongSources() { return activeSources; }

    public static async Task<Dictionary<string, bool>> GetActiveSources(GoogleCalService googleCalService)
    {
        bool googleActive = await googleCalService.IsAccountActiveAsync();
        return new Dictionary<string, bool>
        {
            { "canvas", !string.IsNullOrEmpty(options.CanvasICSLink) && options.CanvasEnabled },
            { "google", googleActive && options.GoogleEnabled }
        };
    }
}

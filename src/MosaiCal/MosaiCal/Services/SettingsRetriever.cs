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

    private static FileSystemWatcher? optionsWatcher;
    private static Timer? optionsReloadTimer;

    public static event Action<int>? WindowTransparencyChanged;
    public static event Action? WidgetBackgroundChanged;

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

        optionsReloadTimer = new Timer(_ => ReloadAppearanceFromDisk(), null, Timeout.Infinite, Timeout.Infinite);
        optionsWatcher = new FileSystemWatcher(appDataLocalFolder, Path.GetFileName(settingsFile))
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
        };
        optionsWatcher.Changed += (_, _) => optionsReloadTimer.Change(150, Timeout.Infinite);
        optionsWatcher.Created += (_, _) => optionsReloadTimer.Change(150, Timeout.Infinite);
        optionsWatcher.Renamed += (_, _) => optionsReloadTimer.Change(150, Timeout.Infinite);
        optionsWatcher.EnableRaisingEvents = true;
    }

    private static void ReloadAppearanceFromDisk()
    {
        try
        {
            BackgroundTypeEnum oldType = options.BackgroundType;
            string oldColor = options.WidgetBackgroundColor;
            int oldTransparency = GetWindowTransparency();

            ReloadOptionsFromDisk();

            if (options.BackgroundType != oldType ||
                !string.Equals(options.WidgetBackgroundColor, oldColor, StringComparison.OrdinalIgnoreCase))
            {
                WidgetBackgroundChanged?.Invoke();
            }

            if (GetWindowTransparency() != oldTransparency)
            {
                WindowTransparencyChanged?.Invoke(GetWindowTransparency());
            }
        }
        catch (Exception exception)
        {
            LoggerService.LogException("SettingsRetriever: Failed to reload appearance settings from disk.", exception);
        }
    }

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

    /// <summary>
    /// Saves a solid background color for one event group. Reads the latest file first so
    /// entries this process filtered out or another process wrote are kept
    /// </summary>
    public static async Task SetSourceBackgroundColorAsync(CalendarSourceIdentity source, string hexColor)
    {
        if (source.IsEmpty)
        {
            return;
        }

        await sourceConfigLoadLock.WaitAsync();
        try
        {
            Dictionary<string, EventTypeConfig> latest = await LoadEventTypeConfigs(ETCSettingsFile);
            string key = latest.Keys.FirstOrDefault(k => k.Equals(source.Key, StringComparison.OrdinalIgnoreCase))
                ?? latest.FirstOrDefault(pair => pair.Value.Source.Equals(source.SourceId, StringComparison.OrdinalIgnoreCase)).Key
                ?? source.Key;

            if (!latest.TryGetValue(key, out EventTypeConfig? config))
            {
                config = new EventTypeConfig { Source = source.SourceId };
                latest[key] = config;
            }

            config.BkgColor = hexColor;

            string temporaryPath = $"{ETCSettingsFile}.{Guid.NewGuid():N}.tmp";
            try
            {
                await File.WriteAllBytesAsync(temporaryPath, MessagePackSerializer.Serialize(latest));
                File.Move(temporaryPath, ETCSettingsFile, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }

            sourceConfigs = latest
                .Where(pair => pair.Value.Enabled)
                .ToDictionary(pair => pair.Key, pair => pair.Value);
            initializedAsyncStatus = true;
        }
        finally
        {
            sourceConfigLoadLock.Release();
        }

        LoggerService.Log($"SettingsRetriever: Set background color for '{source.Key}' to {hexColor}.");
        EventAggService.NotifyViewsChanged();
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
    public static WindowPosition? GetWindowPosition() { return options.PrimaryViewPosition; }
    public static void SetWindowPosition(int x, int y)
    {
        UpdateOptions(o => o.PrimaryViewPosition = new WindowPosition { X = x, Y = y });
    }
    public static WindowSize? GetWindowSize() { return options.PrimaryViewSize; }
    public static void SetWindowSize(int width, int height)
    {
        UpdateOptions(o => o.PrimaryViewSize = new WindowSize { Width = width, Height = height });
    }
    public static void SetGoogleEnabled(bool enabled)
    {
        UpdateOptions(o => o.GoogleEnabled = enabled);
    }
    public static bool GetGoogleEnabled() { return options.GoogleEnabled; }

    public static void SetBackgroundType(BackgroundTypeEnum backgroundType)
    {
        UpdateOptions(o => o.BackgroundType = backgroundType);
        WidgetBackgroundChanged?.Invoke();
    }

    public static string GetWidgetBackgroundColor() { return options.WidgetBackgroundColor; }

    public static void SetWidgetBackgroundColor(string hexColor)
    {
        UpdateOptions(o => o.WidgetBackgroundColor = hexColor);
        WidgetBackgroundChanged?.Invoke();
    }

    public static BackgroundTypeEnum GetSettingsBackgroundType() { return options.SettingsBackgroundType; }

    public static void SetSettingsBackgroundType(BackgroundTypeEnum backgroundType)
    {
        UpdateOptions(o => o.SettingsBackgroundType = backgroundType);
    }

    public static string GetSettingsBackgroundColor() { return options.SettingsBackgroundColor; }

    public static void SetSettingsBackgroundColor(string hexColor)
    {
        UpdateOptions(o => o.SettingsBackgroundColor = hexColor);
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
    public static int GetTrackedDays() { return options.TrackedDays; }
    public static int GetUpdateDurationMins() { return options.updateDurationMins; }

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

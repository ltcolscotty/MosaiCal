using MosaiCal.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace MosaiCal.Services;

/// <summary>
/// Tracks which event groups are hidden from the calendar views. Hidden groups are stored 
/// by CalendarSourceIdentity.Key in a shared file that every MosaiCal process watches, so 
/// toggling a group in the settings window updates views owned by other processes too.
/// </summary>
internal static class SourceVisibilityService
{
    private const string HiddenSourcesFileName = "HiddenSources.json";
    private static readonly object SyncLock = new();
    private static readonly string hiddenSourcesPath = Path.Combine(AppDataPaths.LocalFolderPath, HiddenSourcesFileName);
    private static HashSet<string> hiddenKeys = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Timer reloadTimer = new(_ => ReloadFromDisk(), null, Timeout.Infinite, Timeout.Infinite);
    private static readonly FileSystemWatcher? watcher;

    static SourceVisibilityService()
    {
        Directory.CreateDirectory(AppDataPaths.LocalFolderPath);
        hiddenKeys = LoadFromDisk();

        watcher = new FileSystemWatcher(AppDataPaths.LocalFolderPath, HiddenSourcesFileName)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
        };
        watcher.Changed += (_, _) => QueueReload();
        watcher.Created += (_, _) => QueueReload();
        watcher.Deleted += (_, _) => QueueReload();
        watcher.Renamed += (_, _) => QueueReload();
        watcher.EnableRaisingEvents = true;
    }

    public static bool IsVisible(CalendarSourceIdentity source)
    {
        if (source.IsEmpty)
        {
            return true;
        }

        lock (SyncLock)
        {
            return !hiddenKeys.Contains(source.Key);
        }
    }

    public static void SetVisible(CalendarSourceIdentity source, bool visible)
    {
        if (source.IsEmpty)
        {
            return;
        }

        lock (SyncLock)
        {
            HashSet<string> latest = LoadFromDisk();
            bool changed = visible ? latest.Remove(source.Key) : latest.Add(source.Key);
            hiddenKeys = latest;
            if (!changed)
            {
                return;
            }

            SaveToDisk(latest);
        }

        LoggerService.Log(
            $"SourceVisibilityService: Event group '{source.Key}' is now {(visible ? "shown" : "hidden")}.",
            LoggerStatusEnum.INFO);
        EventAggService.NotifyViewsChanged();
    }

    private static void QueueReload() => reloadTimer.Change(150, Timeout.Infinite);

    private static void ReloadFromDisk()
    {
        HashSet<string> latest = LoadFromDisk();
        bool changed;
        lock (SyncLock)
        {
            changed = !latest.SetEquals(hiddenKeys);
            hiddenKeys = latest;
        }

        if (changed)
        {
            EventAggService.NotifyViewsChanged();
        }
    }

    private static HashSet<string> LoadFromDisk()
    {
        try
        {
            if (!File.Exists(hiddenSourcesPath))
            {
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            string[]? keys = JsonSerializer.Deserialize<string[]>(File.ReadAllText(hiddenSourcesPath));
            return new HashSet<string>(
                keys?.Where(key => !string.IsNullOrWhiteSpace(key)) ?? Enumerable.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            LoggerService.LogException("SourceVisibilityService: Failed to read hidden event groups; keeping current state.", exception);
            lock (SyncLock)
            {
                return new HashSet<string>(hiddenKeys, StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    private static void SaveToDisk(HashSet<string> keys)
    {
        string temporaryPath = $"{hiddenSourcesPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(keys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase).ToArray(),
                    new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporaryPath, hiddenSourcesPath, overwrite: true);
        }
        catch (Exception exception)
        {
            LoggerService.LogException("SourceVisibilityService: Failed to save hidden event groups.", exception);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}

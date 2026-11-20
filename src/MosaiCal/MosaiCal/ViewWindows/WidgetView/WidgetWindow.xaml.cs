using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using MosaiCal.Models;
using MosaiCal.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace MosaiCal.ViewWindows.WidgetView;

// <summary>
// An empty window that can be used on its own or navigated to within a Frame.
// </summary>
public sealed partial class WidgetWindow : Window
{
    public ObservableCollection<SourceCustomizationCard> GoogleSourceCards { get; } = new();
    public ObservableCollection<SourceCustomizationCard> CanvasSourceCards { get; } = new();
    private int _customizationCardsRefreshGeneration;
    private SourceCustomizationCard? _pendingDarknessCard;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _darknessSaveTimer;

    private readonly RefreshSchedService _refreshSchedService;
    private readonly IDisposable _shutdownHandler;
    private bool _isUpdatingTransparencyControls;
    private bool _isUpdatingSourceControls;
    private bool _isUpdatingPrimaryViewControl;
    private bool _isUpdatingStartAtSignInControl;
    private bool _hasConfiguredSource;

    public WidgetWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "mosaical-icon.ico"));
        SettingsNav.SelectedItem = SettingsNav.MenuItems[0];
        GoogleSourceCardsList.ItemsSource = GoogleSourceCards;
        CanvasSourceCardsList.ItemsSource = CanvasSourceCards;
        EventAggService.EventsChanged += OnEventsChanged;
        _ = SettingsRetriever.InitializeAsync();
        _refreshSchedService = new RefreshSchedService(
            TimeSpan.FromMinutes(SettingsRetriever.GetUpdateDurationMins()));
        _shutdownHandler = MosaiCal.Platform.Win32Interop.RegisterShutdownHandler(
            WindowNative.GetWindowHandle(this),
            HandleShutdown);
        _isUpdatingTransparencyControls = true;
        WindowConfiguration.Configure(this, isWidgetViewActive: true);
        ViewWindowManager.SetPrimaryViewManagerActive(true);
        Closed += (_, _) =>
        {
            LoggerService.Log("WidgetWindow: Closed event received; publishing manager inactive state.");
            SavePendingImageDarkness(); // don't lose a slider change made just before closing
            ViewWindowManager.SetPrimaryViewManagerActive(false);
            ViewWindowManager.PrimaryViewClosedByUser -= OnPrimaryViewClosedByUser;
            StartupLaunchService.StatusChanged -= OnStartupStatusChanged;
            EventAggService.EventsChanged -= OnEventsChanged;
            _shutdownHandler.Dispose();
            _ = _refreshSchedService.DisposeAsync();
        };
        Activated += (_, args) =>
        {
            if (args.WindowActivationState != WindowActivationState.Deactivated)
            {
                ViewWindowManager.SetPrimaryViewManagerActive(true);
            }
        };

        _ = _refreshSchedService.RunAsync();

        BkgStyleRadioSettings.SelectedIndex = SettingsRetriever.GetBackgroundType() switch
        {
            BackgroundTypeEnum.Solid => 0,
            BackgroundTypeEnum.Mica => 1,
            BackgroundTypeEnum.Acrylic => 2,
            _ => 0
        };

        CanvasCalLinkInput.Text = SettingsRetriever.GetCanvasICSLink();
        UpdateCanvasConnectButtonText();
        ShowSavedSchool();
        _isUpdatingSourceControls = true;
        EnableGoogle.IsOn = SettingsRetriever.GetGoogleEnabled();
        EnableCanvas.IsOn = SettingsRetriever.GetCanvasEnabled();
        _isUpdatingSourceControls = false;
        _isUpdatingPrimaryViewControl = true;
        EnablePrimaryView.IsOn = SettingsRetriever.GetPrimaryViewEnabled();
        EnablePinnedView.IsOn = SettingsRetriever.GetPinnedViewEnabled();
        EnablePrimaryView.IsEnabled = false;
        EnablePinnedView.IsEnabled = false;
        PrimaryViewSelector.SelectedIndex = SettingsRetriever.GetPrimaryView() switch
        {
            PrimaryViewEnum.Todo => 0,
            PrimaryViewEnum.Day => 1,
            PrimaryViewEnum.Week => 2,
            _ => 0
        };
        _isUpdatingPrimaryViewControl = false;
        int transparencyPercentage = SettingsRetriever.GetWindowTransparency();
        WindowTransparencyInput.Value = transparencyPercentage;
        _isUpdatingTransparencyControls = false;
        ViewWindowManager.PrimaryViewClosedByUser += OnPrimaryViewClosedByUser;
        StartupLaunchService.StatusChanged += OnStartupStatusChanged;
        ApplyStartupStatus(StartupLaunchService.CurrentStatus);
        ApplyBackgroundType();
        _ = UpdateConnectionStatusAsync();
        _ = RefreshCustomizationCardsAsync();
        _ = UpdateViewSourceAvailabilityAsync();
    }

    private void SettingsNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item)
        {
            return;
        }

        string page = item.Tag as string ?? "General";
        GeneralPage.Visibility = page == "General" ? Visibility.Visible : Visibility.Collapsed;
        GooglePage.Visibility = page == "Google" ? Visibility.Visible : Visibility.Collapsed;
        CanvasPage.Visibility = page == "Canvas" ? Visibility.Visible : Visibility.Collapsed;
        sender.Header = item.Content;
    }

    private async Task UpdateViewSourceAvailabilityAsync()
    {
        bool googleConfigured = false;
        try
        {
            googleConfigured = await EventAggService.GetGoogleCalService().IsAccountActiveAsync();
        }
        catch (Exception exception)
        {
            LoggerService.LogException("WidgetWindow: Failed to check Google Calendar configuration.", exception);
        }

        bool canvasConfigured = IsCanvasCalendarConfigured(SettingsRetriever.GetCanvasICSLink());
        _hasConfiguredSource = googleConfigured || canvasConfigured;
        EnablePrimaryView.IsEnabled = _hasConfiguredSource;
        EnablePinnedView.IsEnabled = _hasConfiguredSource;
        if (!_hasConfiguredSource)
        {
            ViewSourceRequirementMessage.Text =
                "Configure a source before enabling views: sign in to Google Calendar or add a Canvas calendar link.";
        }

        ViewSourceRequirementMessage.Visibility = _hasConfiguredSource
            ? Visibility.Collapsed
            : Visibility.Visible;

        if (!_hasConfiguredSource)
        {
            if (SettingsRetriever.GetPrimaryViewEnabled())
            {
                SettingsRetriever.SetPrimaryViewEnabled(false);
            }

            if (SettingsRetriever.GetPinnedViewEnabled())
            {
                SettingsRetriever.SetPinnedViewEnabled(false);
            }

            _isUpdatingPrimaryViewControl = true;
            EnablePrimaryView.IsOn = false;
            EnablePinnedView.IsOn = false;
            _isUpdatingPrimaryViewControl = false;
        }

        _ = StartupLaunchService.SyncWithSettingsAsync();
        ViewWindowManager.SyncWithSettings();
    }

    private static bool IsCanvasCalendarConfigured(string? calendarUrl)
    {
        return Uri.TryCreate(calendarUrl, UriKind.Absolute, out Uri? uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    internal void HandleShutdown()
    {
        LoggerService.Log("WidgetWindow: Windows shutdown confirmed; canceling in-progress refreshes.");
        EventAggService.RequestShutdown();
        _ = _refreshSchedService.DisposeAsync();
    }

    private async Task RefreshCustomizationCardsAsync()
    {
        int generation = ++_customizationCardsRefreshGeneration;

        List<SourceCustomizationCard> googleCards = await BuildSourceCardsAsync("Google");
        List<SourceCustomizationCard> canvasCards = await BuildSourceCardsAsync("Canvas");

        if (generation != _customizationCardsRefreshGeneration)
        {
            return;
        }

        GoogleSourceCards.Clear();
        foreach (var sourceCard in googleCards)
        {
            GoogleSourceCards.Add(sourceCard);
        }

        CanvasSourceCards.Clear();
        foreach (var sourceCard in canvasCards)
        {
            CanvasSourceCards.Add(sourceCard);
        }
    }

    private void OnEventsChanged(object? sender, EventArgs e)
    {
        bool enqueued = DispatcherQueue.TryEnqueue(() => _ = RefreshCustomizationCardsAfterEventsChangedAsync());
        LoggerService.Log($"WidgetWindow: Calendar data update received; customization-card refreshEnqueued={enqueued}.",
            enqueued ? LoggerStatusEnum.INFO : LoggerStatusEnum.WARNING);
    }

    private async Task RefreshCustomizationCardsAfterEventsChangedAsync()
    {
        try
        {
            await RefreshCustomizationCardsAsync();
        }
        catch (Exception exception)
        {
            LoggerService.LogException("WidgetWindow: Failed to refresh source customization cards after calendar data changed.", exception);
        }
    }

    private async Task<List<SourceCustomizationCard>> BuildSourceCardsAsync(string provider)
    {
        var cards = new List<SourceCustomizationCard>();
        var knownAssociations = SourceImageService.Instance.GetAllAssociations();
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (provider == GoogleCalService.ProviderName)
        {
            try
            {
                foreach (GoogleCalendarSource calendar in await EventAggService.GetGoogleCalService().GetCalendarSourcesAsync())
                {
                    CalendarSourceIdentity identity = CalendarSourceIdentity.CreateGoogle(calendar.AccountEmail, calendar.CalendarId);
                    if (!seenKeys.Add(identity.Key))
                    {
                        continue;
                    }

                    cards.Add(CreateSourceCustomizationCard(
                        identity,
                        $"{calendar.AccountEmail} - {calendar.DisplayName}"));
                }
            }
            catch (Exception exception)
            {
                LoggerService.LogException("WidgetWindow: Failed to load Google calendar sources for customization cards.", exception);
            }
        }

        if (File.Exists(SettingsRetriever.GetMainDataPath()))
        {
            Dictionary<IDHelper.EventID, CalEventDto> storedEvents = await EventAggService.ReadUpcomingEventsMessagePackAsync(SettingsRetriever.GetMainDataPath());
            foreach (var calendarEvent in storedEvents.Values)
            {
                string eventProvider = calendarEvent.Provider ?? string.Empty;
                bool isLegacyCanvasEvent = provider == "Canvas" &&
                    string.IsNullOrWhiteSpace(eventProvider) &&
                    !string.IsNullOrWhiteSpace(calendarEvent.LongSource);
                if (!string.Equals(eventProvider, provider, StringComparison.OrdinalIgnoreCase) && !isLegacyCanvasEvent)
                {
                    continue;
                }

                CalendarSourceIdentity identity;
                string displayName;
                if (provider == GoogleCalService.ProviderName)
                {
                    if (string.IsNullOrWhiteSpace(calendarEvent.GoogleAccountEmail) ||
                        string.IsNullOrWhiteSpace(calendarEvent.CalendarId))
                    {
                        continue;
                    }

                    identity = CalendarSourceIdentity.CreateGoogle(
                        calendarEvent.GoogleAccountEmail,
                        calendarEvent.CalendarId);
                    displayName = $"{calendarEvent.GoogleAccountEmail} - {calendarEvent.Source}";
                }
                else
                {
                    string className = calendarEvent.Source?.Trim() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(className))
                    {
                        continue;
                    }

                    identity = CalendarSourceIdentity.FromValues(provider, className);
                    displayName = className;
                }

                if (!seenKeys.Add(identity.Key))
                {
                    continue;
                }

                cards.Add(CreateSourceCustomizationCard(identity, displayName));
            }
        }

        foreach (var association in knownAssociations)
        {
            if (!string.Equals(association.Provider, provider, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string key = SourceImageService.BuildKey(association.Provider, association.SourceId);
            if (!seenKeys.Add(key))
            {
                continue;
            }

            var identity = CalendarSourceIdentity.FromValues(association.Provider, association.SourceId);
            string displayName = association.SourceId;
            if (provider == GoogleCalService.ProviderName &&
                CalendarSourceIdentity.TryGetGoogleComponents(association.SourceId, out string accountEmail, out string calendarId))
            {
                displayName = $"{accountEmail} - {calendarId}";
            }
            else if (provider == GoogleCalService.ProviderName && association.SourceId == GoogleCalService.PrimaryCalendarId)
            {
                displayName = "Google Calendar (unscoped)";
            }

            cards.Add(CreateSourceCustomizationCard(identity, displayName));
        }

        return cards.OrderBy(card => card.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private SourceCustomizationCard CreateSourceCustomizationCard(CalendarSourceIdentity identity, string? fallbackName = null)
    {
        string displayName = fallbackName ?? identity.SourceId;
        string imagePath = SourceImageService.Instance.GetSourceImagePath(identity) ?? string.Empty;
        string backgroundColor = "#3A3A3A";

        var config = SettingsRetriever.GetSourceConfigs().FirstOrDefault(pair =>
            pair.Key.Equals(identity.Key, StringComparison.OrdinalIgnoreCase) ||
            pair.Value.Source.Equals(identity.SourceId, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(config.Value?.BkgColor))
        {
            backgroundColor = config.Value.BkgColor;
        }

        var card = new SourceCustomizationCard
        {
            Provider = identity.Provider,
            SourceId = identity.SourceId,
            DisplayName = displayName,
            ImagePath = imagePath,
            HasImage = !string.IsNullOrWhiteSpace(imagePath),
            BackgroundColor = backgroundColor,
            ImageDarkness = SourceImageService.Instance.GetImageDarkness(identity),
        };

        card.BackgroundBrush = CreateBackgroundBrush(card);
        card.StatusText = card.HasImage ? "Image configured" : "No image configured";
        return card;
    }

    private static Brush CreateBackgroundBrush(SourceCustomizationCard card)
    {
        if (!string.IsNullOrWhiteSpace(card.ImagePath) && File.Exists(card.ImagePath))
        {
            var imageBrush = new ImageBrush
            {
                ImageSource = new BitmapImage(new Uri(card.ImagePath, UriKind.Absolute)),
                Stretch = Stretch.UniformToFill,
                AlignmentX = AlignmentX.Center,
                AlignmentY = AlignmentY.Center,
            };

            return imageBrush;
        }

        if (TryParseColor(card.BackgroundColor, out var color))
        {
            return new SolidColorBrush(color);
        }

        return new SolidColorBrush(Windows.UI.Color.FromArgb(255, 58, 58, 58));
    }

    private static bool TryParseColor(string colorText, out Windows.UI.Color color)
    {
        color = Windows.UI.Color.FromArgb(255, 58, 58, 58);
        if (string.IsNullOrWhiteSpace(colorText))
        {
            return false;
        }

        try
        {
            var trimmed = colorText.Trim();
            if (trimmed.StartsWith("#"))
            {
                trimmed = trimmed.Substring(1);
            }

            if (trimmed.Length == 6)
            {
                trimmed = "FF" + trimmed;
            }

            if (trimmed.Length != 8)
            {
                return false;
            }

            color = Windows.UI.Color.FromArgb(
                byte.Parse(trimmed.Substring(0, 2), System.Globalization.NumberStyles.HexNumber),
                byte.Parse(trimmed.Substring(2, 2), System.Globalization.NumberStyles.HexNumber),
                byte.Parse(trimmed.Substring(4, 2), System.Globalization.NumberStyles.HexNumber),
                byte.Parse(trimmed.Substring(6, 2), System.Globalization.NumberStyles.HexNumber));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task PickImageForCardAsync(SourceCustomizationCard card)
    {
        LoggerService.Log($"WidgetWindow: Opening image picker for source '{card.Provider}:{card.SourceId}'.");
        var picker = new FileOpenPicker();
        picker.ViewMode = PickerViewMode.Thumbnail;
        picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        picker.FileTypeFilter.Add(".png");

        var hwnd = WindowNative.GetWindowHandle(this);
        InitializeWithWindow.Initialize(picker, hwnd);

        StorageFile file = await picker.PickSingleFileAsync();
        if (file == null)
        {
            LoggerService.Log($"WidgetWindow: Image picker cancelled for source '{card.Provider}:{card.SourceId}'.");
            return;
        }

        bool assigned = SourceImageService.Instance.TryAssignImageToSource(
            card.Provider,
            card.SourceId,
            file.Path,
            out string managedPath);

        if (assigned)
        {
            LoggerService.Log($"WidgetWindow: Image customization saved for source '{card.Provider}:{card.SourceId}'.");
            await RefreshCustomizationCardsAsync();
        }
        else
        {
            LoggerService.Log(
                $"WidgetWindow: Image customization failed for source '{card.Provider}:{card.SourceId}'.",
                LoggerStatusEnum.WARNING);
        }
    }

    private async Task RemoveImageForCardAsync(SourceCustomizationCard card)
    {
        bool removed = SourceImageService.Instance.RemoveSourceImage(card.Provider, card.SourceId);
        if (removed)
        {
            await RefreshCustomizationCardsAsync();
        }
    }

    private async void ChooseBackgroundImageClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is Button button && button.DataContext is SourceCustomizationCard card)
            {
                await PickImageForCardAsync(card);
                return;
            }

            await PickImageForCardAsync(new SourceCustomizationCard
            {
                Provider = "Google",
                SourceId = GoogleCalService.PrimaryCalendarId,
                DisplayName = "Google Calendar"
            });
        }
        catch (Exception exception)
        {
            LoggerService.LogException("WidgetWindow: Image picker or customization failed.", exception);
        }
    }

    private async void RemoveBackgroundImageClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.DataContext is SourceCustomizationCard card)
        {
            await RemoveImageForCardAsync(card);
        }
    }

    private void ImageDarknessChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (sender is not Slider { DataContext: SourceCustomizationCard card } ||
            Math.Abs(card.ImageDarkness - e.NewValue) < 0.5)
        {
            return;
        }

        card.ImageDarkness = e.NewValue;
        if (_pendingDarknessCard != null && !ReferenceEquals(_pendingDarknessCard, card))
        {
            SavePendingImageDarkness();
        }

        _pendingDarknessCard = card;
        _darknessSaveTimer ??= CreateDarknessSaveTimer();
        _darknessSaveTimer.Stop();
        _darknessSaveTimer.Start();
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer CreateDarknessSaveTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(300);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => SavePendingImageDarkness();
        return timer;
    }

    private void SavePendingImageDarkness()
    {
        _darknessSaveTimer?.Stop();
        SourceCustomizationCard? card = _pendingDarknessCard;
        _pendingDarknessCard = null;
        if (card != null)
        {
            SourceImageService.Instance.SetImageDarkness(card.Provider, card.SourceId, (int)Math.Round(card.ImageDarkness));
        }
    }

    public sealed class SourceCustomizationCard : System.ComponentModel.INotifyPropertyChanged
    {
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        private double _imageDarkness = SourceImageService.DefaultImageDarkness;

        // 0–90; drives the preview overlay here and the image opacity in the calendar views.
        public double ImageDarkness
        {
            get => _imageDarkness;
            set
            {
                if (Math.Abs(_imageDarkness - value) < 0.01)
                {
                    return;
                }

                _imageDarkness = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(ImageDarkness)));
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(DarknessOverlayOpacity)));
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(ImageDarknessText)));
            }
        }

        public double DarknessOverlayOpacity => _imageDarkness / 100.0;
        public string ImageDarknessText => $"{Math.Round(_imageDarkness)}%";

        public string Provider { get; set; } = string.Empty;
        public string SourceId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string ImagePath { get; set; } = string.Empty;
        public bool HasImage { get; set; }
        public Visibility RemoveImageVisibility => HasImage ? Visibility.Visible : Visibility.Collapsed;
        public Visibility ImageOverlayVisibility => HasImage ? Visibility.Visible : Visibility.Collapsed;
        public ElementTheme CardTheme => HasImage ? ElementTheme.Dark : ElementTheme.Default;
        public string BackgroundColor { get; set; } = "#3A3A3A";
        public string StatusText { get; set; } = "No image configured";
        public Brush BackgroundBrush { get; set; } = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 58, 58, 58));
    }

    private void BkgStyleChanged(object sender, SelectionChangedEventArgs e)
    {
        SaveBackgroundType();
        ApplyBackgroundType();
    }

    private void WindowTransparencyInputChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_isUpdatingTransparencyControls)
        {
            return;
        }

        if (double.IsNaN(args.NewValue))
        {
            _isUpdatingTransparencyControls = true;
            sender.Value = SettingsRetriever.GetWindowTransparency();
            _isUpdatingTransparencyControls = false;
            return;
        }

        int transparencyPercentage = (int)Math.Clamp(Math.Round(args.NewValue), 0, 100);
        SettingsRetriever.SetWindowTransparency(transparencyPercentage);
    }

    private async void GoogleSIClick(object sender, RoutedEventArgs e)
    {
        await GoogleSignInAsync();
    }

    private async void GoogleSignOutClick(object sender, RoutedEventArgs e)
    {
        await EventAggService.GetGoogleCalService().SignOutAsync();
        await SetGoogleEnabledAsync(false);
        _isUpdatingSourceControls = true;
        EnableGoogle.IsOn = false;
        _isUpdatingSourceControls = false;
        await UpdateConnectionStatusAsync(false);
        await UpdateViewSourceAvailabilityAsync();
    }

    private async void GoogleSwitchAccountClick(object sender, RoutedEventArgs e)
    {
        await EventAggService.GetGoogleCalService().SignOutAsync();
        await SetGoogleEnabledAsync(false);
        _isUpdatingSourceControls = true;
        EnableGoogle.IsOn = false;
        _isUpdatingSourceControls = false;
        await GoogleSignInAsync();
    }

    private async Task GoogleSignInAsync()
    {
        GoogleSI.IsEnabled = false;
        GoogleConnectionStatus.Text = "Waiting for Google sign-in...";
        GoogleConnectionStatus.Visibility = Visibility.Visible;
        try
        {
            bool connected = await EventAggService.GetGoogleCalService().AuthorizeAsync();
            if (connected)
            {
                await SetGoogleEnabledAsync(true, reloadGoogleService: true);
                _isUpdatingSourceControls = true;
                EnableGoogle.IsOn = true;
                _isUpdatingSourceControls = false;
            }

            await UpdateConnectionStatusAsync(connected);
        }
        finally
        {
            await UpdateViewSourceAvailabilityAsync();
            GoogleSI.IsEnabled = true;
        }
    }

    private void SaveBackgroundType()
    {
        if (BkgStyleRadioSettings.SelectedItem is not string selectedStyle)
        {
            return;
        }

        BackgroundTypeEnum backgroundType = selectedStyle switch
        {
            "Mica" => BackgroundTypeEnum.Mica,
            "Acrylic" => BackgroundTypeEnum.Acrylic,
            _ => BackgroundTypeEnum.Solid
        };

        SettingsRetriever.SetBackgroundType(backgroundType);
    }
    
    private async void CanvasSaveClick(object sender, RoutedEventArgs e)
    {
        bool response = SettingsRetriever.SetCanvasICSLink(CanvasCalLinkInput.Text);
        if (!response)
        {
            CanvasLinkStatus.Text = "Enter a valid calendar URL.";
            return;
        }

        CanvasLinkStatus.Text = "Canvas calendar link saved.";
        UpdateCanvasConnectButtonText();
        await EventAggService.ReloadSourcesAsync();
        await UpdateViewSourceAvailabilityAsync();
    }

    // ---- School selector -------------------------------------------------------------

    private static readonly IReadOnlyList<string> SupportedSchools = UniRegexService.GetSupportedSchools();

    private static List<string> FilterSchools(string query)
    {
        string trimmed = query?.Trim() ?? string.Empty;
        return trimmed.Length == 0
            ? SupportedSchools.ToList()
            : SupportedSchools.Where(name => name.Contains(trimmed, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private static string? FindSchool(string text)
    {
        return SupportedSchools.FirstOrDefault(name => string.Equals(name, text?.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private void ShowSavedSchool()
    {
        string school = SettingsRetriever.GetSchoolName();
        CanvasSchoolSelector.Text = school;
        CanvasSchoolHint.Text = school.Length == 0
            ? "No school selected. Course names are shown as they appear in Canvas."
            : $"Course names are read using {school}'s naming format.";
    }

    private void CanvasSchoolSelectorGotFocus(object sender, RoutedEventArgs e)
    {
        bool showingSaved = string.Equals(CanvasSchoolSelector.Text, SettingsRetriever.GetSchoolName(), StringComparison.Ordinal);
        CanvasSchoolSelector.ItemsSource = FilterSchools(showingSaved ? string.Empty : CanvasSchoolSelector.Text);
        CanvasSchoolSelector.IsSuggestionListOpen = true;
    }

    private void CanvasSchoolSelectorTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            sender.ItemsSource = FilterSchools(sender.Text);
            sender.IsSuggestionListOpen = true;
        }
    }

    private async void CanvasSchoolSelectorQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        string? school = args.ChosenSuggestion as string;
        if (school == null)
        {
            List<string> matches = FilterSchools(args.QueryText);
            school = string.IsNullOrWhiteSpace(args.QueryText)
                ? string.Empty
                : FindSchool(args.QueryText) ?? (matches.Count == 1 ? matches[0] : null);
        }

        if (school == null)
        {
            return;
        }

        sender.IsSuggestionListOpen = false;
        await ApplySchoolAsync(school);
    }

    private void CanvasSchoolSelectorLostFocus(object sender, RoutedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, async () =>
        {
            if (CanvasSchoolSelector.FocusState != FocusState.Unfocused)
            {
                return;
            }

            string text = CanvasSchoolSelector.Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                await ApplySchoolAsync(string.Empty);
            }
            else if (FindSchool(text) is string exact)
            {
                await ApplySchoolAsync(exact);
            }
            else
            {
                ShowSavedSchool();
            }
        });
    }

    private async Task ApplySchoolAsync(string school)
    {
        bool changed = !string.Equals(school, SettingsRetriever.GetSchoolName(), StringComparison.Ordinal);
        if (changed)
        {
            SettingsRetriever.SetSchoolName(school);
            LoggerService.Log($"WidgetWindow: School set to '{(school.Length == 0 ? "<none>" : school)}'.");
        }

        ShowSavedSchool();

        if (changed && IsCanvasCalendarConfigured(SettingsRetriever.GetCanvasICSLink()))
        {
            CanvasSyncStatus.Text = "Updating course names…";
            try
            {
                bool? refreshed = await EventAggService.RefreshCanvas();
                CanvasSyncStatus.Text = refreshed == true
                    ? "Course names updated for the selected school."
                    : "Canvas events could not be refreshed.";
            }
            catch (Exception exception)
            {
                LoggerService.LogException("WidgetWindow.ApplySchoolAsync", exception);
                CanvasSyncStatus.Text = "Canvas refresh failed. See Logs.txt for details.";
            }
        }
    }

    private void UpdateCanvasConnectButtonText()
    {
        CanvasConnectButtonText.Text = IsCanvasCalendarConfigured(SettingsRetriever.GetCanvasICSLink())
            ? "Update"
            : "Connect Calendar";
        UpdateCanvasSourceRow();
    }

    private async void RefreshCanvasSources(object sender, RoutedEventArgs e)
    {
        try
        {
            bool? response = await EventAggService.RefreshCanvas();
            if (response != true)
            {
                LoggerService.Log(
                    $"WidgetWindow.RefreshCanvasSources: Canvas refresh did not complete (result: {response?.ToString() ?? "null"}).",
                    response == false ? LoggerStatusEnum.WARNING : LoggerStatusEnum.ERROR);
            }
            CanvasSyncStatus.Text = response == true
                ? "Canvas events refreshed."
                : "Canvas events could not be refreshed.";
        }
        catch (OperationCanceledException) when (EventAggService.IsShutdownRequested)
        {
            LoggerService.Log("WidgetWindow: Canvas refresh canceled because Windows is shutting down.");
        }
        catch (Exception exception)
        {
            LoggerService.LogException("WidgetWindow.RefreshCanvasSources", exception);
            CanvasSyncStatus.Text = "Canvas refresh failed. See Logs.txt for details.";
        }
    }
    private async void RefreshGoogleSources(object sender, RoutedEventArgs e)
    {
        RefreshGoogleSourcesButton.IsEnabled = false;
        GoogleSyncStatus.Text = "Syncing Google Calendar...";
        try
        {
            string mainDataPath = SettingsRetriever.GetMainDataPath();
            if (!File.Exists(mainDataPath))
            {
                LoggerService.Log(
                    $"WidgetWindow.RefreshGoogleSources: Could not find '{mainDataPath}'. Creating a default file.",
                    LoggerStatusEnum.WARNING);
                await EventAggService.WriteUpcomingEventsMessagePackAsync(mainDataPath);
            }

            bool? response = await EventAggService.RefreshGoogle();
            GoogleSyncStatus.Text = response == true
                ? "Google Calendar events refreshed."
                : "Google Calendar events could not be refreshed.";
        }
        catch (OperationCanceledException) when (EventAggService.IsShutdownRequested)
        {
            LoggerService.Log("WidgetWindow: Google refresh canceled because Windows is shutting down.");
        }
        catch (Exception exception)
        {
            LoggerService.LogException("WidgetWindow.RefreshGoogleSources", exception);
            GoogleSyncStatus.Text = "Google refresh failed. See Logs.txt for details.";
        }
        finally
        {
            RefreshGoogleSourcesButton.IsEnabled = true;
        }
    }

    private async Task UpdateConnectionStatusAsync(bool? connectedOverride = null)
    {
        bool connected = connectedOverride ?? await EventAggService.GetGoogleCalService().IsAccountActiveAsync();
        GoogleConnectionStatus.Text = "Not connected";
        GoogleConnectionStatus.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
        GoogleSignedOutPanel.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
        GoogleAccountCard.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        GoogleSyncRow.Visibility = connected
            ? Visibility.Visible
            : Visibility.Collapsed;

        UpdateGoogleSourceRow(connected, email: null);

        if (connected)
        {
            GoogleAccountProfile? profile = await EventAggService.GetGoogleCalService().GetAccountProfileAsync();
            if (profile != null)
            {
                GoogleProfileNameText.Text = string.IsNullOrWhiteSpace(profile.Name) ? profile.Email : profile.Name;
                GoogleProfileEmailText.Text = profile.Email;
                if (Uri.TryCreate(profile.PictureUrl, UriKind.Absolute, out Uri? pictureUri))
                    GoogleProfileImage.Source = new BitmapImage(pictureUri);
                UpdateGoogleSourceRow(connected, profile.Email);
            }
        }
    }

    private void ApplyBackgroundType()
    {
        WindowConfiguration.ApplyBackground(this);
    }

    private void GoogleToggled(object sender, RoutedEventArgs e)
    {
        if (EnableGoogle.IsOn) GoogleEnabled(sender, e); else GoogleDisabled(sender, e);
    }

    private void CanvasToggled(object sender, RoutedEventArgs e)
    {
        if (EnableCanvas.IsOn) CanvasEnabled(sender, e); else CanvasDisabled(sender, e);
    }

    private void PrimaryViewToggled(object sender, RoutedEventArgs e)
    {
        if (EnablePrimaryView.IsOn) PrimaryViewEnabled(sender, e); else PrimaryViewDisabled(sender, e);
    }

    private void PinnedViewToggled(object sender, RoutedEventArgs e)
    {
        if (EnablePinnedView.IsOn) PinnedViewEnabled(sender, e); else PinnedViewDisabled(sender, e);
    }

    private void SourceSetupClick(object sender, RoutedEventArgs e)
    {
        string? target = (sender as FrameworkElement)?.Tag as string;
        object? item = SettingsNav.MenuItems
            .OfType<NavigationViewItem>()
            .FirstOrDefault(navItem => navItem.Tag as string == target);
        if (item != null)
        {
            SettingsNav.SelectedItem = item;
        }
    }

    private void UpdateGoogleSourceRow(bool connected, string? email)
    {
        GoogleSourceStatus.Text = connected
            ? string.IsNullOrWhiteSpace(email) ? "Connected" : $"Connected as {email}"
            : "Not set up";
        GoogleSetupLink.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
        EnableGoogle.IsEnabled = connected;

        _isUpdatingSourceControls = true;
        EnableGoogle.IsOn = connected && SettingsRetriever.GetGoogleEnabled();
        _isUpdatingSourceControls = false;
    }

    private void UpdateCanvasSourceRow()
    {
        string link = SettingsRetriever.GetCanvasICSLink();
        bool configured = IsCanvasCalendarConfigured(link);
        CanvasSourceStatus.Text = configured && Uri.TryCreate(link, UriKind.Absolute, out Uri? feedUri)
            ? $"Connected to {feedUri.Host}"
            : "Not set up";
        CanvasSetupLink.Visibility = configured ? Visibility.Collapsed : Visibility.Visible;
        CanvasSyncRow.Visibility = configured ? Visibility.Visible : Visibility.Collapsed;
        EnableCanvas.IsEnabled = configured;
    }

    private async void GoogleEnabled(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingSourceControls)
        {
            return;
        }

        await SetGoogleEnabledAsync(true);
    }
    private async void GoogleDisabled(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingSourceControls)
        {
            return;
        }

        await SetGoogleEnabledAsync(false);
    }

    private async void CanvasEnabled(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingSourceControls)
        {
            return;
        }

        await SetCanvasEnabledAsync(true);
    }

    private async void CanvasDisabled(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingSourceControls)
        {
            return;
        }

        await SetCanvasEnabledAsync(false);
    }

    private async Task SetGoogleEnabledAsync(bool enabled, bool reloadGoogleService = false)
    {
        SettingsRetriever.SetGoogleEnabled(enabled);
        LoggerService.Log(
            $"WidgetWindow: Google Calendar source toggled {(enabled ? "enabled" : "disabled")}.",
            LoggerStatusEnum.INFO);

        if (reloadGoogleService)
        {
            await EventAggService.ReloadGoogleServiceAsync();
        }
        else
        {
            await EventAggService.ReloadSourcesAsync();
        }

        await ApplySourceToggleToEventsAsync("google", enabled);
    }

    private async Task SetCanvasEnabledAsync(bool enabled)
    {
        SettingsRetriever.SetCanvasEnabled(enabled);
        LoggerService.Log(
            $"WidgetWindow: Canvas source toggled {(enabled ? "enabled" : "disabled")}.",
            LoggerStatusEnum.INFO);
        await EventAggService.ReloadSourcesAsync();
        await ApplySourceToggleToEventsAsync("canvas", enabled);
    }

    private static async Task ApplySourceToggleToEventsAsync(string source, bool enabled)
    {
        try
        {
            if (!enabled)
            {
                await EventAggService.RemoveSourceEventsAsync(source);
                return;
            }

            bool? refreshed = source == "google"
                ? await EventAggService.RefreshGoogle()
                : await EventAggService.RefreshCanvas();
            if (refreshed != true)
            {
                LoggerService.Log(
                    $"WidgetWindow: Refresh after enabling '{source}' did not complete (result: {refreshed?.ToString() ?? "null"}).",
                    LoggerStatusEnum.WARNING);
            }
        }
        catch (Exception exception)
        {
            LoggerService.LogException(
                $"WidgetWindow: Failed to apply '{source}' toggle to stored events.",
                exception);
        }
    }

    private void PrimaryViewChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PrimaryViewSelector.SelectedIndex is < 0 or > 2)
        {
            return;
        }

        PrimaryViewEnum selectedView = PrimaryViewSelector.SelectedIndex switch
        {
            0 => PrimaryViewEnum.Todo,
            1 => PrimaryViewEnum.Day,
            2 => PrimaryViewEnum.Week,
            _ => PrimaryViewEnum.Todo
        };
        SettingsRetriever.SetPrimaryView(selectedView);
        if (_hasConfiguredSource && SettingsRetriever.GetPrimaryViewEnabled())
        {
            ViewWindowManager.SyncWithSettings(replacePrimaryView: true);
        }
    }

    private void PrimaryViewEnabled(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingPrimaryViewControl)
        {
            return;
        }

        if (!_hasConfiguredSource)
        {
            _isUpdatingPrimaryViewControl = true;
            EnablePrimaryView.IsOn = false;
            _isUpdatingPrimaryViewControl = false;
            return;
        }

        SettingsRetriever.SetPrimaryViewEnabled(true);
        _ = StartupLaunchService.SyncWithSettingsAsync();
        ViewWindowManager.SyncWithSettings();
    }

    private void PrimaryViewDisabled(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingPrimaryViewControl)
        {
            return;
        }

        SettingsRetriever.SetPrimaryViewEnabled(false);
        _ = StartupLaunchService.SyncWithSettingsAsync();
        ViewWindowManager.SyncWithSettings();
    }

    private void OnPrimaryViewClosedByUser()
    {
        SettingsRetriever.SetPrimaryViewEnabled(false);
        _ = StartupLaunchService.SyncWithSettingsAsync();
        _isUpdatingPrimaryViewControl = true;
        EnablePrimaryView.IsOn = false;
        _isUpdatingPrimaryViewControl = false;
    }

    private async void StartAtSignInToggled(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingStartAtSignInControl)
        {
            return;
        }

        EnableStartAtSignIn.IsEnabled = false;
        await StartupLaunchService.SetEnabledAsync(EnableStartAtSignIn.IsOn);
    }

    private void OnStartupStatusChanged(StartupStatus status)
    {
        DispatcherQueue.TryEnqueue(() => ApplyStartupStatus(status));
    }

    private void ApplyStartupStatus(StartupStatus status)
    {
        _isUpdatingStartAtSignInControl = true;
        EnableStartAtSignIn.IsOn = status.IsOn;
        EnableStartAtSignIn.IsEnabled = status.CanChange;
        _isUpdatingStartAtSignInControl = false;

        StartAtSignInMessage.Text = status.Message ?? string.Empty;
        StartAtSignInMessage.Visibility = string.IsNullOrEmpty(status.Message)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void PinnedViewEnabled(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingPrimaryViewControl)
        {
            return;
        }

        if (!_hasConfiguredSource)
        {
            _isUpdatingPrimaryViewControl = true;
            EnablePinnedView.IsOn = false;
            _isUpdatingPrimaryViewControl = false;
            return;
        }

        SettingsRetriever.SetPinnedViewEnabled(true);
        ViewWindowManager.SyncWithSettings();
    }

    private void PinnedViewDisabled(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingPrimaryViewControl)
        {
            return;
        }

        SettingsRetriever.SetPinnedViewEnabled(false);
        ViewWindowManager.SyncWithSettings();
    }
}

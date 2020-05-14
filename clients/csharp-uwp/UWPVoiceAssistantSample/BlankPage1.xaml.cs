﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

// The Blank Page item template is documented at https://go.microsoft.com/fwlink/?LinkId=234238

namespace UWPVoiceAssistantSample
{
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.Collections.Specialized;
    using System.Diagnostics;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Xml;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.UI.Xaml.Controls;
    using Newtonsoft.Json;
    using UWPVoiceAssistantSample.AudioCommon;
    using UWPVoiceAssistantSample.AudioInput;
    using Windows.ApplicationModel.ConversationalAgent;
    using Windows.ApplicationModel.Core;
    using Windows.ApplicationModel.LockScreen;
    using Windows.Security.Authorization.AppCapabilityAccess;
    using Windows.Storage;
    using Windows.System;
    using Windows.System.Power;
    using Windows.UI;
    using Windows.UI.Core;
    using Windows.UI.Notifications;
    using Windows.UI.ViewManagement;
    using Windows.UI.Xaml;
    using Windows.UI.Xaml.Controls;
    using Windows.UI.Xaml.Documents;
    using Windows.UI.Xaml.Media;
    using Windows.UI.Xaml.Navigation;

    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class BlankPage1 : Page
    {
        /// <summary>
        /// Collection of utterances from user and bot.
        /// </summary>
        public ObservableCollection<Conversation> conversations;

        private static List<Conversation> globalConversations;

        private readonly ServiceProvider services;
        private readonly ILogProvider logger;
        private readonly IKeywordRegistration keywordRegistration;
        private readonly IDialogManager dialogManager;
        private readonly IAgentSessionManager agentSessionManager;
        private readonly HashSet<TextBlock> informationLogs;
        private readonly HashSet<TextBlock> errorLogs;
        private readonly HashSet<TextBlock> noiseLogs;
        private readonly App app;
        private bool configModified;
        private bool hypotheizedSpeechToggle;
        private Conversation activeConversation;
        private int logBufferIndex;
        private bool handlersAdded;

        private CoreDispatcher dispatcher
        {
            get
            {
                CoreApplicationView currentViewDispatcher;
                try
                {
                    Debug.WriteLine("blankpage: get dispatcher");
                    currentViewDispatcher = CoreApplication.GetCurrentView();
                }
                catch (Exception e)
                {
                    return this.Dispatcher;
                }

                if (currentViewDispatcher != null)
                {
                    return CoreApplication.GetCurrentView().Dispatcher;
                }
                else
                {
                    return this.Dispatcher;
                }
            }
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="BlankPage1"/> class.
        /// </summary>
        public BlankPage1()
        {
            Debug.WriteLine("blankpage: Start page");
            this.logger = LogRouter.GetClassLogger();

            this.InitializeComponent();

            this.app = App.Current as App;
            this.app.HasReachedForeground = true;

            this.services = this.app.Services;
            this.dialogManager = this.services.GetRequiredService<IDialogManager>();
            this.keywordRegistration = this.services.GetRequiredService<IKeywordRegistration>();
            this.agentSessionManager = this.services.GetRequiredService<IAgentSessionManager>();

            this.informationLogs = new HashSet<TextBlock>();
            this.errorLogs = new HashSet<TextBlock>();
            this.noiseLogs = new HashSet<TextBlock>();

            // Ensure that we restore the full view (not the compact mode) upon foreground launch
            _ = this.UpdateViewStateAsync();

            // Ensure we have microphone permissions and that we pop a consent dialog if the user
            // hasn't already given an explicit yes/no.
            _ = Task.Run(async () =>
            {
                var control = await AudioCaptureControl.GetInstanceAsync();
                await control.MicrophoneCapability.RequestAccessAsync();
            });

            // Kick off the registration and/or retrieval of the 1st-stage keyword information
            _ = this.DoKeywordSetupAsync();

            // Populate the drop-down list for TTS audio output formats and select the current choice
            var supportedFormats = DirectLineSpeechAudio.SupportedOutputFormats;
            foreach (var entry in supportedFormats)
            {
                this.OutputFormatComboBox.Items.Add(entry.Label);
            }

            this.OutputFormatComboBox.SelectedItem = this.OutputFormatComboBox.Items.FirstOrDefault(item =>
                item.ToString() == LocalSettingsHelper.OutputFormat.Label);

            // Wire a few pieces of UI handling that aren't trivially handled by XAML bindings
            this.AddUIHandlersAsync();

            // Ensure consistency between a few dependent controls and their settings
            this.UpdateUIBasedOnToggles();

            BlankPage1.globalConversations = BlankPage1.globalConversations ?? new List<Conversation>();
            this.conversations = new ObservableCollection<Conversation>();

            BlankPage1.globalConversations.ForEach((element) => this.conversations.Add(element));

            this.conversations.CollectionChanged += (object sender, NotifyCollectionChangedEventArgs e) =>
            {
                foreach (Conversation conversation in e.NewItems)
                {
                    BlankPage1.globalConversations.Add(conversation);
                }
            };

            this.ChatHistoryListView.ContainerContentChanging += this.OnChatHistoryListViewContainerChanging;

            if (!CoreApplication.GetCurrentView().IsMain)
            {
                LockApplicationHost lockHost = LockApplicationHost.GetForCurrentView();
                if (lockHost != null)
                {
                    lockHost.Unlocking += this.LockHost_Unlocking;
                }
            }

            Window.Current.CoreWindow.Activated += (sender, e) =>
            {
                if (!this.handlersAdded)
                {
                    this.handlersAdded = true;
                    this.AddSystemAvailabilityHandlers();
                    this.AddDialogHandlersAsync();
                }
            };

            Debug.WriteLine("blankpage: Render");
        }

        private void LockHost_Unlocking(LockApplicationHost sender, LockScreenUnlockingEventArgs args)
        {
            // save any unsaved work and gracefully exit the app
            //Window.Current.Close();
            Task.Run(async () =>
            {
                var session = await this.agentSessionManager.GetSessionAsync();
                session.SystemStateChanged -= this.System_State_Changed;
            });
        }

        private bool BackgroundTaskRegistered
        {
            get => MVARegistrationHelpers.IsBackgroundTaskRegistered;
            set
            {
                MVARegistrationHelpers.IsBackgroundTaskRegistered = value;
                _ = this.UpdateUIForSharedStateAsync();
            }
        }

        private void AddUIHandlersAsync()
        {
            this.DismissButton.Click += (_, __) =>
            {
                LockApplicationHost lockHost = LockApplicationHost.GetForCurrentView();
                if (lockHost != null)
                {
                    Debug.WriteLine("blankpage: request unlock");
                    lockHost.RequestUnlock();
                    Window.Current.Close();
                }
            };
            this.MicrophoneButton.Click += async (_, __) =>
            {
                this.dialogManager.HandleSignalDetection(DetectionOrigin.FromPushToTalk);
                await this.UpdateUIForSharedStateAsync();
            };
            this.ResetButton.Click += async (_, __) =>
            {
                await this.dialogManager.FinishConversationAsync();
                await this.dialogManager.StopAudioPlaybackAsync();
                BlankPage1.globalConversations.Clear();
                this.RefreshStatus();
            };
            this.ClearLogsButton.Click += async (_, __)
                => await this.dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
                {
                    this.ChangeLogStackPanel.Children.Clear();
                });

            this.OpenLogLocationButton.Click += async (_, __)
                => await Launcher.LaunchFolderAsync(ApplicationData.Current.LocalFolder);
        }

        private async Task UpdateViewStateAsync()
        {
            var session = await this.agentSessionManager.GetSessionAsync();
            if (session.IsUserAuthenticated)
            {
                var appView = ApplicationView.GetForCurrentView();
                await appView.TryEnterViewModeAsync(ApplicationViewMode.Default);
            }
        }

        private async Task DoKeywordSetupAsync()
        {
            var keywordConfig = await this.keywordRegistration.GetOrCreateKeywordConfigurationAsync();
            keywordConfig.AvailabilityChanged += async (s, e)
                => await this.UpdateUIForSharedStateAsync();
            await this.UpdateUIForSharedStateAsync();
        }

        private async void AddSystemAvailabilityHandlers()
        {
            var inputControl = await AudioCaptureControl.GetInstanceAsync();
            inputControl.MicrophoneCapability.AccessChanged += async (s, e)
                => await this.UpdateUIForSharedStateAsync();
            inputControl.AudioInputDeviceChanged += async ()
                => await this.UpdateUIForSharedStateAsync();
            inputControl.InputVolumeStateChanged += async ()
                => await this.UpdateUIForSharedStateAsync();
            var session = await this.agentSessionManager.GetSessionAsync();
            if (session != null)
            {
                session.SystemStateChanged += this.System_State_Changed;
            }

            PowerManager.EnergySaverStatusChanged += async (s, e)
                => await this.UpdateUIForSharedStateAsync();
        }

        private async void System_State_Changed(ConversationalAgentSession session, ConversationalAgentSystemStateChangedEventArgs args)
        {
            await this.UpdateUIForSharedStateAsync();
        }

        private void AddDialogHandlersAsync()
        {
            // Ensure we update UI state (like buttons) when detection begins
            this.dialogManager.DialogStateChanged += async (s, e)
                => await this.UpdateUIForSharedStateAsync();

            // TODO: This is probably too busy for hypothesis events; better way of showing intermediate results?
            this.dialogManager.SpeechRecognizing += (s, e) =>
            {
                this.AddHypothesizedSpeechToTextBox(e);
            };
            this.dialogManager.SpeechRecognized += (s, e) =>
            {
                this.AddMessageToStatus(e);
            };
            this.dialogManager.DialogResponseReceived += (s, e) =>
            {
                // TODO: Duplicate wrapper creation unnecessary
                var wrapper = new ActivityWrapper(e.MessageBody.ToString());
                if (wrapper.Type == ActivityWrapper.ActivityType.Message)
                {
                    this.AddBotResponse(wrapper.Message);
                }
            };

            this.logger.LogAvailable += (s, e) =>
            {
                this.WriteLog();
            };
            this.logger.Log(LogMessageLevel.Noise, "Main page created, UI rendering");
        }

        private void UpdateUIBasedOnToggles()
        {
            var useSpeechSdk = LocalSettingsHelper.EnableSpeechSDK;
            var visibility = useSpeechSdk ? Visibility.Visible : Visibility.Collapsed;
            var useKws = useSpeechSdk && LocalSettingsHelper.EnableSecondStageKws;
            var enableLogs = useSpeechSdk && LocalSettingsHelper.EnableSdkLogging;

            this.EnableSpeechSDKLoggingToggle.Visibility = visibility;
            this.EnableSecondStageKwsToggle.Visibility = visibility;
            this.EnableSecondStageKwsToggle.IsOn = useKws;
            this.EnableSpeechSDKLoggingToggle.IsOn = enableLogs;

            this.RefreshStatus();
        }

        private async Task UpdateUIForSharedStateAsync()
        {
            // UI changes must be performed on the UI thread.
            await this.dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
            {
                this.RefreshStatus();

                var session = await this.agentSessionManager.GetSessionAsync();
                var audioControl = await AudioCaptureControl.GetInstanceAsync();
                var micStatus = audioControl.MicrophoneCapability.CheckAccess();

                var agentIdle = session == null || session.AgentState == ConversationalAgentState.Inactive;
                var micReady = micStatus == AppCapabilityAccessStatus.Allowed && audioControl.HasAudioInputAvailable;

                var keywordConfig = await this.keywordRegistration.GetOrCreateKeywordConfigurationAsync();

                this.AppVoiceActivationEnabledToggle.IsEnabled = keywordConfig != null;
                this.AppVoiceActivationEnabledToggle.OffContent = keywordConfig != null
                    ? "Application has disabled voice activation."
                    : "App voice activation status unknown: configuration not yet queried";
                this.AppVoiceActivationEnabledToggle.IsOn = keywordConfig != null && keywordConfig.AvailabilityInfo.IsEnabled;

                this.MicrophoneButton.IsEnabled = agentIdle && micReady;
                this.MicrophoneButton.Content = micReady ? Glyphs.Microphone : Glyphs.MicrophoneOff;

                var microphoneStatusInfo = await UIAudioStatus.GetMicrophoneStatusAsync();
                this.MicrophoneInfoIcon.Glyph = microphoneStatusInfo.Glyph;
                this.MicrophoneInfoIcon.Foreground = new SolidColorBrush(microphoneStatusInfo.Color);

                int teachingTipCount = 0;

                if (microphoneStatusInfo.Status[0] == UIAudioStatus.MicrophoneAvailable)
                {
                    this.MicrophoneLinkButton.Content = UIAudioStatus.MicrophoneAvailable;
                    this.TeachingTipStackPanel.Children.Clear();
                }
                else
                {
                    this.MicrophoneLinkButton.Content = "Microphone is not available.";
                    this.VAStatusIcon.Glyph = Glyphs.Warning;
                    this.TeachingTipStackPanel.Children.Clear();
                }

                var voiceActivationStatusInfo = await UIAudioStatus.GetVoiceActivationStatusAsync();
                this.VAStatusIcon.Glyph = voiceActivationStatusInfo.Glyph;
                this.VAStatusIcon.Foreground = new SolidColorBrush(voiceActivationStatusInfo.Color);

                if (voiceActivationStatusInfo.Status[0] == UIAudioStatus.VoiceActivationEnabledMessage)
                {
                    this.VoiceActivationLinkButton.Content = UIAudioStatus.VoiceActivationEnabledMessage;
                    this.TeachingTipStackPanel.Children.Clear();
                }
                else
                {
                    this.VoiceActivationLinkButton.Content = "Voice activation is not available";
                    this.TeachingTipStackPanel.Children.Clear();
                }

                foreach (var item in microphoneStatusInfo.Status)
                {
                    TextBlock microphoneStatusTextBlock = new TextBlock();
                    TextBlock emptyTextBlock = new TextBlock();
                    Border border = new Border();
                    microphoneStatusTextBlock.Text = item;
                    microphoneStatusTextBlock.TextWrapping = TextWrapping.WrapWholeWords;
                    emptyTextBlock.Text = string.Empty;
                    emptyTextBlock.Height = 5;
                    border.BorderBrush = new SolidColorBrush(Colors.LightGray);
                    border.BorderThickness = new Thickness(1);
                    this.TeachingTipStackPanel.Children.Add(microphoneStatusTextBlock);
                    this.TeachingTipStackPanel.Children.Add(emptyTextBlock);
                    this.TeachingTipStackPanel.Children.Add(border);
                    teachingTipCount++;

                    if (item == UIAudioStatus.MicrophoneAvailable)
                    {
                        this.TeachingTipStackPanel.Children.Remove(microphoneStatusTextBlock);
                        this.TeachingTipStackPanel.Children.Remove(border);
                        teachingTipCount--;
                    }
                }

                foreach (var item in voiceActivationStatusInfo.Status)
                {
                    TextBlock voiceActivationStatusTextBlock = new TextBlock();
                    TextBlock emptyTextBlock = new TextBlock();
                    Border border = new Border();
                    voiceActivationStatusTextBlock.Text = item;
                    voiceActivationStatusTextBlock.TextWrapping = TextWrapping.WrapWholeWords;
                    emptyTextBlock.Text = string.Empty;
                    emptyTextBlock.Height = 5;
                    border.BorderBrush = new SolidColorBrush(Colors.LightGray);
                    border.BorderThickness = new Thickness(1);
                    this.TeachingTipStackPanel.Children.Add(voiceActivationStatusTextBlock);
                    this.TeachingTipStackPanel.Children.Add(emptyTextBlock);
                    this.TeachingTipStackPanel.Children.Add(border);
                    teachingTipCount++;

                    if (item == UIAudioStatus.VoiceActivationEnabledMessage)
                    {
                        this.TeachingTipStackPanel.Children.Remove(voiceActivationStatusTextBlock);
                        this.TeachingTipStackPanel.Children.Remove(border);
                        teachingTipCount--;
                    }
                }

                if (teachingTipCount == 0)
                {
                    this.ApplicationStateBadgeIcon.Glyph = Glyphs.CircleCheckMark;
                    this.ApplicationStateBadgeIcon.Foreground = new SolidColorBrush(Colors.Green);
                    this.ApplicationStateBadgeIcon.FontSize = 35;
                    this.ApplicationStateBadge.IsEnabled = false;
                }
                else
                {
                    this.ApplicationStateBadgeIcon.Glyph = Glyphs.Warning;
                    this.ApplicationStateBadgeIcon.Foreground = new SolidColorBrush(Colors.DarkOrange);
                    this.ApplicationStateBadgeIcon.FontSize = 20;
                    this.ApplicationStateBadge.IsEnabled = true;
                }

                this.ApplicationStateBadge.Content = $"{teachingTipCount} Warnings";

                this.DismissButton.Visibility = session.IsUserAuthenticated ? Visibility.Collapsed : Visibility.Visible;
            });
        }

        private async void RefreshStatus()
        {
            var session = await this.agentSessionManager.GetSessionAsync().ConfigureAwait(false);
            var agentStatusMessage = session == null ?
               "No current agent session"
               : $"{session.AgentState.ToString()} {(this.app.InvokedViaSignal ? "[via signal]" : string.Empty)}";

            _ = this.dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                this.ConversationStateTextBlock.Text = $"System: {agentStatusMessage}";
            });
        }

        private void AddBotResponse(string message)
        {
            _ = this.dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                var newConversation = new Conversation
                {
                    Body = message,
                    Time = DateTime.Now.ToString(CultureInfo.CurrentCulture),
                    Sent = false,
                };

                BlankPage1.globalConversations.Add(newConversation);
                this.conversations.Add(newConversation);
            });
        }

        private void AddHypothesizedSpeechToTextBox(string message)
        {
            _ = this.dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                if (this.hypotheizedSpeechToggle)
                {
                    if (this.activeConversation == null)
                    {
                        this.activeConversation = new Conversation
                        {
                            Body = message,
                            Time = DateTime.Now.ToString(CultureInfo.CurrentCulture),
                            Sent = true,
                        };

                        BlankPage1.globalConversations.Add(this.activeConversation);
                        this.conversations.Add(this.activeConversation);
                    }

                    this.activeConversation.Body = message;

                    this.conversations.Last().Body = message;
                }
            });
        }

        private void AddMessageToStatus(string message)
        {
            _ = this.dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                if (this.activeConversation == null)
                {
                    this.activeConversation = new Conversation
                    {
                        Body = message,
                        Time = DateTime.Now.ToString(CultureInfo.CurrentCulture),
                        Sent = true,
                    };

                    BlankPage1.globalConversations.Add(this.activeConversation);
                    this.conversations.Add(this.activeConversation);
                }

                this.activeConversation.Body = message;
                this.activeConversation = null;
            });

            this.RefreshStatus();
        }

        private bool LogInformation(string information)
        {
            if (information.Contains("Information", StringComparison.OrdinalIgnoreCase))
            {
                TextBlock informationTextBlock = new TextBlock();
                informationTextBlock.Foreground = new SolidColorBrush(Colors.Blue);
                informationTextBlock.TextWrapping = TextWrapping.Wrap;
                string[] split = information.Split("Information");
                if (split[1].Contains(" : ", StringComparison.OrdinalIgnoreCase))
                {
                    string[] removeColon = split[1].Split(" : ");
                    informationTextBlock.Text = removeColon[1];
                }
                else
                {
                    informationTextBlock.Text = split[1];
                }

                this.informationLogs.Add(informationTextBlock);

                if (this.LogInformationFlyoutItem.IsChecked)
                {
                    this.ChangeLogStackPanel.Children.Add(informationTextBlock);
                }

                return true;
            }

            return false;
        }

        private bool LogNoise(string noise)
        {
            if (noise.Contains("Noise", StringComparison.OrdinalIgnoreCase))
            {
                TextBlock noiseTextBlock = new TextBlock();
                noiseTextBlock.Foreground = new SolidColorBrush(Colors.Gray);
                noiseTextBlock.TextWrapping = TextWrapping.Wrap;
                string[] split = noise.Split("Noise");
                noiseTextBlock.Text = split[1];

                this.noiseLogs.Add(noiseTextBlock);

                if (this.LogNoiseFlyoutItem.IsChecked)
                {
                    this.ChangeLogStackPanel.Children.Add(noiseTextBlock);
                }

                return true;
            }

            return false;
        }

        private bool LogErrors(string error)
        {
            if (error.Contains("Error", StringComparison.OrdinalIgnoreCase))
            {
                TextBlock errorTextBlock = new TextBlock();
                errorTextBlock.Foreground = new SolidColorBrush(Colors.Red);
                errorTextBlock.TextWrapping = TextWrapping.Wrap;
                string[] split = error.Split("Error");
                errorTextBlock.Text = split[1];

                this.errorLogs.Add(errorTextBlock);

                if (this.LogErrorFlyoutItem.IsChecked)
                {
                    this.ChangeLogStackPanel.Children.Add(errorTextBlock);
                }

                return true;
            }

            return false;
        }

        private async void WriteLog()
        {
            var dispatcher = this.dispatcher;
            await this.dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                int nextLogIndex = this.logBufferIndex;
                for (; nextLogIndex < this.logger.LogBuffer.Count; nextLogIndex++)
                {
                    var text = this.logger.LogBuffer[nextLogIndex];

                    if (this.LogInformation(text))
                    {
                    }
                    else if (this.LogErrors(text))
                    {
                    }
                    else
                    {
                        this.LogNoise(text);
                    }
                }

                this.logBufferIndex = nextLogIndex;

                this.ChangeLogScrollViewer.ChangeView(0.0f, double.MaxValue, 1.0f);
            });
        }

        private async void FilterLogs()
        {
            await this.dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                foreach (TextBlock textBlock in this.informationLogs)
                {
                    textBlock.Visibility = this.LogInformationFlyoutItem.IsChecked ? Visibility.Visible : Visibility.Collapsed;
                }

                foreach (TextBlock textBlock in this.noiseLogs)
                {
                    textBlock.Visibility = this.LogNoiseFlyoutItem.IsChecked ? Visibility.Visible : Visibility.Collapsed;
                }

                foreach (TextBlock textBlock in this.errorLogs)
                {
                    textBlock.Visibility = this.LogErrorFlyoutItem.IsChecked ? Visibility.Visible : Visibility.Collapsed;
                }

                this.ChangeLogScrollViewer.ChangeView(0.0f, double.MaxValue, 1.0f);
            });
        }

        private async void OpenConfigClick(object o, RoutedEventArgs e)
        {
            _ = o;
            _ = e;

            // Add FileSystemWatcher to watch config file. If changed set configmodified to true.
            using (FileSystemWatcher watcher = new FileSystemWatcher())
            {
                watcher.Path = Directory.GetCurrentDirectory();
                watcher.NotifyFilter = NotifyFilters.LastWrite;

                watcher.Filter = "*.json";

                string fileName = "config.json";
                var file = await ApplicationData.Current.LocalFolder.GetFileAsync(fileName);

                if (file.Path != null)
                {
                    await Launcher.LaunchFileAsync(file);
                    this.logger.Log("Config file opened");
                    this.logger.Log("Click Load Config to use modified values");
                }

                this.configModified = true;
            }
        }

        private async void LoadConfigClick(object sender, RoutedEventArgs e)
        {
            var configFile = await ApplicationData.Current.LocalFolder.GetFileAsync("config.json");

            AppSettings appSettings = await AppSettings.Load(configFile);

            var speechKeyModified = LocalSettingsHelper.SpeechSubscriptionKey != appSettings.SpeechSubscriptionKey;
            var speechRegionModified = LocalSettingsHelper.AzureRegion != appSettings.AzureRegion;
            var customSpeechIdModified = LocalSettingsHelper.CustomSpeechId != appSettings.CustomSpeechId;
            var customVoiceIdModified = LocalSettingsHelper.CustomVoiceIds != appSettings.CustomVoiceIds;
            var customCommandsAppIdModified = LocalSettingsHelper.CustomCommandsAppId != appSettings.CustomCommandsAppId;
            var botIdModified = LocalSettingsHelper.BotId != appSettings.BotId;
            var keywordDisplayNameModified = LocalSettingsHelper.KeywordDisplayName != appSettings.KeywordActivationModel.DisplayName;
            var keywordIdModified = LocalSettingsHelper.KeywordId != appSettings.KeywordActivationModel.KeywordId;
            var keywordModelIdModified = LocalSettingsHelper.KeywordModelId != appSettings.KeywordActivationModel.ModelId;
            var keywordActivationModelDataFormatModified = LocalSettingsHelper.KeywordActivationModelDataFormat != appSettings.KeywordActivationModel.ModelDataFormat;
            var keywordActivationModelPathModified = LocalSettingsHelper.KeywordActivationModelPath != appSettings.KeywordActivationModel.Path;
            var keywordConfirmationModelPathModified = LocalSettingsHelper.KeywordConfirmationModelPath != appSettings.KeywordModel;

            this.configModified = speechKeyModified || speechRegionModified || customSpeechIdModified ||
                customVoiceIdModified || customCommandsAppIdModified || botIdModified ||
                keywordDisplayNameModified || keywordIdModified || keywordModelIdModified ||
                keywordActivationModelDataFormatModified || keywordActivationModelPathModified || keywordConfirmationModelPathModified;

            if (this.configModified)
            {
                this.logger.Log("Configuration file has been modified");

                if (speechKeyModified)
                {
                    LocalSettingsHelper.SpeechSubscriptionKey = appSettings.SpeechSubscriptionKey;
                    this.logger.Log($"Speech Key: {LocalSettingsHelper.SpeechSubscriptionKey}");
                }

                if (speechRegionModified)
                {
                    LocalSettingsHelper.AzureRegion = appSettings.AzureRegion;
                    this.logger.Log($"Azure Region: {LocalSettingsHelper.AzureRegion}");
                }

                if (customSpeechIdModified)
                {
                    LocalSettingsHelper.CustomSpeechId = appSettings.CustomSpeechId;
                    this.logger.Log($"Custom Speech Id: {LocalSettingsHelper.CustomSpeechId}");
                }

                if (customVoiceIdModified)
                {
                    LocalSettingsHelper.CustomVoiceIds = appSettings.CustomVoiceIds;
                    this.logger.Log($"Custom Voice Id: {LocalSettingsHelper.CustomVoiceIds}");
                }

                if (customCommandsAppIdModified)
                {
                    LocalSettingsHelper.CustomCommandsAppId = appSettings.CustomCommandsAppId;
                    this.logger.Log($"Custom Commands App Id: {LocalSettingsHelper.CustomCommandsAppId}");
                }

                if (botIdModified)
                {
                    LocalSettingsHelper.BotId = appSettings.BotId;
                    this.logger.Log($"Bot Id: {LocalSettingsHelper.BotId}");
                }

                if (keywordDisplayNameModified)
                {
                    LocalSettingsHelper.KeywordDisplayName = appSettings.KeywordActivationModel.DisplayName;
                    this.logger.Log($"Keyword Display Name: {LocalSettingsHelper.KeywordDisplayName}");
                }

                if (keywordIdModified)
                {
                    LocalSettingsHelper.KeywordId = appSettings.KeywordActivationModel.KeywordId;
                    this.logger.Log($"Keyword Id: {LocalSettingsHelper.KeywordId}");
                }

                if (keywordModelIdModified)
                {
                    LocalSettingsHelper.KeywordModelId = appSettings.KeywordActivationModel.ModelId;
                    this.logger.Log($"Keyword Model Id: {LocalSettingsHelper.KeywordModelId}");
                }

                if (keywordActivationModelDataFormatModified)
                {
                    LocalSettingsHelper.KeywordActivationModelDataFormat = appSettings.KeywordActivationModel.ModelDataFormat;
                    this.logger.Log($"Keyword Activation Model Data Format: {LocalSettingsHelper.KeywordActivationModelDataFormat}");
                }

                if (keywordActivationModelPathModified)
                {
                    LocalSettingsHelper.KeywordActivationModelPath = appSettings.KeywordActivationModel.Path;
                    this.logger.Log($"Keyword Activation Model Path: {LocalSettingsHelper.KeywordActivationModelPath}");
                }

                if (keywordConfirmationModelPathModified)
                {
                    LocalSettingsHelper.KeywordConfirmationModelPath = appSettings.KeywordModel;
                    this.logger.Log($"Keyword Confirmation Model Path: {LocalSettingsHelper.KeywordConfirmationModelPath}");
                }

                if (keywordActivationModelDataFormatModified
                    || keywordActivationModelPathModified
                    || keywordConfirmationModelPathModified
                    || keywordDisplayNameModified
                    || keywordIdModified
                    || keywordModelIdModified)
                {
                    await this.keywordRegistration.CreateKeywordConfigurationAsync();
                }
            }
            else
            {
                this.logger.Log("No changes in config");
            }
        }

        private void CollapseControls(object sender, RoutedEventArgs e)
        {
            this.WindowsContolFlyoutItem.IsChecked = false;
            this.ToggleControls(sender, e);
        }

        private void CollapseLogs(object sender, RoutedEventArgs e)
        {
            this.WindowsLogFlyoutItem.IsChecked = false;
            this.ToggleControls(sender, e);
        }

        private void ToggleControls(object sender, RoutedEventArgs e)
        {
            if (this.WindowsContolFlyoutItem.IsChecked && this.WindowsLogFlyoutItem.IsChecked && !this.WindowsChatFlyoutItem.IsChecked)
            {
                this.ControlsGrid.Visibility = Visibility.Visible;
                this.LogGrid.Visibility = Visibility.Visible;
                this.ChatGrid.Visibility = Visibility.Collapsed;
                var logGridMargin = this.LogGrid.Margin;
                logGridMargin.Top = 0;
                this.LogGrid.Margin = logGridMargin;
                var controlsGridMargin = this.ControlsGrid.Margin;
                controlsGridMargin.Top = 0;
                this.ControlsGrid.Margin = controlsGridMargin;
                Grid.SetColumn(this.ControlsGrid, 0);
                Grid.SetColumn(this.LogGrid, 1);
                Grid.SetRow(this.LogGrid, 2);
                Grid.SetColumn(this.ChatGrid, 2);
                Grid.SetRow(this.ChatGrid, 0);
                Grid.SetColumn(this.HelpButtonGrid, 1);
                Grid.SetColumnSpan(this.ApplicationStateGrid, 2);
                Grid.SetColumn(this.HelpButtonGrid, 1);
                Grid.SetRowSpan(this.ApplicationStateGrid, 1);
                Grid.SetRow(this.VoiceSettingsStackPanel, 0);
                Grid.SetColumn(this.VoiceSettingsStackPanel, 0);
                Grid.SetRow(this.MicrophoneSettingsStackPanel, 0);
                Grid.SetColumn(this.MicrophoneSettingsStackPanel, 1);
                Grid.SetRow(this.ConversationStateStackPanel, 0);
                Grid.SetColumn(this.ConversationStateStackPanel, 2);
                Grid.SetRow(this.ApplicationStateBadgeStackPanel, 0);
                Grid.SetColumn(this.ApplicationStateBadgeStackPanel, 3);
                this.ApplicationStateBadgeStackPanel.HorizontalAlignment = HorizontalAlignment.Right;
                ApplicationView.GetForCurrentView().SetPreferredMinSize(new Windows.Foundation.Size { Width = ((int)this.ControlsGrid.ActualWidth) + ((int)this.LogGrid.ActualWidth), Height = 800 });
                ApplicationView.GetForCurrentView().TryResizeView(new Windows.Foundation.Size { Width = ((int)this.ControlsGrid.ActualWidth) + ((int)this.LogGrid.ActualWidth), Height = 800 });
            }

            if (this.WindowsContolFlyoutItem.IsChecked && !this.WindowsLogFlyoutItem.IsChecked && this.WindowsChatFlyoutItem.IsChecked)
            {
                this.ControlsGrid.Visibility = Visibility.Visible;
                this.LogGrid.Visibility = Visibility.Collapsed;
                this.ChatGrid.Visibility = Visibility.Visible;
                Grid.SetColumn(this.ControlsGrid, 0);
                Grid.SetRow(this.ControlsGrid, 2);
                Grid.SetColumn(this.ChatGrid, 1);
                Grid.SetRow(this.ChatGrid, 2);
                var margin = this.ChatGrid.Margin;
                margin.Top = 0;
                this.ChatGrid.Margin = margin;
                var controlsGridMargin = this.ControlsGrid.Margin;
                controlsGridMargin.Top = 0;
                this.ControlsGrid.Margin = controlsGridMargin;
                Grid.SetColumnSpan(this.ApplicationStateGrid, 2);
                Grid.SetRowSpan(this.ApplicationStateGrid, 1);
                Grid.SetRow(this.VoiceSettingsStackPanel, 0);
                Grid.SetColumn(this.VoiceSettingsStackPanel, 0);
                Grid.SetRow(this.MicrophoneSettingsStackPanel, 0);
                Grid.SetColumn(this.MicrophoneSettingsStackPanel, 1);
                Grid.SetRow(this.ConversationStateStackPanel, 0);
                Grid.SetColumn(this.ConversationStateStackPanel, 2);
                Grid.SetColumn(this.HelpButtonGrid, 1);
                Grid.SetRow(this.ApplicationStateBadgeStackPanel, 0);
                Grid.SetColumn(this.ApplicationStateBadgeStackPanel, 3);
                this.ApplicationStateBadgeStackPanel.HorizontalAlignment = HorizontalAlignment.Right;
                ApplicationView.GetForCurrentView().SetPreferredMinSize(new Windows.Foundation.Size { Width = ((int)this.ControlsGrid.ActualWidth) + ((int)this.ChatGrid.ActualWidth), Height = 800 });
                ApplicationView.GetForCurrentView().TryResizeView(new Windows.Foundation.Size { Width = ((int)this.ControlsGrid.ActualWidth) + ((int)this.ChatGrid.ActualWidth), Height = 800 });
            }

            if (!this.WindowsContolFlyoutItem.IsChecked && this.WindowsLogFlyoutItem.IsChecked && this.WindowsChatFlyoutItem.IsChecked)
            {
                this.ControlsGrid.Visibility = Visibility.Collapsed;
                this.LogGrid.Visibility = Visibility.Visible;
                this.ChatGrid.Visibility = Visibility.Visible;
                Grid.SetColumn(this.LogGrid, 0);
                Grid.SetRow(this.LogGrid, 2);
                Grid.SetColumn(this.ChatGrid, 1);
                Grid.SetRow(this.ChatGrid, 2);
                var chatGridMargin = this.ChatGrid.Margin;
                chatGridMargin.Top = 0;
                this.ChatGrid.Margin = chatGridMargin;
                var margin = this.LogGrid.Margin;
                margin.Top = 0;
                this.LogGrid.Margin = margin;
                Grid.SetColumnSpan(this.ApplicationStateGrid, 2);
                Grid.SetColumn(this.HelpButtonGrid, 1);
                Grid.SetRowSpan(this.ApplicationStateGrid, 1);
                Grid.SetRow(this.VoiceSettingsStackPanel, 0);
                Grid.SetColumn(this.VoiceSettingsStackPanel, 0);
                Grid.SetRow(this.MicrophoneSettingsStackPanel, 0);
                Grid.SetColumn(this.MicrophoneSettingsStackPanel, 1);
                Grid.SetRow(this.ConversationStateStackPanel, 0);
                Grid.SetColumn(this.ConversationStateStackPanel, 2);
                Grid.SetRow(this.ApplicationStateBadgeStackPanel, 0);
                Grid.SetColumn(this.ApplicationStateBadgeStackPanel, 3);
                this.ApplicationStateBadgeStackPanel.HorizontalAlignment = HorizontalAlignment.Right;
                var chatAndLogGrid = ((int)this.ChatGrid.ActualWidth) + ((int)this.LogGrid.ActualWidth);
                ApplicationView.GetForCurrentView().SetPreferredMinSize(new Windows.Foundation.Size { Width = chatAndLogGrid, Height = 800 });
                ApplicationView.GetForCurrentView().TryResizeView(new Windows.Foundation.Size { Width = chatAndLogGrid, Height = 800 });
            }

            if (!this.WindowsContolFlyoutItem.IsChecked && !this.WindowsLogFlyoutItem.IsChecked && this.WindowsChatFlyoutItem.IsChecked)
            {
                this.ControlsGrid.Visibility = Visibility.Collapsed;
                this.LogGrid.Visibility = Visibility.Collapsed;
                this.ChatGrid.Visibility = Visibility.Visible;
                Grid.SetColumn(this.ChatGrid, 0);
                Grid.SetRow(this.ChatGrid, 2);
                var margin = this.ChatGrid.Margin;
                margin.Top = 100;
                this.ChatGrid.Margin = margin;
                Grid.SetColumnSpan(this.ChatGrid, 1);
                Grid.SetColumn(this.ApplicationStateGrid, 0);
                Grid.SetColumn(this.HelpButtonGrid, 0);
                Grid.SetRowSpan(this.ApplicationStateGrid, 3);
                Grid.SetRow(this.VoiceSettingsStackPanel, 0);
                Grid.SetColumn(this.VoiceSettingsStackPanel, 0);
                Grid.SetRow(this.MicrophoneSettingsStackPanel, 1);
                Grid.SetColumn(this.MicrophoneSettingsStackPanel, 0);
                Grid.SetRow(this.ConversationStateStackPanel, 2);
                Grid.SetColumn(this.ConversationStateStackPanel, 0);
                this.ChatGrid.HorizontalAlignment = HorizontalAlignment.Center;
                Grid.SetRow(this.ApplicationStateBadgeStackPanel, 0);
                Grid.SetColumn(this.ApplicationStateBadgeStackPanel, 3);
                this.ApplicationStateBadgeStackPanel.HorizontalAlignment = HorizontalAlignment.Right;
                ApplicationView.GetForCurrentView().SetPreferredMinSize(new Windows.Foundation.Size { Width = (int)this.ChatGrid.ActualWidth - 10, Height = 800 });
                ApplicationView.GetForCurrentView().TryResizeView(new Windows.Foundation.Size { Width = (int)this.ChatGrid.ActualWidth, Height = 800 });
            }

            if (this.WindowsContolFlyoutItem.IsChecked && this.WindowsLogFlyoutItem.IsChecked && this.WindowsChatFlyoutItem.IsChecked)
            {
                this.ControlsGrid.Visibility = Visibility.Visible;
                this.LogGrid.Visibility = Visibility.Visible;
                this.ChatGrid.Visibility = Visibility.Visible;
                var controlsGridMargin = this.ControlsGrid.Margin;
                controlsGridMargin.Top = 0;
                this.ControlsGrid.Margin = controlsGridMargin;
                var chatGridMargin = this.ChatGrid.Margin;
                chatGridMargin.Top = 0;
                this.ChatGrid.Margin = chatGridMargin;
                Grid.SetColumn(this.ControlsGrid, 0);
                Grid.SetRow(this.ControlsGrid, 2);
                Grid.SetColumn(this.LogGrid, 1);
                Grid.SetRow(this.LogGrid, 2);
                Grid.SetColumn(this.ChatGrid, 2);
                Grid.SetRow(this.ChatGrid, 0);
                Grid.SetRow(this.ApplicationStateGrid, 1);
                Grid.SetColumnSpan(this.ApplicationStateGrid, 2);
                Grid.SetColumn(this.HelpButtonGrid, 1);
                Grid.SetRow(this.ApplicationStateBadgeStackPanel, 0);
                Grid.SetColumn(this.ApplicationStateBadgeStackPanel, 3);
                this.ApplicationStateBadgeStackPanel.HorizontalAlignment = HorizontalAlignment.Right;
                ApplicationView.GetForCurrentView().SetPreferredMinSize(new Windows.Foundation.Size { Width = ((int)this.ControlsGrid.ActualWidth) + ((int)this.ChatGrid.ActualWidth) + ((int)this.LogGrid.ActualWidth), Height = 800 });
                ApplicationView.GetForCurrentView().TryResizeView(new Windows.Foundation.Size { Width = ((int)this.ControlsGrid.ActualWidth) + ((int)this.ChatGrid.ActualWidth) + ((int)this.LogGrid.ActualWidth), Height = 800 });
            }

            if (!this.WindowsContolFlyoutItem.IsChecked && !this.WindowsLogFlyoutItem.IsChecked && !this.WindowsChatFlyoutItem.IsChecked)
            {
                this.ControlsGrid.Visibility = Visibility.Collapsed;
                this.LogGrid.Visibility = Visibility.Collapsed;
                this.ChatGrid.Visibility = Visibility.Collapsed;
                Grid.SetColumnSpan(this.ApplicationStateGrid, 2);
                Grid.SetColumn(this.HelpButtonGrid, 1);
                Grid.SetRowSpan(this.ApplicationStateGrid, 2);
                Grid.SetRow(this.VoiceSettingsStackPanel, 0);
                Grid.SetColumn(this.VoiceSettingsStackPanel, 0);
                Grid.SetRow(this.MicrophoneSettingsStackPanel, 0);
                Grid.SetColumn(this.MicrophoneSettingsStackPanel, 1);
                Grid.SetRow(this.ConversationStateStackPanel, 0);
                Grid.SetColumn(this.ConversationStateStackPanel, 2);
                Grid.SetRow(this.ApplicationStateBadgeStackPanel, 1);
                Grid.SetColumn(this.ApplicationStateBadgeStackPanel, 0);
                this.ApplicationStateBadgeStackPanel.HorizontalAlignment = HorizontalAlignment.Left;
                ApplicationView.GetForCurrentView().SetPreferredMinSize(new Windows.Foundation.Size { Width = 632, Height = 125 });
                ApplicationView.GetForCurrentView().TryResizeView(new Windows.Foundation.Size { Width = 632, Height = 125 });
            }

            if (this.WindowsContolFlyoutItem.IsChecked && !this.WindowsLogFlyoutItem.IsChecked && !this.WindowsChatFlyoutItem.IsChecked)
            {
                this.LogGrid.Visibility = Visibility.Collapsed;
                this.ChatGrid.Visibility = Visibility.Collapsed;
                this.ControlsGrid.Visibility = Visibility.Visible;
                var margin = this.ControlsGrid.Margin;
                margin.Top = 90;
                this.ControlsGrid.Margin = margin;
                Grid.SetColumn(this.ControlsGrid, 0);
                Grid.SetColumn(this.ApplicationStateGrid, 0);
                Grid.SetColumn(this.HelpButtonGrid, 0);
                Grid.SetRowSpan(this.ApplicationStateGrid, 3);
                Grid.SetRow(this.VoiceSettingsStackPanel, 0);
                Grid.SetColumn(this.VoiceSettingsStackPanel, 0);
                Grid.SetRow(this.MicrophoneSettingsStackPanel, 1);
                Grid.SetColumn(this.MicrophoneSettingsStackPanel, 0);
                Grid.SetRow(this.ConversationStateStackPanel, 2);
                Grid.SetColumn(this.ConversationStateStackPanel, 0);
                Grid.SetRow(this.ApplicationStateBadgeStackPanel, 3);
                Grid.SetColumn(this.ApplicationStateBadgeStackPanel, 0);
                this.ApplicationStateBadgeStackPanel.HorizontalAlignment = HorizontalAlignment.Right;
                ApplicationView.GetForCurrentView().SetPreferredMinSize(new Windows.Foundation.Size { Width = (int)this.ControlsGrid.ActualWidth, Height = 800 });
                ApplicationView.GetForCurrentView().TryResizeView(new Windows.Foundation.Size { Width = (int)this.ControlsGrid.ActualWidth, Height = 800 });
            }

            if (!this.WindowsContolFlyoutItem.IsChecked && this.WindowsLogFlyoutItem.IsChecked && !this.WindowsChatFlyoutItem.IsChecked)
            {
                this.ControlsGrid.Visibility = Visibility.Collapsed;
                this.LogGrid.Visibility = Visibility.Visible;
                this.ChatGrid.Visibility = Visibility.Collapsed;
                var margin = this.LogGrid.Margin;
                margin.Top = 90;
                this.LogGrid.Margin = margin;
                Grid.SetColumn(this.LogGrid, 0);
                Grid.SetColumn(this.ApplicationStateGrid, 0);
                Grid.SetColumn(this.HelpButtonGrid, 0);
                Grid.SetRowSpan(this.ApplicationStateGrid, 3);
                Grid.SetRow(this.VoiceSettingsStackPanel, 0);
                Grid.SetColumn(this.VoiceSettingsStackPanel, 0);
                Grid.SetRow(this.MicrophoneSettingsStackPanel, 1);
                Grid.SetColumn(this.MicrophoneSettingsStackPanel, 0);
                Grid.SetRow(this.ConversationStateStackPanel, 2);
                Grid.SetColumn(this.ConversationStateStackPanel, 0);
                Grid.SetRow(this.ApplicationStateBadgeStackPanel, 0);
                Grid.SetColumn(this.ApplicationStateBadgeStackPanel, 3);
                this.ApplicationStateBadgeStackPanel.HorizontalAlignment = HorizontalAlignment.Right;
                ApplicationView.GetForCurrentView().SetPreferredMinSize(new Windows.Foundation.Size { Width = (int)this.LogGrid.ActualWidth, Height = 800 });
                ApplicationView.GetForCurrentView().TryResizeView(new Windows.Foundation.Size { Width = (int)this.LogGrid.ActualWidth, Height = 800 });
            }
        }

        private async void HelpFlyoutItemClick(object sender, RoutedEventArgs e)
        {
            string githubReadme = @"https://github.com/Azure-Samples/Cognitive-Services-Voice-Assistant/blob/master/clients/csharp-uwp/README.md";

            var uri = new Uri(githubReadme);

            await Launcher.LaunchUriAsync(uri);
        }

        private async void DocumentationFlyoutItemClick(object sender, RoutedEventArgs e)
        {
            string mvaDocumentation = @"https://docs.microsoft.com/en-us/uwp/api/windows.applicationmodel.conversationalagent?view=winrt-18362";

            var uri = new Uri(mvaDocumentation);

            await Launcher.LaunchUriAsync(uri);
        }

        private void OutputFormatComboBox_SelectionChanged(object s, SelectionChangedEventArgs e)
        {
            _ = s;
            _ = e;

            var selectedLabel = this.OutputFormatComboBox.SelectedItem.ToString();
            var selectedFormat = DialogAudio.GetMatchFromLabel(selectedLabel);
            LocalSettingsHelper.OutputFormat = selectedFormat;
        }

        private void OnChatHistoryListViewContainerChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.InRecycleQueue)
            {
                return;
            }

            Conversation message = (Conversation)args.Item;
            args.ItemContainer.HorizontalAlignment = message.Sent ? Windows.UI.Xaml.HorizontalAlignment.Right : Windows.UI.Xaml.HorizontalAlignment.Left;
        }

        private async void DownloadChatHistoryClick(object sender, RoutedEventArgs e)
        {
            var localFolder = ApplicationData.Current.LocalFolder;
            var fileName = $"chatHistory_{DateTime.Now.ToString("yyyyMMdd_HHmmss", null)}.txt";
            var writeChatHistory = await localFolder.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting);

            foreach (var message in this.conversations)
            {
                await File.AppendAllTextAsync(writeChatHistory.Path, "\r\n" + "Text: " + message.Body + "\r\n" + "BotReply: " + message.Received + "\r\n" + "Timestamp: " + message.Time + "\r\n" + "========");
            }

            var launchOption = new FolderLauncherOptions();

            var fileToSelect = await localFolder.GetFileAsync(fileName);
            launchOption.ItemsToSelect.Add(fileToSelect);

            await Launcher.LaunchFolderAsync(localFolder, launchOption);
        }

        private async void TriggerLogAvailable(object sender, RoutedEventArgs e)
        {
            await this.dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
            {
                this.FilterLogs();
            });
        }

        private void ApplicationStateBadgeClick(object sender, RoutedEventArgs e)
        {
            this.ApplicationStateTeachingTip.IsOpen = true;
        }

        private async void TextInputTextBoxKeyUp(object sender, Windows.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key != VirtualKey.Enter)
            {
                return;
            }

            var message = this.TextInputTextBox.Text;
            this.TextInputTextBox.Text = string.Empty;
            this.AddMessageToStatus(message);
            await this.dialogManager.SendActivityAsync(message);
        }
    }
}
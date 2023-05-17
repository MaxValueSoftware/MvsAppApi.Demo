using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using System.Xml;
using DemoUI.Views;
using Microsoft.Win32;
using MvsAppApi.Core;
using MvsAppApi.Core.Enums;
using MvsAppApi.Core.Structs;
using Newtonsoft.Json;
using Prism.Commands;
using Serilog;
using Point = MvsAppApi.Core.Structs.Point;

namespace DemoUI.ViewModels
{
    public sealed class MainWindowViewModel : INotifyPropertyChanged
    {
        public class ImportHandSiteData
        {
            public int Id { get; set; }
            public string Value { get; set; }
        }

        private const int PokerStarsSiteId = 100;
        private const int PartyPokerSiteId = 200;
        private const int StandardizedHandsSiteId = 3000; // not a site but oh well

        private bool _sendingBrokenRequests;
        private bool _breakStatValues;
        private bool _delayStatValues;

        public bool ShouldClose { get; set; }

        public List<ImportHandSiteData> ImportHandSites
        {
            get
            {
                var list = new List<ImportHandSiteData>
                {
                    new ImportHandSiteData {Id = PokerStarsSiteId, Value = "PokerStars"},
                    new ImportHandSiteData {Id = PartyPokerSiteId, Value = "PartyPoker"},
                    new ImportHandSiteData {Id = StandardizedHandsSiteId, Value = "StandardizedHands"}
                };
                return list;
            }
        }

        #region private

        private string AppId { get; set; } = "[insert your app id here]";
        private string AppName { get; set; } = "[insert your app name here]";

        private const int MaxInbound = 4;  // server pipes
        private const int MaxOutbound = 1; // client pipes
        private bool _isAttached;
        private bool _isSleeping;
        private bool _isBusy;
        private bool _isFullDetails;
        private bool _isCash;
        private bool _includeExtraStats;
        private bool _needsStats = true;

        // user interface
        private bool _isVisible = true;
        private Dictionary<string, ICommand> _apiCommands;
        private const string RequestLabel = "Request: ";

        #endregion


        private TimeSpan _totalClientDuration;
        private long _totalClientResponses;

        private string ClientResponseLine(TimeSpan span, string response)
        {
            _totalClientResponses++;
            _totalClientDuration += span;
            ClientStatus = @"Tracker's avg. response time: " + _totalClientDuration.TotalMilliseconds / _totalClientResponses + @"ms.";
            return $"Response({span.TotalMilliseconds}ms): {response}";
        }

        #region private methods

        #endregion

        private bool _attachedOnce;
        private const string DefaultApiVersion = "1.2";


        private string SelectStatsOrFiltersTableType
        {
            get
            {
                switch (SelectStatsOrFiltersTableTypeIndex)
                {
                    case 0:
                        return "cash";
                    case 1:
                        return "tournament";
                    default:
                        return "both";
                }
            }
        }

        public Action CloseAction { get; set; }

        private readonly IAdapter _adapter;
        private readonly Dispatcher _dispatcher;
        public MainWindowViewModel(IAdapter adapter)
        {
            _dispatcher = Application.Current.Dispatcher;
            _adapter = adapter;

            var args = Environment.GetCommandLineArgs();

            Tracker = args.Any(a => a.Equals("--tracker=pt4")) ? Tracker.PT4 : Tracker.HM3;

            ApiVersion = DefaultApiVersion;
            var apiVersionArg = args.FirstOrDefault(a => a.StartsWith("--apiversion="));
            if (!string.IsNullOrEmpty(apiVersionArg))
                ApiVersion = apiVersionArg.Split('=')[1];

            var logsFolder = @".\";
            var logsFolderArg = args.FirstOrDefault(a => a.StartsWith("--log_directory="));
            if (!string.IsNullOrEmpty(logsFolderArg))
                logsFolder = logsFolderArg.Split('=')[1];

            var appId = args.FirstOrDefault(a => a.StartsWith("--appId="));
            if (!string.IsNullOrEmpty(appId))
                AppId = appId.Split('=')[1];

            var appName = args.FirstOrDefault(a => a.StartsWith("--appName="));
            if (!string.IsNullOrEmpty(appName))
                AppName = appName.Split('=')[1].Replace("\"", "");

            var logFile = Path.Combine(logsFolder, "ApiDemo.log");
            var template = "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{ThreadID}] {Message:lj}{NewLine}{Exception}";

            Log.Logger = new LoggerConfiguration()
                .Enrich.With(new ThreadIdEnricher())
                .MinimumLevel.Debug()
                .WriteTo.File(logFile, rollingInterval: RollingInterval.Day, outputTemplate: template)
                .CreateLogger();

            Log.Information($@"App started.");
            Log.Information($@"Logging to {logFile}, tracker is {Tracker}, API {ApiVersion}");
            Log.Information($@"AppName is {AppName}, AppId is {AppId}");
        }

        private void AddClientText(string text)
        {
            var msg = "Client: " + text;
            LogMessage(msg);
        }

        private bool DoLogCallback(string msg)
        {
            LogMessage(msg);
            return true;
        }

        private void LogMessage(string txt)
        {
            Log.Information(txt);
            ClientText += $"{DateTime.Now}: {txt}{Environment.NewLine}";
        }

        private string DoConnectHash(string salt)
        {
            Console.WriteLine(salt);
            var hashAlgorithm = (HashAlgorithm)SHA512.Create();
            return Hash.Calculate(hashAlgorithm, AppId, salt);
        }

        private bool DoConnectInfo(string rootDir, string dataDir, string logDir, Restriction[] restrictions, bool isTrial, string expires, bool isSleeping, string email, string trackerVersion, string apiVersion)
        {
            restrictions = restrictions ?? Array.Empty<Restriction>();
            LogMessage($@"ConnectInfo: trackerVersion={trackerVersion}, apiVersion={ApiVersion}");
            LogMessage($@"ConnectInfo: isTrial={isTrial}, expires={expires}, isSleeping={isSleeping}, email={email}");
            LogMessage($@"ConnectInfo: rootDir={rootDir}");
            LogMessage($@"ConnectInfo: dataDir={dataDir}");
            LogMessage($@"ConnectInfo: logDir={logDir}");
            if (restrictions.Length > 0)
            {
                LogMessage($@"ConnectInfo: restrictions ...");
                foreach (var restriction in restrictions)
                    LogMessage($@"                 Name={restriction.Name},Value={restriction.Value},Type={restriction.Type}, Units={restriction.Units}");
            }
            LogMessage($@"ConnectInfo: restrictions.Length={restrictions.Length}");
            return true;
        }


        public void Attach()
        {
            var profile = new Profile
            {
                Tracker = Tracker,
                MaxOutbound = MaxOutbound,
                MaxInbound = MaxInbound,
                AppName = AppName,
                AppVersion = CurrentVersion,
                ApiVersion = ApiVersion,
                AppId = AppId,
                // optional callbacks
                MenuSelectedCallback = DoMenuSelected,
                HandCallback = DoHandCallback,
                HandsSelectedCallback = DoHandsSelectedCallback,
                TablesCallback = DoTablesCallback,
                ImportStartedCallback = DoImportStartedCallback,
                ImportStoppedCallback = DoImportStoppedCallback,
                NotesCallback = DoNotesCallback,
                TagsCallback = DoTagsCallback,
                StatValueCallback = DoStatValueCallback,
                StatPreviewCallback = DoStatPreviewCallback,
                CallbackCallback = DoCallbackCallback,
                NoteTabValueCallback = DoNoteTabValueCallback,
                NoteHandsCallback = DoNoteHandsCallback,
                SettingsChangedCallback = DoSettingsChangedCallback,
                LicenseChangedCallback = DoLicenseChangedCallback,
                StatsChangedCallback = DoStatsChangedCallback,
                SleepBeginCallback = DoSleepBeginCallback,
                SleepEndCallback = DoSleepEndCallback,
                HasUnsavedChangesCallback = DoHasUnsavedChangesCallback,
                ReplayHandCallback = DoReplayHandCallback,
                NoopCallback = DoNoop
            };
            // connect (i.e. create/register/verify inbound pipes)
            var success = _adapter.Connect(profile, DoLogCallback, DoQuitCallback, DoConnectHash, DoConnectInfo);

            if (!success)
            {
                Close();
                return;
            }

            IsAttached = true;

            _adapter.BusyStateBegin();

            // request hands, tables, register menus and note tabs

            RequestHandsCommand.Execute(null);
            if (!_lastRequestHandsCommandSuccess)
                LogMessage("Error requesting hands.");

            RequestTablesCommand.Execute(null);
            if (!_lastRequestTablesCommandSuccess)
                LogMessage("Error requesting tables.");

            // temp: stay busy for an extra 2 seconds
            Thread.Sleep(2000);

            RegisterMenuCommand.Execute(null);
            if (!_lastRegisterMenuCommandSuccess)
                LogMessage("Error registering menu.");

            RegisterNoteTabsCommand.Execute(null);
            if (!_lastRegisterNoteTabsCommandSuccess)
                LogMessage("Error registering tabs.");

            RegisterHandsMenuCommand.Execute(null);
            if (!_lastRegisterHandsMenuCommandSuccess)
                LogMessage("Error registering tabs.");

            if (!GetSetting("active_database_alias", out var dbName))
                LogMessage("Error getting active database setting.");

            CurrentDatabaseName = dbName.ToString();

            if (!(GetSetting("active_player", out var value) && value is CurrentPlayerInfo))
                LogMessage("Error getting active player setting.");
            else
            {
                var activePlayer = (CurrentPlayerInfo)value;
                CurrentPlayerName = activePlayer.PlayerName;
                CurrentPlayerSite = string.IsNullOrEmpty(activePlayer.SiteId) ? 0 : Convert.ToInt32(activePlayer.SiteId);
            }

            if (!GetSetting("system_dpi", out var dpi))
                LogMessage("Error getting hud system dpi setting.");

            HudSystemDpi = Convert.ToDouble(dpi);

            if (!GetSetting("import_started", out var importStarted))
                LogMessage("Error getting import_started setting.");

            ImportStarted = importStarted.ToString();

            if (!GetSetting("hand_tags", out var handTagsObj))
                LogMessage("Error getting hand_tags setting.");
            else
            {
                var handTags = handTagsObj as SettingHandTag[];
                if (handTags != null)
                {
                    LogMessage("HandTags:");
                    foreach (var handTag in handTags)
                        LogMessage($@"    {handTag.Name}: img_checked={handTag.ImgChecked}, img_unchecked={handTag.ImgUnchecked}");
                }
            }

            if (Tracker == Tracker.HM3 && !GetSetting("theme", out var theme))
                LogMessage("Error getting theme setting.");

            if (Tracker == Tracker.HM3 && !GetSetting("dark_mode", out var darkMode))
                LogMessage("Error getting dark_mode setting.");

            // setup a default filter for stats queries (depends on tracker and api version)
            var filterObj = Tracker == Tracker.PT4
                ? new FilterObject
                {
                    name = "Actions and Opportunities - Preflop - Voluntarily Put Money In Pot",
                    description = "Voluntarily Put Money in Pot"
                }
                : new FilterObject { name = "@DidVPIP = true" };
            var filter = JsonConvert.SerializeObject(filterObj);
            var filters = "{\"filters\": [ " + filter + "]}";     // filters is a filter_group object 

            StatQueryFilters = filters;

            // setup some default stats for stat queries (depends on the tracker)
            StatQueryStats = StatQueryDefaultStats = Tracker == Tracker.HM3
                ? "VPIP,PFR,ThreeBet" : "VPIP";

            // at startup, show it loading briefly (like a splashscreen) then hide it
            Thread.Sleep(1000);
            if (!_attachedOnce)
                HideCommand.Execute(null);
            _attachedOnce = true;

            _adapter.BusyStateEnd();
        }

        public string ApiVersion { get; set; }

        // trackers

        public Tracker Tracker { get; set; }
        public bool IsHm3 => Tracker == Tracker.HM3;

        public bool BreakStatValues
        {
            get => _breakStatValues;
            set
            {
                if (_breakStatValues == value) return;

                _breakStatValues = value;
                _adapter.BreakStatValues = value;

                RaisePropertyChanged();
            }
        }

        public bool DelayStatValues
        {
            get => _delayStatValues;
            set
            {
                if (_delayStatValues == value) return;

                _delayStatValues = value;

                RaisePropertyChanged();
            }
        }

        public bool IsAttached
        {
            get => _isAttached;
            set
            {
                if (_isAttached == value) return;

                _isAttached = value;

                RaisePropertyChanged();
            }
        }

        public bool DisableUnsavedChangesSupport
        {
            get => _disableUnsavedChangesSupport;
            set
            {
                if (_disableUnsavedChangesSupport == value) return;

                _disableUnsavedChangesSupport = value;
                _adapter.DisableUnsavedChangesSupport = value;

                RaisePropertyChanged();
            }
        }

        public bool IsSleeping
        {
            get => _isSleeping;
            set
            {
                if (_isSleeping == value) return;

                _isSleeping = value;

                RaisePropertyChanged();
            }
        }


        private string _currentDatabaseName = "unknown";

        public string CurrentDatabaseName
        {
            get => _currentDatabaseName;
            set
            {
                if (_currentDatabaseName == value) return;

                _currentDatabaseName = value;

                RaisePropertyChanged();
                RaisePropertyChanged("WindowTitle");
            }
        }

        private string _currentPlayerName = "unknown";

        public string CurrentPlayerName
        {
            get => _currentPlayerName;
            set
            {
                if (_currentPlayerName == value) return;

                // default the players list on stat query tab to the current player
                if (string.IsNullOrEmpty(StatQueryPlayers) || _currentPlayerName.Equals("unknown"))
                    StatQueryPlayers = value;

                _currentPlayerName = value;

                RaisePropertyChanged();
                RaisePropertyChanged("WindowTitle");
            }
        }

        private int _currentPlayerSite = -1;

        public int CurrentPlayerSite
        {
            get => _currentPlayerSite;
            set
            {
                if (_currentPlayerSite == value) return;

                // default the site id on stat query tab to the current player's site
                if (_currentPlayerSite == -1)
                    StatQuerySiteId = value;

                _currentPlayerSite = value;

                RaisePropertyChanged();
                RaisePropertyChanged("WindowTitle");
            }
        }

        private string _currentVersion;

        public string CurrentVersion
        {
            get
            {
                if (_currentVersion != null)
                    return _currentVersion;

                _currentVersion = Assembly.GetExecutingAssembly().GetName().Version.ToString();
                return _currentVersion;
            }
        }

        public string WindowTitle =>
            $"{AppName} - {CurrentVersion} (database:{CurrentDatabaseName}, player:{CurrentPlayerName}, site:{CurrentPlayerSite})";

        public bool IsBusy
        {
            get => _isBusy;
            set
            {
                if (_isBusy == value) return;

                _isBusy = value;

                if (_isBusy)
                    _adapter.BusyStateBegin();
                else
                    _adapter.BusyStateEnd();

                RaisePropertyChanged();
            }
        }

        public bool IsFullDetails
        {
            get => _isFullDetails;
            set
            {
                if (_isFullDetails == value) return;

                _isFullDetails = value;

                RaisePropertyChanged();
            }
        }

        public bool IsCash
        {
            get => _isCash;
            set
            {
                if (_isCash == value) return;

                _isCash = value;

                RaisePropertyChanged();
            }
        }

        public bool IncludeExtraStats
        {
            get => _includeExtraStats;
            set
            {
                if (_includeExtraStats == value) return;

                _includeExtraStats = value;

                RaisePropertyChanged();
            }
        }

        public bool NeedsStats
        {
            get => _needsStats;
            set
            {
                if (_needsStats == value) return;

                _needsStats = value;

                RaisePropertyChanged();
            }
        }

        public bool IsVisible
        {
            get => _isVisible;
            set
            {
                if (_isVisible == value) return;

                _isVisible = value;

                RaisePropertyChanged();
            }
        }


        private bool _isQueryNotesEnabled;

        public bool IsQueryNotesEnabled
        {
            get => _isQueryNotesEnabled;
            set
            {
                if (_isQueryNotesEnabled == value) return;

                _isQueryNotesEnabled = value;

                RaisePropertyChanged();
            }
        }

        private bool _hasUnsavedChanges;
        public bool HasUnsavedChanges
        {
            get => _hasUnsavedChanges;
            set
            {
                if (_hasUnsavedChanges == value) return;

                _hasUnsavedChanges = value;
                RaisePropertyChanged();
            }
        }

        private bool _isImporting;

        public bool IsImporting
        {
            get => _isImporting;
            set
            {
                if (_isImporting == value) return;

                _isImporting = value;
                RaisePropertyChanged();
            }
        }

        private bool _includeNative = true;
        public bool IncludeNative
        {
            get => _includeNative;
            set
            {
                if (_includeNative == value) return;

                _includeNative = value;
                RaisePropertyChanged();
            }
        }

        private string _serverStatus;

        public string ServerStatus
        {
            get => _serverStatus;
            set
            {
                if (_serverStatus == value) return;

                _serverStatus = value;
                RaisePropertyChanged();
            }
        }

        private string _clientText;

        public string ClientText
        {
            get => _clientText;
            set
            {
                if (_clientText == value) return;

                _clientText = value;
                RaisePropertyChanged();
            }
        }

        private string _clientStatus;

        public string ClientStatus
        {
            get => _clientStatus;
            set
            {
                if (_clientStatus == value) return;

                _clientStatus = value;
                RaisePropertyChanged();
            }
        }

        private string _importHudProfileFileName = "C:\\Users\\Mike\\Desktop\\Cash - Default.hm3hud";

        public string ImportHudProfileFileName
        {
            get => _importHudProfileFileName;
            set
            {
                if (_importHudProfileFileName == value) return;

                _importHudProfileFileName = value;
                RaisePropertyChanged();
            }
        }

        private string _importHudProfileProfileName = "hudProfileName";

        public string ImportHudProfileProfileName
        {
            get => _importHudProfileProfileName;
            set
            {
                if (_importHudProfileProfileName == value) return;

                _importHudProfileProfileName = value;
                RaisePropertyChanged();
            }
        }

        private string _importHudProfileTableType = "cash";

        public string ImportHudProfileTableType
        {
            get => _importHudProfileTableType;
            set
            {
                if (_importHudProfileTableType == value) return;

                _importHudProfileTableType = value;
                RaisePropertyChanged();
            }
        }

        public class HandInfo : INotifyPropertyChanged
        {
            private int _siteId;
            private string _handNo;
            private string _handTagsDesc;
            private string _xml;
            private bool _handSelected;
            private string _menuItem;

            public int SiteId
            {
                get => _siteId;
                set
                {
                    _siteId = value;
                    OnPropertyChanged();
                }
            }

            public string HandNo
            {
                get => _handNo;
                set
                {
                    _handNo = value;
                    OnPropertyChanged();
                }
            }

            public bool HandSelected
            {
                get => _handSelected;
                set
                {
                    _handSelected = value;
                    OnPropertyChanged();
                }
            }

            public string MenuItem
            {
                get => _menuItem;
                set
                {
                    _menuItem = value;
                    OnPropertyChanged();
                }
            }

            public string HandTagsDesc
            {
                get => _handTagsDesc;
                set
                {
                    _handTagsDesc = value;
                    OnPropertyChanged();
                }
            }

            public string Xml
            {
                get => _xml;
                set
                {
                    _xml = value;
                    OnPropertyChanged();
                }
            }

            public event PropertyChangedEventHandler PropertyChanged;

            protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            }
        }

        private ObservableCollection<HandInfo> _hands = new ObservableCollection<HandInfo>();

        public ObservableCollection<HandInfo> Hands
        {
            get => _hands;
            set
            {
                if (_hands == value) return;

                _hands = value;
                RaisePropertyChanged();
            }
        }

        public string HandsTooltip
        {
            get
            {
                var tooltip = "";
                if (Hands == null || Hands.Count == 0)
                    tooltip += "Hint: Right click from hm3 hands grid to 'send hands to the API demo' or use the PTSQL Query tab's 'Add to Hands' button to first load some hands. Also, to get the hand tags make sure you select a hand.";
                else if (SelectedHand == null)
                    tooltip = "Hint: To be able to get the hand tags, make sure you first select a hand.";
                return tooltip;
            }
        }

        private HandInfo _selectedHand;

        public HandInfo SelectedHand
        {
            get => _selectedHand;
            set
            {
                if (_selectedHand == value) return;

                _selectedHand = value;
                RaisePropertyChanged();
                RaisePropertyChanged("HandsTooltip");
            }
        }
        public bool HasSelectedHand => SelectedHand != null;


        public class SettingInfo
        {
            public string Name { get; set; }
            public string Value { get; set; }
            public string Status { get; set; }
            public string Prior { get; set; }
            public int Fetches { get; set; }
            public int Notified { get; set; }
        }

        private ObservableCollection<SettingInfo> _settings = new ObservableCollection<SettingInfo>();

        public ObservableCollection<SettingInfo> Settings
        {
            get => _settings;
            set
            {
                if (_settings == value) return;

                _settings = value;
                RaisePropertyChanged();
            }
        }

        public class PlayerInfo : INotifyPropertyChanged
        {
            private int _siteId;
            private string _name;
            private bool _anon;
            private int _cashHands;
            private int _tournamentHands;
            private string _note;

            public int SiteId
            {
                get => _siteId;
                set
                {
                    _siteId = value;
                    RaisePropertyChanged();
                }
            }

            public string Name
            {
                get => _name;
                set
                {
                    _name = value;
                    RaisePropertyChanged();
                }
            }

            public bool Anon
            {
                get => _anon;
                set
                {
                    _anon = value;
                    RaisePropertyChanged();
                }
            }

            public int CashHands
            {
                get => _cashHands;
                set
                {
                    _cashHands = value;
                    RaisePropertyChanged();
                }
            }

            public int TournamentHands
            {
                get => _tournamentHands;
                set
                {
                    _tournamentHands = value;
                    RaisePropertyChanged();
                }
            }

            public string Note
            {
                get => _note;
                set
                {
                    _note = value;
                    RaisePropertyChanged();
                }
            }

            #region INotifyPropertyChanged implementation

            public event PropertyChangedEventHandler PropertyChanged;

            private void RaisePropertyChanged([CallerMemberName] string propertyName = "")
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            }

            #endregion // INotifyPropertyChanged implementation
        }

        private ObservableCollection<PlayerInfo> _players = new ObservableCollection<PlayerInfo>();

        public ObservableCollection<PlayerInfo> Players
        {
            get => _players;
            set
            {
                if (_players == value) return;

                _players = value;
                RaisePropertyChanged();
            }
        }

        private ObservableCollection<PlayerInfo> _selectedPlayers = new ObservableCollection<PlayerInfo>();

        public ObservableCollection<PlayerInfo> SelectedPlayers
        {
            get => _selectedPlayers;
            set
            {
                if (_selectedPlayers == value) return;

                _selectedPlayers = value;
                RaisePropertyChanged();
                IsQueryNotesEnabled = _selectedPlayers.Count > 0;
            }
        }

        private string _positionalStats = "VPIP";
        public string PositionalStats
        {
            get => _positionalStats;
            set
            {
                if (_positionalStats == value) return;

                _positionalStats = value;
                RaisePropertyChanged();
            }
        }

        private string _hasPosition;
        public string HasPosition
        {
            get => _hasPosition;
            set
            {
                if (_hasPosition == value) return;

                _hasPosition = value;
                RaisePropertyChanged();
            }
        }

        private string _positionType = "";
        public string PositionType
        {
            get => _positionType;
            set
            {
                if (_positionType == value) return;

                _positionType = value;
                RaisePropertyChanged();
            }
        }

        private Collection<DataRowView> _selectedPositionalStatsRows = new Collection<DataRowView>();
        public Collection<DataRowView> SelectedPositionalStatsRows
        {
            get => _selectedPositionalStatsRows;
            set
            {
                if (_selectedPositionalStatsRows == value) return;

                _selectedPositionalStatsRows = value;
                RaisePropertyChanged();
            }
        }

        private DataTable _positionalStatsResults = new DataTable();
        public DataTable PositionalStatsResults
        {
            get => _positionalStatsResults;
            set
            {
                if (_positionalStatsResults == value) return;

                _positionalStatsResults = value;
                RaisePropertyChanged();
            }
        }

        private ObservableCollection<StatInfo> _stats = new ObservableCollection<StatInfo>();
        public ObservableCollection<StatInfo> Stats
        {
            get => _stats;
            set
            {
                if (_stats == value) return;

                _stats = value;
                RaisePropertyChanged();
            }
        }

        private ObservableCollection<StatInfo> _selectedStats = new ObservableCollection<StatInfo>();
        public ObservableCollection<StatInfo> SelectedStats
        {
            get => _selectedStats;
            set
            {
                if (_selectedStats == value) return;

                _selectedStats = value;
                RaisePropertyChanged();
            }
        }

        private string _hmqlQueryText = "select Vpip, Pfr, ThreeBet, TotalHands from stats";

        public string HmqlQueryText
        {
            get => _hmqlQueryText;
            set
            {
                if (_hmqlQueryText == value) return;

                _hmqlQueryText = value;
                RaisePropertyChanged();
            }
        }

        private string _ptsqlQueryStats = "Hand #, Date, Board";  // default "Hand" stats (for player stats, try "Hand", "VPIP", etc)

        public string PtsqlQueryStats
        {
            get => _ptsqlQueryStats;
            set
            {
                if (_ptsqlQueryStats == value) return;

                _ptsqlQueryStats = value;
                RaisePropertyChanged();
            }
        }

        private string _ptsqlQueryTableType = "cash";
        public string PtsqlQueryTableType
        {
            get => _ptsqlQueryTableType;
            set
            {
                if (_ptsqlQueryTableType == value) return;

                _ptsqlQueryTableType = value;
                RaisePropertyChanged();
            }
        }

        private bool _ptsqlQueryActivePlayer = true;
        public bool PtsqlQueryActivePlayer
        {
            get => _ptsqlQueryActivePlayer;
            set
            {
                if (_ptsqlQueryActivePlayer == value) return;

                _ptsqlQueryActivePlayer = value;
                RaisePropertyChanged();
            }
        }

        private bool _ptsqlQueryHandQuery = true;
        public bool PtsqlQueryHandQuery
        {
            get => _ptsqlQueryHandQuery;
            set
            {
                if (_ptsqlQueryHandQuery == value) return;

                _ptsqlQueryHandQuery = value;
                RaisePropertyChanged();
            }
        }

        private DataTable _ptsqlQueryResults = new DataTable();
        public DataTable PtsqlQueryResults
        {
            get => _ptsqlQueryResults;
            set
            {
                if (_ptsqlQueryResults == value) return;

                _ptsqlQueryResults = value;
                RaisePropertyChanged();
            }
        }

        private Collection<DataRowView> _selectedPtsqlQueryResultsRows = new Collection<DataRowView>();
        public Collection<DataRowView> SelectedPtsqlQueryResultsRows
        {
            get => _selectedPtsqlQueryResultsRows;
            set
            {
                if (_selectedPtsqlQueryResultsRows == value) return;

                _selectedPtsqlQueryResultsRows = value;
                RaisePropertyChanged();
                RaisePropertyChanged("AreSelectedPtsqlQueryResultsRowsValidForAddingToHands");
            }
        }

        public bool AreSelectedPtsqlQueryResultsRowsValidForAddingToHands =>
            GetPtsqlHandIdentifierColumns(out int siteId, out int handNo) && SelectedPtsqlQueryResultsRows.Count > 0;

        private string _statQueryPlayers;
        public string StatQueryPlayers
        {
            get => _statQueryPlayers;
            set
            {
                if (_statQueryPlayers == value) return;

                _statQueryPlayers = value;
                RaisePropertyChanged();
            }
        }

        private int _statQuerySiteId = -1;
        public int StatQuerySiteId
        {
            get => _statQuerySiteId;
            set
            {
                if (_statQuerySiteId == value) return;

                _statQuerySiteId = value;
                RaisePropertyChanged();
            }
        }

        public string StatQueryDefaultStats { get; set; }

        private string _statQueryStats = "";
        public string StatQueryStats
        {
            get => _statQueryStats;
            set
            {
                if (_statQueryStats == value) return;

                _statQueryStats = value;
                RaisePropertyChanged();
            }
        }

        private DataTable _statQueryResults = new DataTable();
        public DataTable StatQueryResults
        {
            get => _statQueryResults;
            set
            {
                if (_statQueryResults == value) return;

                _statQueryResults = value;
                RaisePropertyChanged();
            }
        }

        private DataTable _hmqlQueryResults = new DataTable();
        public DataTable HmqlQueryResults
        {
            get => _hmqlQueryResults;
            set
            {
                if (_hmqlQueryResults == value) return;

                _hmqlQueryResults = value;
                RaisePropertyChanged();
            }
        }


        private int _getHandsType;
        public int GetHandsType
        {
            get => _getHandsType;
            set
            {
                if (_getHandsType == value) return;

                _getHandsType = value;
                RaisePropertyChanged();
            }
        }

        private int _handsMenuOption;
        public int HandsMenuOption
        {
            get => _handsMenuOption;
            set
            {
                if (_handsMenuOption == value) return;

                _handsMenuOption = value;
                RaisePropertyChanged();
            }
        }

        private int _handsMenuHandFormat;
        public int HandsMenuHandFormat
        {
            get => _handsMenuHandFormat;
            set
            {
                if (_handsMenuHandFormat == value) return;

                _handsMenuHandFormat = value;
                RaisePropertyChanged();
            }
        }

        private bool _useCustomHandsMenuIcon;
        public bool UseCustomHandsMenuIcon
        {
            get => _useCustomHandsMenuIcon;
            set
            {
                if (_useCustomHandsMenuIcon == value) return;

                _useCustomHandsMenuIcon = value;
                RaisePropertyChanged();
            }
        }

        private int _importHandSiteId = StandardizedHandsSiteId;
        public int ImportHandSiteId
        {
            get => _importHandSiteId;
            set
            {
                if (_importHandSiteId == value) return;

                _importHandSiteId = value;
                RaisePropertyChanged();
            }
        }

        private double _hudSystemDpi;
        public double HudSystemDpi
        {
            get => _hudSystemDpi;
            set
            {
                _hudSystemDpi = value;
                RaisePropertyChanged();
            }
        }

        private string _importStarted;
        public string ImportStarted
        {
            get => _importStarted;
            set
            {
                _importStarted = value;
                RaisePropertyChanged();
            }
        }

        private int _numNoops = 100;
        public int NumNoops
        {
            get => _numNoops;
            set
            {
                _numNoops = value;
                RaisePropertyChanged();
            }
        }

        private int _extraNoopBytes = 0;
        public int ExtraNoopBytes
        {
            get => _extraNoopBytes;
            set
            {
                _extraNoopBytes = value;
                RaisePropertyChanged();
            }
        }

        private int _noopWaitTime = 1000;
        public int NoopWaitTime
        {
            get => _noopWaitTime;
            set
            {
                _noopWaitTime = value;
                RaisePropertyChanged();
            }
        }

        private bool _noopShouldFail;
        public bool NoopShouldFail
        {
            get => _noopShouldFail;
            set
            {
                _noopShouldFail = value;
                RaisePropertyChanged();
            }
        }

        private int _selectStatsOrFiltersTableTypeIndex = 0;
        public int SelectStatsOrFiltersTableTypeIndex
        {
            get => _selectStatsOrFiltersTableTypeIndex;
            set
            {
                if (_selectStatsOrFiltersTableTypeIndex == value) return;

                _selectStatsOrFiltersTableTypeIndex = value;
                RaisePropertyChanged();
            }
        }


        #region INotifyPropertyChanged implementation
        public event PropertyChangedEventHandler PropertyChanged;

        private void RaisePropertyChanged([CallerMemberName] string propertyName = "")
        {
            if (null != PropertyChanged)
                PropertyChanged(this, new PropertyChangedEventArgs(propertyName));
        }
        #endregion // INotifyPropertyChanged implementation


        // api commands to call
        internal Dictionary<string, ICommand> ApiCommands =>
            _apiCommands ?? (_apiCommands = new Dictionary<string, ICommand>
            {
                {"&Hide", HideCommand },
                {"&Show", ShowCommand },
                {"-", null},
                {"&Get Stats", GetStatsCommand},
                {"Re&gister Stats", RegisterStatsCommand},
                {"Remo&ve Stats", RemoveStatsCommand},
                {"Selec&t Stats", SelectStatsCommand},
                {"Select &Filters", SelectFiltersCommand},
                {"Get Hand &Xml", GetHandXmlCommand},
                {"Get Hand Ta&gs", GetHandTagsCommand},
                {"&Replay Hands", ReplayHandsCommand},
                {"&Import Hand", ImportHandCommand},
                {"Player Search", PlayerSearchCommand},
                {"&Change Table HUD", ChangeHudProfileCommand},
                {"Change Database", ChangeDatabaseCommand},
                {"&Noop Test", NoopTestCommand},
            });

        // build menu items from the list of api commands
        private List<string> MenuItems
        {
            get
            {
                var menuItems = new List<string>();
                foreach (var item in ApiCommands)
                {
                    if (item.Key.Equals("&Hide") && !_isVisible)
                        continue;
                    if (item.Key.Equals("&Show") && _isVisible)
                        continue;
                    menuItems.Add(item.Key);
                }

                return menuItems;
            }
        }

        private bool DoMenuSelected(string menuItem)
        {
            if (ApiCommands.TryGetValue(menuItem, out var command))
                command.Execute(null);
            return true;
        }

        private bool DoCallbackCallback(string callback, int windowId, int positionX, int positionY)
        {
            LogMessage($@"DoCallbackCallback: callback={callback}, windowId={windowId}, positionX={positionX}, positionY={positionY}");
            return true;
        }

        private bool DoStatPreviewCallback(string stat, int tabletype)
        {
            LogMessage($@"DoStatPreviewCallback: stat={stat}, tabletype={tabletype}");
            return true;
        }


        private static long _noteId;
        private bool DoNoteTabValueCallback(string tabName, string playerName, int siteId, string lastHandNo, StringBuilder jsonBuffer, int jsonBufferLen)
        {
            var on = " on " + tabName + " for " + playerName;
            const string image = "{ \"path\": \".\\\\Data\\\\Images\\\\Hud\\\\nc_sample.png\", " +
                                 "\"tooltip\": { \"text\": \"main tooltip for nc_sample.png image\"}, " +
                                 "\"hotspots\": [{ \"x\": 10, \"y\": 10, \"width\": 50, \"height\": 50, \"tooltip\": { \"text\": \"textual tooltip in image hotspot 1\" }}," +
                                                "{ \"x\": 60, \"y\": 60, \"width\": 50, \"height\": 50, \"tooltip\": { \"text\": \"textual tooltip in image hotspot 2\", \"image\": { \"path\": \".\\\\Data\\\\Images\\\\Hud\\\\nc_sample.png\" }}}," +
                                                "{ \"x\": 110, \"y\": 110, \"width\": 50, \"height\": 50, \"tooltip\": { \"text\": \"textual tooltip in image hotspot 3\" }}] }";

            // todo: fix exceptions and make it return some hands
            var hands = FindHandsForPlayer(siteId, playerName);

            var emptyList = new List<Tuple<int, string>>();
            var hands1 = emptyList;
            var hands2 = hands.Count >= 1 ? hands.Take(1).ToList() : emptyList;
            var hands3 = hands.Count >= 5 ? hands.Take(5).ToList() : emptyList;

            // remember which hands belong to which notes
            _noteHands[(_noteId + 1).ToString()] = hands1;
            _noteHands[(_noteId + 2).ToString()] = hands2;
            _noteHands[(_noteId + 3).ToString()] = hands3;

            const string newLine = @"\n";
            const string note2ExtraText = " ... a longer single line note, that might work well with word wrap.";
            const string note3ExtraText = "... a much longer multi-line note with both short and long lines" +
                                          newLine + "paragraph 2 starts here" +
                                          newLine + "paragraph 3 has more info" +
                                          newLine + "paragraph 4, well: " +
                                          "Jackdaws love my big sphinx of quartz." +
                                          "The five boxing wizards jump quickly." +
                                          "The quick brown fox jumps over the lazy dog." +
                                          "Heavy boxes perform quick waltzes and jigs." +
                                          "Pack my box with five dozen liquor jugs.";

            var note2HasHands = hands2.Any() ? "true" : "false";
            var note3HasHands = hands3.Any() ? "true" : "false";

            var note1Tooltip = "{ \"text\": \"note 1 tooltip\" }";
            var note2Tooltip = "{ \"text\": \"note 2 tooltip\" }";
            var note2ReplayerTooltip = "{ \"image\": " + image + ", \"text\": \"note 2 replayer tooltip\" }";
            var note3Tooltip = "{ \"image\": " + image + ", \"text\": \"note 3 tooltip\" }";
            var note3ReplayerTooltip = "{ \"text\": \"note 3 replayer tooltip\" }";

            var result =
                "[ {\"note\": \"Note " + ++_noteId + on + "\", " +
                    "\"note_id\": \"" + _noteId + "\", " +
                    "\"tooltip\":" + note1Tooltip + "}," +
                  "{\"note\": \"Note " + ++_noteId + on + note2ExtraText + "\" ," +
                   " \"note_id\": \"" + _noteId + "\", " +
                    "\"has_hands\": " + note2HasHands + ", " +
                    "\"tooltip\": " + note2Tooltip + ", " +
                    "\"replayer_tooltip\": " + note2ReplayerTooltip + "}," +
                  "{\"note\": \"Note " + ++_noteId + on + note3ExtraText + "\" , " +
                   "\"note_id\": \"" + _noteId + "\", " +
                    "\"has_hands\": " + note3HasHands + ", " +
                    "\"tooltip\": " + note3Tooltip + ", " +
                    "\"replayer_tooltip\": " + note3ReplayerTooltip + ", " +
                    "\"image\": " + image + "} ]";

            jsonBuffer.Append(result);

            return true;
        }

        private List<Tuple<int, string>> FindHandsForPlayer(int ptSiteId, string playerName)
        {
            var noteHands = new List<Tuple<int, string>>();
            foreach (var item in Hands)
            {
                var xmlDoc = new XmlDocument();
                xmlDoc.LoadXml(item.Xml);
                var xmlSiteId = xmlDoc.DocumentElement.Attributes["site_id"].Value;
                if (!int.TryParse(xmlSiteId, out var xmlSiteIdInt))
                    continue;
                var xmlHandNo = xmlDoc.DocumentElement.Attributes["hand_no"].Value;
                var playerNodes = xmlDoc.DocumentElement.SelectNodes("/Hand/Player");
                if (playerNodes == null)
                    continue;
                if (ptSiteId != xmlSiteIdInt)
                    continue;
                // skip hands with missing or invalid hand numbers (pre-fetch hands will have "0")
                if (string.IsNullOrEmpty(xmlHandNo) || xmlHandNo == "0")
                    continue;
                foreach (XmlNode player in playerNodes)
                    if (player.Attributes != null && playerName.Equals(player.Attributes["name"].Value))
                        noteHands.Add(new Tuple<int, string>(xmlSiteIdInt, xmlHandNo));
            }
            return noteHands;
        }

        private readonly Dictionary<string, List<Tuple<int, string>>> _noteHands = new Dictionary<string, List<Tuple<int, string>>>();
        private bool DoNoteHandsCallback(string noteId, out HandIdentifier[] hands)
        {
            var handsList = _noteHands.ContainsKey(noteId)
                ? _noteHands[noteId]
                : new List<Tuple<int, string>>();
            hands = handsList.Select(h => new HandIdentifier { SiteId = h.Item1, HandNo = h.Item2 }).ToArray();
            return true;
        }

        public static string Base64Encode(string plainText)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(plainText));
        }

        private static string Base64Decode(string encodedText)
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(encodedText));
        }

        private bool DoHandCallback(string hand)
        {
            TrackHands(hand);
            return true;
        }

        private bool DoHandsSelectedCallback(string[] hands, string menuItem)
        {
            foreach (var hand in hands)
                TrackHands(hand, true, menuItem);
            return true;
        }

        private void TrackHands(string handText, bool handSelected = false, string menuItem = "")
        {
            // track the known site id's and hand #'s (so we can use them in get_hands requests)
            if (HandsMenuHandFormat == 0)
            {
                var xmlHand = new XmlDocument();
                xmlHand.LoadXml(handText);
                if (xmlHand.DocumentElement != null)
                {
                    var siteId = Convert.ToInt32(xmlHand.DocumentElement.Attributes["site_id"].Value);
                    var handNo = xmlHand.DocumentElement.Attributes["hand_no"].Value;
                    var handInfo = new HandInfo
                    {
                        SiteId = siteId,
                        HandNo = handNo,
                        Xml = handText,
                        HandSelected = handSelected,
                        MenuItem = menuItem
                    };
                    _dispatcher.Invoke(() => Hands.Add(handInfo));
                    RaisePropertyChanged("HandsTooltip");
                }
            }
            else
            {
                dynamic jsonHand = JsonConvert.DeserializeObject(handText);
                var ohh = jsonHand != null ? jsonHand["ohh"] : null;
                var siteName = ohh != null ? ohh["site_name"] : null;
                var handNo = ohh != null ? ohh["game_number"] : null;
                var handInfo = new HandInfo
                {
                    SiteId = siteName == "PokerStars" ? 100 : -1,
                    HandNo = handNo,
                    Xml = handText,
                    HandSelected = handSelected,
                    MenuItem = menuItem
                };
                _dispatcher.Invoke(() => Hands.Add(handInfo));
                RaisePropertyChanged("HandsTooltip");
            }
        }

        private bool DoTablesCallback(Table[] tables)
        {
            Console.WriteLine($"tables:{tables.Length}");
            _tableInfo = tables.ToList();
            return true;
        }

        private bool DoSettingsChangedCallback(string setting, string newValue)
        {
            if (setting != null && newValue != null)
            {
                var settingName = setting;
                switch (settingName)
                {
                    case "active_database_alias":
                        CurrentDatabaseName = newValue;
                        ClearHands();
                        break;
                    case "active_player":
                        var str = newValue;
                        var player = JsonConvert.DeserializeObject<CurrentPlayerInfo>(str);
                        CurrentPlayerName = player.PlayerName;
                        CurrentPlayerSite = string.IsNullOrEmpty(player.SiteId) ? 0 : Convert.ToInt32(player.SiteId);
                        ClearHands();
                        break;
                    case "import_started":
                        ImportStarted = newValue;
                        break;
                }
                _dispatcher.Invoke(() =>
                {
                    var priorInfo = Settings.FirstOrDefault(s => s.Name == settingName);
                    var current = priorInfo ?? new SettingInfo();
                    if (priorInfo != null)
                        current.Prior = priorInfo.Value;
                    current.Value = newValue;
                    current.Notified++;
                    if (priorInfo == null)
                        current.Name = settingName;
                    else
                        Settings.Remove(priorInfo);
                    Settings.Add(current);
                });
            }

            return true;
        }

        private bool DoNotesCallback(string player, int siteid, string notes, IntPtr autonotescash, int autonotescashcount, IntPtr autonotestny, int autonotestnycount, string color)
        {
            Console.WriteLine("Notes");
            return true;
        }

        private bool DoLicenseChangedCallback(Restriction[] restrictions, int restrictionscount, int istrial, string expires)
        {
            Console.WriteLine("LicenseChanged");
            return true;
        }


        private bool DoReplayHandCallback(string hand, int hwnd, Point[] centerPoints)
        {
            _dispatcher.Invoke(() =>
            {
                var centerPointsJson = JsonConvert.SerializeObject(centerPoints);
                AddClientText($@"ReplayHandCallback: hwnd={hwnd}, centerPoints={centerPointsJson}, hand={hand}");
            });
            return true;
        }

        private bool DoNoop(int wait, bool shouldFail)
        {
            if (wait > 0)
                Thread.Sleep(wait);
            return shouldFail;
        }

        private void ClearHands()
        {
            // clear known hands list (empty and start gathering again for the new db)
            _dispatcher.Invoke(() => Hands.Clear());
            RaisePropertyChanged("HandsTooltip");
        }

        private string _statQueryFilters;
        public string StatQueryFilters
        {
            get => _statQueryFilters;
            set
            {
                if (value == _statQueryFilters) return;

                _statQueryFilters = value;
                RaisePropertyChanged();
            }
        }

        private bool DoHasUnsavedChangesCallback()
        {
            return HasUnsavedChanges;
        }


        private bool DoImportStartedCallback(string importType)
        {
            IsImporting = true;
            return true;
        }

        private bool DoImportStoppedCallback()
        {
            IsImporting = false;
            return true;
        }

        private bool DoSleepBeginCallback()
        {
            IsSleeping = true;
            return true;
        }

        private bool DoSleepEndCallback()
        {
            IsSleeping = false;
            return true;
        }


        private bool DoStatValueCallback(string stat, int gameType, int siteId, string player, string filters, out string value)
        {
            value = StatValue(stat, player);
            return true;
        }

        private string ReverseCharacters(string value)
        {
            var reversed = value.ToCharArray();
            Array.Reverse(reversed);
            return new string(reversed);
        }

        private string StatValue(string statName, string playerName)
        {
            var statValue = "";
            switch (statName)
            {
                case "DMO.UserNameLength":
                    statValue = playerName.Length.ToString();
                    break;
                case "DMO.UserNameRestrictedOnPokerStars":
                    statValue = playerName;
                    break;
                case "DMO.UserNameReverse":
                    statValue = ReverseCharacters(playerName);
                    break;
                default:
                    if (statName.StartsWith("DMO.RandomStat"))
                        statValue = ReverseCharacters(playerName);
                    break;
            }

            if (DelayStatValues)
                Thread.Sleep(1000);

            return statName + ":" + statValue;
        }

        private bool DoQuitCallback()
        {
            _dispatcher.BeginInvoke(
                new Action(() =>
                {
                    _adapter.Disconnect();
                    Close();
                }));
            return true;
        }

        private void Close()
        {
            ShouldClose = true;
            _dispatcher.Invoke(CloseAction);
        }

        // client commands ...

        private bool RegisterNoteTab(string tabName, string tabIcon)
        {
            return _adapter.RegisterNoteTab(tabName, tabIcon);
        }


        private DelegateCommand<object> _sendNoopCommand;
        public ICommand NoopTestCommand
        {
            get
            {
                return _sendNoopCommand ?? (_sendNoopCommand = new DelegateCommand<object>(param =>
                {
                    if (NumNoops <= 0)
                        return;
                    int noopSize = 0;
                    var start = DateTime.Now;
                    for (var x = 0; x < NumNoops; x++)
                    {
                        var extraBytesStr = new string('a', ExtraNoopBytes);
                        _adapter.Noop(NoopWaitTime, NoopShouldFail, extraBytesStr, out noopSize);
                    }
                    var span = DateTime.Now - start;
                    _totalClientDuration += span;
                    _totalClientResponses += NumNoops;

                    ClientStatus = @"Tracker's avg. response time: " + _totalClientDuration.TotalMilliseconds / _totalClientResponses + @"ms.";
                    var spanPerNoop = span.TotalMilliseconds / NumNoops;
                    AddClientText($"Requested {NumNoops} noops in {span.TotalSeconds}secs ({spanPerNoop}ms/noop, size={noopSize})");
                }, param => true));
            }
        }

        private bool _lastRegisterMenuCommandSuccess;
        private DelegateCommand<object> _registerMenuCommand;
        public ICommand RegisterMenuCommand
        {
            get
            {
                return _registerMenuCommand ?? (_registerMenuCommand = new DelegateCommand<object>(param =>
                {
                    _lastRegisterMenuCommandSuccess = _adapter.RegisterMenu(MenuItems);
                }, param => true));
            }
        }


        private bool DoGetStatsCallback(BlockingCollection<StatInfo> stats, IntPtr userData)
        {
            LogMessage("DoGetStatsCallback called");
            _dispatcher.BeginInvoke(new Action(() =>
            {
                Stats.Clear();
                LogMessage("DoGetStatsCallback - clear stats");
                while (true)
                {
                    var addingCompleted = stats.IsAddingCompleted;
                    if (!stats.TryTake(out var stat))
                    {
                        if (addingCompleted)
                            break;
                        continue;
                    }
                    Stats.Add(stat);
                }

                LogMessage($"DoGetStatsCallback - completed, {Stats.Count} stats added");
                NeedsStats = false;  // will be informed via stats_changed if stats are needed
            }));

            LogMessage("DoGetStatsCallback returns");
            return true;
        }

        private DelegateCommand<object> _getStatsCommand;
        public ICommand GetStatsCommand
        {
            get
            {
                return _getStatsCommand ?? (_getStatsCommand = new DelegateCommand<object>(param =>
                {
                    ClientStatus = string.Empty;
                    var tableType = IsCash ? TableType.Cash : TableType.Tournament;
                    _adapter.GetStats(tableType, IsFullDetails, DoGetStatsCallback, IntPtr.Zero);
                }, param => true));
            }
        }

        private List<Stat> GetSampleStats(bool includeExtraStats)
        {
            // add 'user name reversed' and 'user name length' stats
            var userNameReverseStat = new Stat
            {
                Categories = new List<string> { "API Demo Stats" }.ToArray(),
                Description = "ApiDemo - User Name Reversed",
                Title = "User Name Reversed",
                Name = "DMO.UserNameReverse",
                TableType = "both",
            };
            var userNameLengthStat = new Stat
            {
                Categories = new List<string> { "API Demo Stats" }.ToArray(),
                Description = "ApiDemo - User Name Length",
                Title = "User Name Length",
                Name = "DMO.UserNameLength",
                TableType = "both",
                Flags = new List<string> { "decimals" }.ToArray()
            };
            var restrictedOnStarsFlag = "restricted_on_100";
            var userNameRestrictedOnStarsStat = new Stat
            {
                Categories = new List<string> { "API Demo Stats" }.ToArray(),
                Description = "ApiDemo - User Name (Restricted on PokerStars)",
                Title = "User Name (Restricted on PokerStars)",
                Name = "DMO.UserNameRestrictedOnPokerStars",
                TableType = "both",
                Flags = new List<string> { restrictedOnStarsFlag }.ToArray()
            };

            var stats = new List<Stat> { userNameReverseStat, userNameLengthStat, userNameRestrictedOnStarsStat };

            if (!includeExtraStats)
                return stats;

            // add some random stats
            for (var x = 1; x <= 1700; x++)
            {
                var stat = new Stat
                {
                    Categories = new List<string> { "API Demo Stats" }.ToArray(),
                    Description = "Random Stat #" + x,
                    Title = "RandomStat" + x,
                    Name = "DMO.RandomStat" + x,
                    TableType = "both"
                };
                stats.Add(stat);
            }

            return stats;
        }

        private bool DoRegisterStatsCallback(int callerId, bool errored, int errorCode, string errorMessage, IntPtr userData)
        {
            // do something
            return true;
        }

        private bool DoRemoveStatsCallback(int callerId, bool errored, int errorCode, string errorMessage, IntPtr userData)
        {
            // do something
            return true;
        }

        private DelegateCommand<object> _registerStatsCommand;
        public ICommand RegisterStatsCommand
        {
            get
            {
                return _registerStatsCommand ?? (_registerStatsCommand = new DelegateCommand<object>(param =>
                {
                    var start = DateTime.Now;
                    ClientStatus = string.Empty;
                    var stats = GetSampleStats(IncludeExtraStats);
                    bool success = _adapter.RegisterStats(stats, DoRegisterStatsCallback);
                }, param => true));
            }
        }

        private DelegateCommand<object> _removeStatsCommand;
        public ICommand RemoveStatsCommand
        {
            get
            {
                return _removeStatsCommand ?? (_removeStatsCommand = new DelegateCommand<object>(param =>
                {
                    var start = DateTime.Now;
                    ClientStatus = string.Empty;
                    var stats = GetSampleStats(IncludeExtraStats);
                    var success = _adapter.RemoveStats(stats, DoRemoveStatsCallback);
                }, param => true));
            }
        }


        private bool DoRegisterPositionalStatsCallback(int callerid, bool errored, int errorcode, string errormessage, string[] statnames, IntPtr userdata)
        {
            _dispatcher.BeginInvoke(new Action(() =>
            {
                var dataTable = new DataTable();

                dataTable.Columns.Add("Stat");
                dataTable.Columns.Add("Table Type");
                dataTable.Columns.Add("Has Position");
                dataTable.Columns.Add("Position Type");

                if (statnames == null)
                {
                    StatQueryResults = dataTable;
                    return;
                }

                for (var index = 0; index < statnames.Length; index++)
                    statnames[index] = statnames[index].Trim();

                // see: https://stackoverflow.com/questions/2940618/what-is-it-about-datatable-column-names-with-dots-that-makes-them-unsuitable-for
                foreach (var stat in statnames)
                    dataTable.Columns.Add(stat.Replace(".", "\x2024"));

                foreach (var name in statnames)
                {
                    var values = new List<object>();
                    values.Add(name);
                    var valuesArray = values.ToArray();
                    dataTable.Rows.Add(valuesArray);
                }

                PositionalStatsResults = dataTable;
            }));

            return true;
        }



        private DelegateCommand<object> _registerPositionalStatsCommand;

        public ICommand RegisterPositionalStatsCommand
        {
            get
            {
                return _registerPositionalStatsCommand ?? (_registerPositionalStatsCommand =
                        new DelegateCommand<object>
                        (
                            param =>
                            {
                                var start = DateTime.Now;
                                ClientStatus = string.Empty;
                                var stats = PositionalStats.Split(',').Select(s => s.Trim()).ToList();
                                var tableType = SelectStatsOrFiltersTableType == "cash" ? TableType.Cash : TableType.Tournament;
                                var hasPosition = MvsAppApi.Core.Enums.HasPosition.None;
                                switch (HasPosition)
                                {
                                    case "inpos_flop":
                                        hasPosition = MvsAppApi.Core.Enums.HasPosition.InPos_Flop;
                                        break;
                                    case "inpos_turn":
                                        hasPosition = MvsAppApi.Core.Enums.HasPosition.InPos_Turn;
                                        break;
                                    case "inpos_river":
                                        hasPosition = MvsAppApi.Core.Enums.HasPosition.InPos_River;
                                        break;
                                    case "outpos_flop":
                                        hasPosition = MvsAppApi.Core.Enums.HasPosition.OutPos_Flop;
                                        break;
                                    case "outpos_turn":
                                        hasPosition = MvsAppApi.Core.Enums.HasPosition.OutPos_Turn;
                                        break;
                                    case "outpos_river":
                                        hasPosition = MvsAppApi.Core.Enums.HasPosition.OutPos_River;
                                        break;
                                }
                                var positionType = MvsAppApi.Core.Enums.PositionType.None;
                                switch (PositionType)
                                {
                                    case "bb":
                                        positionType = MvsAppApi.Core.Enums.PositionType.Bb;
                                        break;
                                    case "sb":
                                        positionType = MvsAppApi.Core.Enums.PositionType.Sb;
                                        break;
                                    case "ep":
                                        positionType = MvsAppApi.Core.Enums.PositionType.Ep;
                                        break;
                                    case "mp":
                                        positionType = MvsAppApi.Core.Enums.PositionType.Mp;
                                        break;
                                    case "co":
                                        positionType = MvsAppApi.Core.Enums.PositionType.Co;
                                        break;
                                    case "btn":
                                        positionType = MvsAppApi.Core.Enums.PositionType.Btn;
                                        break;
                                }
                                var success = _adapter.RegisterPositionalStats(tableType, stats, positionType, hasPosition, DoRegisterPositionalStatsCallback);
                            }
                        )
                    );
            }
        }

        private bool DoSelectStats(int callerId, bool cancelled, string[] selectedStats, IntPtr userData)
        {
            if (!cancelled)
                StatQueryStats = string.Join(",", selectedStats);
            return true;
        }


        private DelegateCommand<object> _selectStatsCommand;
        public ICommand SelectStatsCommand
        {
            get
            {
                return _selectStatsCommand ?? (_selectStatsCommand = new DelegateCommand<object>(param =>
                {
                    ClientStatus = string.Empty;

                    var includedStats = StatQueryStats.Split(',').Select(s => s.Trim()).ToArray();
                    var defaultStats = StatQueryDefaultStats.Split(',').Select(s => s.Trim()).ToArray();
                    var tableType = SelectStatsOrFiltersTableType == "cash" ? TableType.Cash : TableType.Tournament;
                    var result = _adapter.SelectStats(tableType, includedStats, defaultStats, DoSelectStats);
                }, param => true));
            }
        }

        private class FilterObject
        {
            public string name;
            public string description;
        }

        private bool DoSelectFilters(int callerId, bool cancelled, string filters, IntPtr userData)
        {
            if (!cancelled)
                StatQueryFilters = filters;

            return true;
        }


        private DelegateCommand<object> _selectFiltersCommand;
        public ICommand SelectFiltersCommand
        {
            get
            {
                return _selectFiltersCommand ?? (_selectFiltersCommand = new DelegateCommand<object>(param =>
                {
                    var start = DateTime.Now;
                    ClientStatus = string.Empty;
                    var result = _adapter.SelectFilters(SelectStatsOrFiltersTableType, StatQueryFilters, DoSelectFilters);
                }, param => true));
            }
        }

        private bool DoGetHandTagsCallback(int callerId, bool errored, int errorCode, string errorMsg, string[] tags, IntPtr userData)
        {
            _dispatcher.BeginInvoke(new Action(() =>
            {
                SelectedHand.HandTagsDesc = string.Join(",", tags);
            }));
            return true;
        }

        bool SetHands(IReadOnlyList<string> hands, bool encoded)
        {
            if (hands.Count != Hands.Count)
            {
                // error, mismatch in count of hands requested vs. returned
                return false;
            }

            _dispatcher.BeginInvoke(new Action(() =>
            {
                for (var x = 0; x < Hands.Count; x++)
                {
                    var hand = encoded ? Base64Decode(hands[x]) : hands[x];
                    Hands[x] = new HandInfo
                    {
                        SiteId = Hands[x].SiteId,
                        HandNo = Hands[x].HandNo,
                        Xml = hand
                    };
                }
            }));
            return true;
        }

        private bool DoGetHandsCallback(int callerId, string[] encodedHands, IntPtr userData)
        {
            return SetHands(encodedHands, true);
        }

        private void GetHands()
        {
            var handIds = Hands.Select(h => new HandIdentifier { SiteId = h.SiteId, HandNo = h.HandNo });
            _adapter.GetHands(handIds, IncludeNative, DoGetHandsCallback);
        }

        private bool DoGetHandsToFileCallback(int callerId, string[] hands, IntPtr userData)
        {
            return SetHands(hands, false);
        }

        private void GetHandsToFile()
        {
            var fileName = Path.GetTempFileName();
            var handIds = Hands.Select(h => new HandIdentifier { SiteId = h.SiteId, HandNo = h.HandNo });
            _adapter.GetHandsToFile(handIds, IncludeNative, fileName, DoGetHandsToFileCallback);
        }

        private bool DoGetHandsToSharedMemoryCallback(int callerId, string[] hands, IntPtr userData)
        {
            return SetHands(hands, false);
        }


        private void GetHandsToSharedMemory()
        {
            const string memoryName = "tempNameForThis";
            const int memorySize = 100000;
            var handIds = Hands.Select(h => new HandIdentifier { SiteId = h.SiteId, HandNo = h.HandNo });
            _adapter.GetHandsToSharedMemory(handIds, IncludeNative, memoryName, memorySize, DoGetHandsToSharedMemoryCallback);
        }

        private bool DoTagsCallback(int siteid, string handno, IntPtr tags, int tagscount)
        {
            Console.WriteLine("Tags");
            return true;
        }


        // todo: we might want to move this (taken from HM3's string extensions)
        private static string[] GetQueryFields(string query)
        {
            if (string.IsNullOrEmpty(query))
                return null;
            int startIndex = query.ToLower().IndexOf("select ", StringComparison.Ordinal);
            if (0 > startIndex)
                return null;
            startIndex += "select ".Length;
            int endIndex = query.ToLower().IndexOf(" from ", StringComparison.Ordinal);
            if (startIndex >= endIndex)
                return null;
            var tokens = query.Substring(startIndex, endIndex - startIndex).Split(',');
            if (0 == tokens.Length)
                return null;
            for (int index = 0; index < tokens.Length; index++)
                tokens[index] = tokens[index].Trim();
            return tokens.Distinct(StringComparer.InvariantCultureIgnoreCase).ToArray();
        }


        private DelegateCommand<object> _getHandXmlCommand;
        public ICommand GetHandXmlCommand
        {
            get
            {
                return _getHandXmlCommand ?? (_getHandXmlCommand = new DelegateCommand<object>(param =>
                {
                    var start = DateTime.Now;
                    ClientStatus = string.Empty;
                    switch (GetHandsType)
                    {
                        default:
                            GetHands();
                            break;
                        case 1:
                            GetHandsToSharedMemory();
                            break;
                        case 2:
                            GetHandsToFile();
                            break;
                    }
                }, param => true));
            }
        }

        private DelegateCommand<object> _getHandTagsCommand;
        public ICommand GetHandTagsCommand
        {
            get
            {
                return _getHandTagsCommand ?? (_getHandTagsCommand = new DelegateCommand<object>(param =>
                {
                    ClientStatus = string.Empty;
                    if (SelectedHand == null) return;

                    var siteId = SelectedHand.SiteId;
                    var handNo = SelectedHand.HandNo;
                    _adapter.GetHandTags(siteId, handNo, DoGetHandTagsCallback);
                }, param => true));
            }
        }

        private DelegateCommand<object> _importHandCommand;
        public ICommand ImportHandCommand
        {
            get
            {
                return _importHandCommand ?? (_importHandCommand = new DelegateCommand<object>(param =>
                {
                    ClientStatus = string.Empty;

                    // get hand from a file
                    var openFileDialog = new OpenFileDialog();
                    if (openFileDialog.ShowDialog() == true)
                    {
                        var hand = File.ReadAllText(openFileDialog.FileName);
                        var success = _adapter.ImportHand(ImportHandSiteId, hand);
                    }
                }, param => true));
            }
        }

        private DelegateCommand<object> _playerSearchCommand;
        public ICommand PlayerSearchCommand
        {
            get
            {
                return _playerSearchCommand ?? (_playerSearchCommand = new DelegateCommand<object>(param =>
                {
                    int? siteId = null;
                    string playerName = null;
                    bool? anon = null;
                    string gameType = null;
                    int? minCashHands = null;
                    int? maxCashHands = null;
                    int? minTourneyHands = null;
                    int? maxTourneyHands = null;
                    string[] orderByFields = null;
                    string order = null;
                    int? limit = null;
                    int? offset = null;
                    var start = DateTime.Now;
                    bool? success;
                    _dispatcher.Invoke(() =>
                    {
                        ClientStatus = string.Empty;

                        // get hand from a file
                        var playerSearch = new PlayerSearch();
                        success = playerSearch.ShowDialog();
                        if (success != true)
                            return;

                        if (!string.IsNullOrEmpty(playerSearch.SiteId.Text))
                            siteId = Convert.ToInt32(playerSearch.SiteId.Text);

                        if (!string.IsNullOrEmpty(playerSearch.PlayerName.Text))
                            playerName = playerSearch.PlayerName.Text;

                        if (playerSearch.Anon.IsChecked != null)
                            anon = Convert.ToBoolean(playerSearch.Anon.IsChecked);

                        if (!string.IsNullOrEmpty(playerSearch.GameType.Text))
                            gameType = playerSearch.GameType.Text;

                        if (!string.IsNullOrEmpty(playerSearch.MinCashHands.Text))
                            minCashHands = Convert.ToInt32(playerSearch.MinCashHands.Text);

                        if (!string.IsNullOrEmpty(playerSearch.MaxCashHands.Text))
                            maxCashHands = Convert.ToInt32(playerSearch.MaxCashHands.Text);

                        if (!string.IsNullOrEmpty(playerSearch.MinTourneyHands.Text))
                            minTourneyHands = Convert.ToInt32(playerSearch.MinTourneyHands.Text);

                        if (!string.IsNullOrEmpty(playerSearch.MaxTourneyHands.Text))
                            maxTourneyHands = Convert.ToInt32(playerSearch.MaxTourneyHands.Text);

                        if (!string.IsNullOrEmpty(playerSearch.Limit.Text))
                            limit = Convert.ToInt32(playerSearch.Limit.Text);

                        if (!string.IsNullOrEmpty(playerSearch.Offset.Text))
                            offset = Convert.ToInt32(playerSearch.Offset.Text);
                        if (!string.IsNullOrEmpty(playerSearch.OrderBy.Text))
                        {
                            orderByFields = playerSearch.OrderBy.Text.Split(',');
                            for (var i = 0; i < orderByFields.Length; i++)
                                orderByFields[i] = orderByFields[i].Trim();
                        }
                        if (!string.IsNullOrEmpty(playerSearch.Order.Text))
                            order = playerSearch.Order.Text.Trim();
                    });


                    _adapter.QueryPlayers(siteId, playerName, anon, gameType, minCashHands, maxCashHands,
                        minTourneyHands, maxTourneyHands, orderByFields.ToList(), order, limit, offset, DoQueryPlayersCallback);
                }, param => true));
            }
        }

        private bool DoQueryPlayersCallback(QueryPlayersResult result, IntPtr userdata)
        {
            _dispatcher.BeginInvoke(new Action(() =>
            {
                Players.Clear();
                foreach (var player in result.Players)
                {
                    Players.Add(new PlayerInfo
                    {
                        Anon = player.Anon,
                        Name = player.Name,
                        SiteId = player.SiteId,
                        CashHands = player.CashHands,
                        TournamentHands = player.TournamentHands
                    });
                }
            }));
            return true;
        }

        private bool DoQueryNotesCallback(QueryNotesResult result, IntPtr userData)
        {
            foreach (var playerNote in result.PlayerNotes)
            {
                _dispatcher.Invoke(new Action(() =>
                {
                    // todo: include site id in search
                    var player =
                        Players.FirstOrDefault(p => p.Name.Equals(playerNote.Player)); //&& p.SiteId.Equals(siteId)//);
                    if (player != null)
                        player.Note = playerNote.Note;
                }));
            }

            return true;
        }

        private DelegateCommand<object> _queryNotesCommand;
        public ICommand QueryNotesCommand
        {
            get
            {
                return _queryNotesCommand ?? (_queryNotesCommand = new DelegateCommand<object>(param =>
                {
                    var start = DateTime.Now;
                    ClientStatus = string.Empty;

                    var siteIds = new HashSet<int>();
                    foreach (var player in SelectedPlayers)
                        siteIds.Add(player.SiteId);
                    foreach (var siteId in siteIds)
                    {
                        var playerNames = SelectedPlayers.Where(p => p.SiteId == siteId).Select(p => p.Name);
                        var success = _adapter.QueryNotes(siteId, playerNames, DoQueryNotesCallback);
                    }
                }, param => true));
            }
        }

        private DelegateCommand<object> _changeHudProfileCommand;
        private bool _changeHudProfileIsDefault = true;

        private bool GetSetting(string name, out object value)
        {
            var success = _adapter.GetSetting(name, out value);
            LogMessage($@"GetSetting({name} returns {success}, value {value}");
            var priorInfo = Settings.FirstOrDefault(s => s.Name.Equals(name));
            var status = success ? "OK" : "ERROR";
            var current = priorInfo ?? new SettingInfo();
            current.Value = success ? value.ToString() : "";
            current.Fetches++;
            current.Status = status;
            if (priorInfo == null)
                current.Name = name;
            else
                Settings.Remove(priorInfo);
            Settings.Add(current);

            return success;
        }

        private List<Table> _tableInfo = new List<Table>();

        public ICommand ChangeHudProfileCommand
        {
            get
            {
                return _changeHudProfileCommand ?? (_changeHudProfileCommand = new DelegateCommand<object>(param =>
                {
                    if (!(GetSetting("available_hud_profiles", out var value) && value is string[]))
                    {
                        LogMessage("Error getting available_hud_profiles setting.");
                        return;
                    }

                    string profileCash, profileTourney;
                    if (Tracker == Tracker.PT4)
                    {
                        profileCash = "Cash - " + (_changeHudProfileIsDefault ? "Professional" : "Default");
                        profileTourney = "Tourney - " + (_changeHudProfileIsDefault ? "Professional" : "Default");
                    }
                    else
                    {
                        profileCash = "Cash - " + (_changeHudProfileIsDefault ? "HM3 Standard" : "Default");
                        profileTourney = "Tourney - " + (_changeHudProfileIsDefault ? "HM3 Standard" : "Default");
                    }

                    _changeHudProfileIsDefault = !_changeHudProfileIsDefault;

                    foreach (var info in _tableInfo)
                    {
                        _adapter.ChangeHudProfile(info.SiteId, info.TableName, info.IsTourney ? profileTourney : profileCash);
                    }

                }, param => true));
            }
        }

        public ICommand ChangeDatabaseCommand
        {
            get
            {
                return _changeDatabaseCommand ?? (_changeDatabaseCommand = new DelegateCommand<object>(param =>
                {
                    var openFileDialog = new OpenFileDialog
                    {
                        Multiselect = true,
                        Filter = "Database files (*.hmdb)|*.hmdb|All files (*.*)|*.*",
                        InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                    };
                    if (openFileDialog.ShowDialog() == true)
                    {
                        var fileName = openFileDialog.FileName;
                        var result = _adapter.ChangeDatabase(fileName);
                    }


                }, param => true));
            }
        }

        public bool DoImportHudProfileCallback(int callerId, bool errored, int errorCode, string errMsg, IntPtr userData)
        {
            _dispatcher.BeginInvoke(new Action(() =>
            {
                AddClientText("Hud Profile Imported: " + ImportHudProfileFileName);
            }));
            return true;
        }


        private DelegateCommand<object> _importHudProfileCommand;
        public ICommand ImportHudProfileCommand
        {
            get
            {
                return _importHudProfileCommand ?? (_importHudProfileCommand = new DelegateCommand<object>(param =>
                {
                    var start = DateTime.Now;
                    var tableType = ImportHudProfileTableType == "cash" ? TableType.Cash : TableType.Tournament;
                    var success = _adapter.ImportHudProfile(ImportHudProfileFileName, ImportHudProfileProfileName, tableType, DoImportHudProfileCallback);
                }, param => true));
            }
        }


        // temp solution until we can query the tracker for site ids
        private readonly int[] _ptSiteIds =
        {
            100, 200, 300, 400, 500, 600, 700, 900, 1000, 1100, 1200, 1300, 1400, 1500, 1600, 1700, 2000, 2100, 2200,
            2300, 2400, 2600, 2800, 2900, 2700, 3100, 3200,
            3300, 3400, 3500, 3600, 3700, 3800, 3900, 4000, 4100, 4200, 4300, 4400, 4500, 4600, 4700
        };

        public ICommand GetSiteAutoimportEnabledCommand
        {
            get
            {
                return _getSiteAutoimportEnabledCommand ?? (_getSiteAutoimportEnabledCommand = new DelegateCommand<object>(param =>
                {
                    foreach (var ptSiteId in _ptSiteIds)
                    {
                        var settingName = "is_auto_import_enabled_for_" + ptSiteId;
                        GetSetting(settingName, out var isEnabled);
                    }
                }, param => true));
            }
        }

        private bool ReplayHands()
        {
            var handSelectors = Hands.Select(h => new HandSelector
            {
                HandNo = h.HandNo,
                SiteId = h.SiteId,
                Street = 0,
                Action = 0
            }).ToList();
            var success = _adapter.ReplayHands(handSelectors);
            return success;
        }

        private DelegateCommand<object> _replayHandsCommand;
        public ICommand ReplayHandsCommand
        {
            get
            {
                return _replayHandsCommand ?? (_replayHandsCommand = new DelegateCommand<object>(param =>
                {
                    ClientStatus = string.Empty;
                    ReplayHands();
                }, param => true));
            }
        }

        private DelegateCommand<object> _clearHandsCommand;
        public ICommand ClearHandsCommand
        {
            get
            {
                return _clearHandsCommand ?? (_clearHandsCommand = new DelegateCommand<object>(param =>
                {
                    _dispatcher.Invoke(() => Hands.Clear());
                    RaisePropertyChanged("HandsTooltip");
                }, param => true));
            }
        }

        private DelegateCommand<object> _clearStatsCommand;
        public ICommand ClearStatsCommand
        {
            get
            {
                return _clearStatsCommand ?? (_clearStatsCommand = new DelegateCommand<object>(param =>
                {
                    _dispatcher.Invoke(() =>
                    {
                        Stats.Clear();
                        NeedsStats = true;
                    });
                }, param => true));
            }
        }

        private bool DoStatsChangedCallback()
        {
            _dispatcher.Invoke(() => { NeedsStats = true; });
            return true;
        }


        private DelegateCommand<object> _clearHandInfoCommand;
        public ICommand ClearHandInfoCommand
        {
            get
            {
                return _clearHandInfoCommand ?? (_clearHandInfoCommand = new DelegateCommand<object>(param =>
                {
                    _dispatcher.Invoke(() =>
                    {
                        var replace = Hands.Select(h =>
                            new HandInfo
                            {
                                SiteId = h.SiteId,
                                HandNo = h.HandNo,
                                MenuItem = h.MenuItem,
                                HandSelected = h.HandSelected
                            }).ToList();
                        Hands.Clear();
                        foreach (var item in replace)
                            Hands.Add(item);
                        RaisePropertyChanged("HandsTooltip");
                    });
                }, param => true));
            }
        }

        private bool _lastRegisterNoteTabsCommandSuccess;
        private DelegateCommand<object> _registerNoteTabsCommand;
        public ICommand RegisterNoteTabsCommand
        {
            get
            {
                return _registerNoteTabsCommand ?? (_registerNoteTabsCommand = new DelegateCommand<object>(param =>
                {
                    const string tabIcon = ".\\\\Data\\\\Images\\\\Icons\\\\link_break.png";
                    _lastRegisterNoteTabsCommandSuccess = true;
                    if (!RegisterNoteTab("tab1", tabIcon) || !RegisterNoteTab("tab2", tabIcon) || !RegisterNoteTab("tab3", tabIcon))
                        _lastRegisterNoteTabsCommandSuccess = false;
                }, param => true));
            }
        }

        private bool _lastRegisterHandsMenuCommandSuccess;
        private DelegateCommand<object> _registerHandsMenuCommand;
        public ICommand RegisterHandsMenuCommand
        {
            get
            {
                return _registerHandsMenuCommand ?? (_registerHandsMenuCommand = new DelegateCommand<object>(param =>
                {
                    string menuIcon = UseCustomHandsMenuIcon ? ".\\\\Data\\\\Images\\\\Icons\\\\link_break.png" : null;
                    List<string> menuItems = null;
                    switch (HandsMenuOption)
                    {
                        case 0:
                            menuItems = new List<string> { "apple", "banana", "cherry" };
                            break;
                        case 1:
                            menuItems = new List<string> { "pear" };
                            break;
                        case 2:
                            menuItems = new List<string> { "" };
                            break;
                    }

                    _lastRegisterHandsMenuCommandSuccess = true;
                    if (!_adapter.RegisterHandsMenu(menuItems, menuIcon, (HandFormat)HandsMenuHandFormat))
                        _lastRegisterHandsMenuCommandSuccess = false;
                }, param => true));
            }
        }

        private bool _lastRequestHandsCommandSuccess;
        private DelegateCommand<object> _requestHandsCommand;
        public ICommand RequestHandsCommand
        {
            get
            {
                return _requestHandsCommand ?? (_requestHandsCommand = new DelegateCommand<object>(param =>
                {
                    var result = _lastRequestHandsCommandSuccess = _adapter.RequestHands();
                }, param => true));
            }
        }

        private DelegateCommand<object> _hideCommand;
        public ICommand HideCommand
        {
            get
            {
                return _hideCommand ?? (_hideCommand = new DelegateCommand<object>(param =>
                {
                    _dispatcher.Invoke(() => { IsVisible = false; });
                    _adapter.RegisterMenu(MenuItems);
                }, param => true));
            }
        }


        private DelegateCommand<object> _showCommand;
        public ICommand ShowCommand
        {
            get
            {
                return _showCommand ?? (_showCommand = new DelegateCommand<object>(param =>
                {
                    _dispatcher.Invoke(() => { IsVisible = true; });
                    _adapter.RegisterMenu(MenuItems);
                }, param => true));
            }
        }

        private bool _lastRequestTablesCommandSuccess;
        private DelegateCommand<object> _hudRequestTablesCommand;
        private ICommand _getSiteAutoimportEnabledCommand;

        public ICommand RequestTablesCommand
        {
            get
            {
                return _hudRequestTablesCommand ?? (_hudRequestTablesCommand = new DelegateCommand<object>(param =>
                {
                    var start = DateTime.Now;
                    var result = _lastRequestTablesCommandSuccess = _adapter.RequestTables();
                    var dateDiff = DateTime.Now - start;
                    AddClientText(RequestLabel + "RequestTables");
                    AddClientText(ClientResponseLine(dateDiff, "result=" + result));
                }, param => true));
            }
        }

        private DelegateCommand<object> _runHmqlQueryCommand;
        public ICommand RunHmqlQueryCommand
        {
            get
            {
                return _runHmqlQueryCommand ?? (_runHmqlQueryCommand = new DelegateCommand<object>(param =>
                {
                    var success = _adapter.QueryHmql(HmqlQueryText, DoQueryHmqlCallback);
                }, param => true));
            }
        }

        private bool DoQueryHmqlCallback(QueryHmqlResult result, IntPtr userData)
        {
            _dispatcher.BeginInvoke(new Action(() =>
            {
                var queryFields = GetQueryFields(HmqlQueryText);
                var dataTable = new DataTable();

                foreach (var field in queryFields)
                    dataTable.Columns.Add(field);

                foreach (var row in result.Values)
                {
                    var rowData = new List<object>();
                    var fieldCount = 0;
                    for (var index = 0; index < queryFields.Length; index++)
                    {
                        if (fieldCount >= row.Length) break;
                        rowData.Add(row[fieldCount++].Value);
                    }

                    dataTable.Rows.Add(rowData.ToArray());
                }
                HmqlQueryResults = dataTable;
            }));
            return true;
        }

        public string[] PtsqlQueryStatsArray => PtsqlQueryStats.Split(',').Select(s => s.Trim()).ToArray();

        private DelegateCommand<object> _runPtsqlQueryCommand;
        public ICommand RunPtsqlQueryCommand
        {
            get
            {
                return _runPtsqlQueryCommand ?? (_runPtsqlQueryCommand = new DelegateCommand<object>(param =>
                {
                    var tableType = PtsqlQueryTableType.ToLower() == "cash" ? TableType.Cash : TableType.Tournament;
                    var filters = ""; // todo
                    var orderByStats = new string[] { }; // todo
                    var orderByDesc = false;
                    var success = _adapter.QueryPtsql(tableType, PtsqlQueryStatsArray, filters, orderByStats, orderByDesc, PtsqlQueryActivePlayer, PtsqlQueryHandQuery, DoQueryPtsqlCallback);
                }, param => true));
            }
        }


        private bool DoQueryPtsqlCallback(QueryStatsResult result, IntPtr userData)
        {
            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                var dataTable = new DataTable();

                var stats = PtsqlQueryStats.Split(',');

                // see: https://stackoverflow.com/questions/2940618/what-is-it-about-datatable-column-names-with-dots-that-makes-them-unsuitable-for
                foreach (var stat in stats)
                    dataTable.Columns.Add(stat.Replace(".", "\x2024"));

                foreach (var statValues in result.PlayerStatValues)
                {
                    var values = new List<object>();
                    foreach (var statValue in statValues)
                    {
                        var displayValue = statValue.Value;
                        if (!string.IsNullOrEmpty(statValue.PctDetail))
                            displayValue += " (" + statValue.PctDetail + ")";
                        values.Add(displayValue);
                    }

                    var valuesArray = values.ToArray();
                    dataTable.Rows.Add(valuesArray);
                }

                PtsqlQueryResults = dataTable;
            }));
            return true;
        }

        private bool GetPtsqlHandIdentifierColumns(out int siteId, out int handNo)
        {
            handNo = -1;
            siteId = -1;
            int columnCount = 0;

            foreach (DataColumn column in PtsqlQueryResults.Columns)
            {
                if (column.ColumnName.Equals("Hand #"))
                    handNo = columnCount;
                if (column.ColumnName.Equals("Site"))
                    siteId = columnCount;
                columnCount++;
            }

            return handNo != -1 && siteId != -1;
        }

        private DelegateCommand<object> _ptsqlQueryAddToHandsCommand;
        public ICommand PtsqlQueryAddToHandsCommand
        {
            get
            {
                return _ptsqlQueryAddToHandsCommand ?? (_ptsqlQueryAddToHandsCommand = new DelegateCommand<object>(param =>
                {
                    if (!GetPtsqlHandIdentifierColumns(out int siteIdColumn, out int handNoColumn))
                        return;

                    foreach (DataRowView dataRowView in SelectedPtsqlQueryResultsRows)
                    {
                        var siteId = Convert.ToInt32(dataRowView.Row.ItemArray[siteIdColumn]);
                        var handNo = dataRowView.Row.ItemArray[handNoColumn].ToString();
                        _dispatcher.Invoke(() => Hands.Add(new HandInfo { SiteId = siteId, HandNo = handNo }));
                    }
                    RaisePropertyChanged("HandsTooltip");
                }, param => true));
            }
        }

        private bool DoQueryStatsCallback(QueryStatsResult result, IntPtr userData)
        {
            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                var dataTable = new DataTable();

                var stats = StatQueryStats.Split(',');
                var players = StatQueryPlayers.Split(',');
                
                if (!stats.Contains("PlayerName"))
                    dataTable.Columns.Add("PlayerName");

                // see: https://stackoverflow.com/questions/2940618/what-is-it-about-datatable-column-names-with-dots-that-makes-them-unsuitable-for
                foreach (var stat in stats)
                    dataTable.Columns.Add(stat.Replace(".", "\x2024"));

                int count = 0;
                foreach (var statValues in result.PlayerStatValues)
                {
                    var values = new List<object>();
                    if (!stats.Contains("PlayerName"))
                        values.Add(players[count]);
                    foreach (var statValue in statValues)
                    {
                        var displayValue = statValue.Value;
                        if (!string.IsNullOrEmpty(statValue.PctDetail))
                            displayValue += " (" + statValue.PctDetail + ")";
                        values.Add(displayValue);
                    }
                    count++;

                    var valuesArray = values.ToArray();
                    dataTable.Rows.Add(valuesArray);
                }

                StatQueryResults = dataTable;
            }));
            return true;
        }


        private DelegateCommand<object> _runStatQueryCommand;
        public ICommand RunStatQueryCommand
        {
            get
            {
                return _runStatQueryCommand ?? (_runStatQueryCommand = new DelegateCommand<object>(param =>
                {
                    var start = DateTime.Now;
                    var statQueryPlayersList = StatQueryPlayers.Split(',');
                    var statQueryStatsList = StatQueryStats.Split(',');
                    var siteId = Convert.ToInt32(StatQuerySiteId);

                    // setup the data table for the results that will come later
                    var dataTable = StatQueryResults = new DataTable();

                    var stats = StatQueryStats.Split(',');
                    for (var index = 0; index < stats.Length; index++)
                        stats[index] = stats[index].Trim();

                    var players = StatQueryPlayers.Split(',');
                    for (var index = 0; index < players.Length; index++)
                        players[index] = players[index].Trim();

                    if (!stats.Contains("PlayerName"))
                        dataTable.Columns.Add("PlayerName");

                    // see: https://stackoverflow.com/questions/2940618/what-is-it-about-datatable-column-names-with-dots-that-makes-them-unsuitable-for
                    foreach (var stat in stats)
                        dataTable.Columns.Add(stat.Replace(".", "\x2024"));

                    var tableType = IsCash ? TableType.Cash : TableType.Tournament;
                    bool success = _adapter.QueryStats(tableType, siteId, statQueryPlayersList, statQueryStatsList, StatQueryFilters, DoQueryStatsCallback);
                    
                }, param => true));
            }
        }

        
        private DelegateCommand<object> _statQueryUseCurrentPlayerCommand;
        public ICommand StatQueryUseCurrentPlayerCommand
        {
            get
            {
                return _statQueryUseCurrentPlayerCommand ?? (_statQueryUseCurrentPlayerCommand = new DelegateCommand<object>(param =>
                {
                    _dispatcher.Invoke(() =>
                    {
                        StatQueryPlayers = CurrentPlayerName;
                        StatQuerySiteId = CurrentPlayerSite;
                    });
                }, param => true));
            }
        }

        private DelegateCommand<object> _statQueryUseSelectedPlayersCommand;
        public ICommand StatQueryUseSelectedPlayersCommand
        {
            get
            {
                return _statQueryUseSelectedPlayersCommand ?? (_statQueryUseSelectedPlayersCommand = new DelegateCommand<object>(param =>
                {
                    if (SelectedPlayers.Count <= 0) return;
                    _dispatcher.Invoke(() => StatQueryPlayers = string.Join(",",SelectedPlayers.Select(sp => sp.Name)));
                }, param => true));
            }
        }

        private DelegateCommand<object> _statQueryUseSelectedStatsCommand;
        public ICommand StatQueryUseSelectedStatsCommand
        {
            get
            {
                return _statQueryUseSelectedStatsCommand ?? (_statQueryUseSelectedStatsCommand = new DelegateCommand<object>(param =>
                {
                    if (SelectedStats.Count <= 0) return;
                    _dispatcher.Invoke(() => StatQueryStats = string.Join(",", SelectedStats.Select(sp => sp.Stat)));
                }, param => true));
            }
        }

        private DelegateCommand<object> _clearStatQueryResultsCommand;
        public ICommand ClearStatQueryResultsCommand
        {
            get
            {
                return _clearStatQueryResultsCommand ?? (_clearStatQueryResultsCommand = new DelegateCommand<object>(param =>
                {
                    _dispatcher.Invoke(() => StatQueryResults.Clear());
                }, param => true));
            }
        }

        private DelegateCommand<object> _cancelCallbackCommand;
        public ICommand CancelCallbackCommand
        {
            get
            {
                return _cancelCallbackCommand ?? (_cancelCallbackCommand = new DelegateCommand<object>(param =>
                {
                    var start = DateTime.Now;
                    /*
                    var cancelRequestId = _outboundRequestId;
                    _outbound.CancelCallback(++_outboundRequestId, cancelRequestId, out var responseStr);
                    var dateDiff = DateTime.Now - start;
                    AddClientText(RequestLabel + _outbound.PriorRequest);
                    AddClientText(ClientResponseLine(dateDiff, responseStr));
                    */
                }, param => true));
            }
        }

        private bool _sendingBrokenResponses;
        private bool _disableUnsavedChangesSupport;
        private ICommand _changeDatabaseCommand;

        public bool SendingBrokenResponses
        {
            get => _sendingBrokenResponses;
            set
            {
                _sendingBrokenResponses = value;
                _adapter.SendingBrokenResponses = value;
                RaisePropertyChanged();
            }
        }

        public bool SendingBrokenRequests
        {
            get => _sendingBrokenRequests;
            set
            {
                _sendingBrokenRequests = value;
                _adapter.BreakRequests = value;
                RaisePropertyChanged();
            }
        }
    }
}

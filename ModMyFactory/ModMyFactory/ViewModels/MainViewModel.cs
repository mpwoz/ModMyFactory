﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using ModMyFactory.Export;
using ModMyFactory.Helpers;
using ModMyFactory.Lang;
using Ookii.Dialogs.Wpf;
using ModMyFactory.Models;
using ModMyFactory.MVVM.Sorters;
using ModMyFactory.Views;
using ModMyFactory.Web;
using ModMyFactory.Web.ModApi;
using WPFCore;
using WPFCore.Commands;

namespace ModMyFactory.ViewModels
{
    sealed class MainViewModel : ViewModelBase
    {
        static MainViewModel instance;

        public static MainViewModel Instance => instance ?? (instance = new MainViewModel());

        public MainWindow Window => (MainWindow)View;

        #region AvailableCultures

        public List<CultureEntry> AvailableCultures { get; }

        public ListCollectionView AvailableCulturesView { get; }

        #endregion

        #region FactorioVersions

        ObservableCollection<FactorioVersion> factorioVersions;
        CollectionViewSource factorioVersionsSource;
        ListCollectionView factorioVersionsView;
        FactorioVersion selectedFactorioVersion;

        private bool FactorioVersionFilter(object item)
        {
            FactorioVersion factorioVersion = item as FactorioVersion;
            return factorioVersion?.IsSpecialVersion == false;
        }

        public ObservableCollection<FactorioVersion> FactorioVersions
        {
            get { return factorioVersions; }
            private set
            {
                if (value != factorioVersions)
                {
                    factorioVersions = value;
                    OnPropertyChanged(new PropertyChangedEventArgs(nameof(FactorioVersions)));

                    if (factorioVersionsSource == null) factorioVersionsSource = new CollectionViewSource();
                    factorioVersionsSource.Source = factorioVersions;
                    var factorioVersionsView = (ListCollectionView)factorioVersionsSource.View;
                    factorioVersionsView.CustomSort = new FactorioVersionSorter();
                    factorioVersionsView.Filter = FactorioVersionFilter;
                    FactorioVersionsView = factorioVersionsView;
                }
            }
        }

        public ListCollectionView FactorioVersionsView
        {
            get { return factorioVersionsView; }
            private set
            {
                if (value != factorioVersionsView)
                {
                    factorioVersionsView = value;
                    OnPropertyChanged(new PropertyChangedEventArgs(nameof(FactorioVersionsView)));
                }
            }
        }

        private void SelectedFactorioVersionPropertyChangedHandler(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(FactorioVersion.VersionString))
            {
                App.Instance.Settings.SelectedVersion = selectedFactorioVersion.VersionString;
                App.Instance.Settings.Save();
            }
        }

        public FactorioVersion SelectedFactorioVersion
        {
            get { return selectedFactorioVersion; }
            set
            {
                if (value != selectedFactorioVersion)
                {
                    if (selectedFactorioVersion != null)
                        selectedFactorioVersion.PropertyChanged -= SelectedFactorioVersionPropertyChangedHandler;
                    selectedFactorioVersion = value;
                    if (selectedFactorioVersion != null)
                        selectedFactorioVersion.PropertyChanged += SelectedFactorioVersionPropertyChangedHandler;

                    OnPropertyChanged(new PropertyChangedEventArgs(nameof(SelectedFactorioVersion)));

                    string newVersionString = selectedFactorioVersion?.VersionString ?? string.Empty;
                    if (newVersionString != App.Instance.Settings.SelectedVersion)
                    {
                        App.Instance.Settings.SelectedVersion = newVersionString;
                        App.Instance.Settings.Save();
                    }
                }
            }
        }

        #endregion

        #region Mods

        string modFilterPattern;
        bool? allModsActive;
        bool allModsSelectedChanging;
        ModCollection mods;
        CollectionViewSource modsSource;
        ListCollectionView modsView;

        public string ModFilterPattern
        {
            get { return modFilterPattern; }
            set
            {
                if (value != modFilterPattern)
                {
                    modFilterPattern = value;
                    OnPropertyChanged(new PropertyChangedEventArgs(nameof(ModFilterPattern)));

                    ModsView.Refresh();
                }
            }
        }

        private bool ModFilter(object item)
        {
            Mod mod = item as Mod;
            if (mod == null) return false;

            if (string.IsNullOrWhiteSpace(ModFilterPattern)) return true;
            return StringHelper.FilterIsContained(ModFilterPattern, $"{mod.Title} {mod.Author}");
        }

        public bool? AllModsActive
        {
            get { return allModsActive; }
            set
            {
                if (value != allModsActive)
                {
                    allModsActive = value;
                    allModsSelectedChanging = true;

                    if (allModsActive.HasValue)
                    {
                        ModManager.BeginUpdateTemplates();

                        foreach (var mod in Mods)
                        {
                            if (mod.Active != allModsActive.Value)
                                mod.Active = allModsActive.Value;
                        }

                        ModManager.EndUpdateTemplates();
                        ModManager.SaveTemplates();
                    }

                    allModsSelectedChanging = false;
                    OnPropertyChanged(new PropertyChangedEventArgs(nameof(AllModsActive)));
                }
            }
        }

        private void SetAllModsActive()
        {
            if (Mods.Count == 0 || allModsSelectedChanging)
                return;

            bool? newValue = Mods[0].Active;
            for (int i = 1; i < Mods.Count; i++)
            {
                if (Mods[i].Active != newValue)
                {
                    newValue = null;
                    break;
                }
            }

            if (newValue != allModsActive)
            {
                allModsActive = newValue;
                OnPropertyChanged(new PropertyChangedEventArgs(nameof(AllModsActive)));
            }
        }

        private void ModPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Mod.Active))
            {
                SetAllModsActive();
            }
        }

        private void ModsChangedHandler(object sender, NotifyCollectionChangedEventArgs e)
        {
            switch (e.Action)
            {
                case NotifyCollectionChangedAction.Add:
                    foreach (Mod mod in e.NewItems)
                        mod.PropertyChanged += ModPropertyChanged;
                    SetAllModsActive();
                    break;
                case NotifyCollectionChangedAction.Remove:
                    foreach (Mod mod in e.OldItems)
                        mod.PropertyChanged -= ModPropertyChanged;
                    SetAllModsActive();
                    break;
                case NotifyCollectionChangedAction.Reset:
                    if (e.NewItems != null)
                    {
                        foreach (Mod mod in e.NewItems)
                        mod.PropertyChanged += ModPropertyChanged;
                    }
                    if (e.OldItems != null)
                    {
                        foreach (Mod mod in e.OldItems)
                        mod.PropertyChanged -= ModPropertyChanged;
                    }
                    SetAllModsActive();
                    break;
            }
        }

        public ModCollection Mods
        {
            get { return mods; }
            private set
            {
                if (value != mods)
                {
                    mods = value;
                    OnPropertyChanged(new PropertyChangedEventArgs(nameof(Mods)));

                    if (modsSource == null) modsSource = new CollectionViewSource();
                    modsSource.Source = mods;
                    var modsView = (ListCollectionView)modsSource.View;
                    modsView.CustomSort = new ModSorter();
                    modsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(Mod.FactorioVersion)));
                    modsView.Filter = ModFilter;
                    mods.CollectionChanged += ModsChangedHandler;
                    ModsView = modsView;

                    SetAllModsActive();
                }
            }
        }

        public ListCollectionView ModsView
        {
            get { return modsView; }
            private set
            {
                if (value != modsView)
                {
                    modsView = value;
                    OnPropertyChanged(new PropertyChangedEventArgs(nameof(ModsView)));
                }
            }
        }

        #endregion

        #region Modpacks

        string modpackFilterPattern;
        bool? allModpacksActive;
        bool allModpacksSelectedChanging;
        ModpackCollection modpacks;
        CollectionViewSource modpacksSource;
        ListCollectionView modpacksView;

        public string ModpackFilterPattern
        {
            get { return modpackFilterPattern; }
            set
            {
                if (value != modpackFilterPattern)
                {
                    modpackFilterPattern = value;
                    OnPropertyChanged(new PropertyChangedEventArgs(nameof(ModpackFilterPattern)));

                    ModpacksView.Refresh();
                }
            }
        }

        private bool ModpackFilter(object item)
        {
            Modpack modpack = item as Modpack;
            if (modpack == null) return false;

            if (string.IsNullOrWhiteSpace(ModpackFilterPattern)) return true;
            return StringHelper.FilterIsContained(ModpackFilterPattern, modpack.Name);
        }

        public bool? AllModpacksActive
        {
            get { return allModpacksActive; }
            set
            {
                if (value != allModpacksActive)
                {
                    allModpacksActive = value;
                    allModpacksSelectedChanging = true;

                    if (allModpacksActive.HasValue)
                    {
                        ModManager.BeginUpdateTemplates();

                        foreach (var modpack in Modpacks)
                        {
                            if (modpack.Active != allModpacksActive.Value)
                                modpack.Active = allModpacksActive.Value;
                        }

                        ModManager.EndUpdateTemplates();
                        ModManager.SaveTemplates();
                    }

                    allModpacksSelectedChanging = false;
                    OnPropertyChanged(new PropertyChangedEventArgs(nameof(AllModpacksActive)));
                }
            }
        }

        private void SetAllModpacksActive()
        {
            if (Modpacks.Count == 0 || allModpacksSelectedChanging)
                return;

            bool? newValue = Modpacks[0].Active;
            for (int i = 1; i < Modpacks.Count; i++)
            {
                if (Modpacks[i].Active != newValue)
                {
                    newValue = null;
                    break;
                }
            }

            if (newValue != allModpacksActive)
            {
                allModpacksActive = newValue;
                OnPropertyChanged(new PropertyChangedEventArgs(nameof(AllModpacksActive)));
            }
        }

        private void ModpackPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Modpack.Active))
            {
                SetAllModpacksActive();
            }
        }

        private void ModpacksChangedHandler(object sender, NotifyCollectionChangedEventArgs e)
        {
            switch (e.Action)
            {
                case NotifyCollectionChangedAction.Add:
                    foreach (Modpack modpack in e.NewItems)
                        modpack.PropertyChanged += ModpackPropertyChanged;
                    SetAllModpacksActive();
                    break;
                case NotifyCollectionChangedAction.Remove:
                    foreach (Modpack modpack in e.OldItems)
                        modpack.PropertyChanged -= ModpackPropertyChanged;
                    SetAllModpacksActive();
                    break;
                case NotifyCollectionChangedAction.Reset:
                    if (e.NewItems != null)
                    {
                        foreach (Modpack modpack in e.NewItems)
                        modpack.PropertyChanged += ModpackPropertyChanged;
                    }
                    if (e.OldItems != null)
                    { foreach (Modpack modpack in e.OldItems)
                        modpack.PropertyChanged -= ModpackPropertyChanged;
                    }
                    SetAllModpacksActive();
                    break;
            }
        }

        public ModpackCollection Modpacks
        {
            get { return modpacks; }
            set
            {
                if (value != modpacks)
                {
                    modpacks = value;
                    OnPropertyChanged(new PropertyChangedEventArgs(nameof(Modpacks)));

                    if (modpacksSource == null) modpacksSource = new CollectionViewSource();
                    modpacksSource.Source = modpacks;
                    var modpacksView = (ListCollectionView)modpacksSource.View;
                    modpacksView.CustomSort = new ModpackSorter();
                    modpacksView.Filter = ModpackFilter;
                    modpacks.CollectionChanged += ModpacksChangedHandler;
                    ModpacksView = modpacksView;

                    SetAllModpacksActive();
                }
            }
        }

        public ListCollectionView ModpacksView
        {
            get { return modpacksView; }
            set
            {
                if (value != modpacksView)
                {
                    modpacksView = value;
                    OnPropertyChanged(new PropertyChangedEventArgs(nameof(ModpacksView)));
                }
            }
        }

        #endregion

        #region GridLengths

        GridLength modGridLength;
        GridLength modpackGridLength;

        public GridLength ModGridLength
        {
            get { return modGridLength; }
            set
            {
                if (value != modGridLength)
                {
                    modGridLength = value;
                    OnPropertyChanged(new PropertyChangedEventArgs(nameof(ModGridLength)));

                    App.Instance.Settings.ModGridLength = modGridLength;
                }
            }
        }

        public GridLength ModpackGridLength
        {
            get { return modpackGridLength; }
            set
            {
                if (value != modpackGridLength)
                {
                    modpackGridLength = value;
                    OnPropertyChanged(new PropertyChangedEventArgs(nameof(ModpackGridLength)));

                    App.Instance.Settings.ModpackGridLength = modpackGridLength;
                }
            }
        }

        #endregion

        #region Commands

        public RelayCommand DownloadModsCommand { get; }

        public RelayCommand AddModsFromFilesCommand { get; }

        public RelayCommand AddModFromFolderCommand { get; }

        public RelayCommand CreateModpackCommand { get; }

        public RelayCommand CreateLinkCommand { get; }

        public RelayCommand ExportModpacksCommand { get; }

        public RelayCommand ImportModpacksCommand { get; }

        public RelayCommand StartGameCommand { get; }

        public RelayCommand OpenFactorioFolderCommand { get; }

        public RelayCommand OpenModFolderCommand { get; }

        public RelayCommand OpenSavegameFolderCommand { get; }

        public RelayCommand OpenScenarioFolderCommand { get; }

        public RelayCommand UpdateModsCommand { get; }

        public RelayCommand OpenVersionManagerCommand { get; }

        public RelayCommand OpenSettingsCommand { get; }

        public RelayCommand BrowseFactorioWebsiteCommand { get; }

        public RelayCommand BrowseModWebsiteCommand { get; }

        public RelayCommand BrowseForumThreadCommand { get; }

        public RelayCommand<bool> UpdateCommand { get; }

        public RelayCommand OpenAboutWindowCommand { get; }

        public RelayCommand BrowseWikiCommand { get; }

        public RelayCommand ActivateSelectedModsCommand { get; }

        public RelayCommand DeactivateSelectedModsCommand { get; }

        public RelayCommand DeleteSelectedModsCommand { get; }

        public RelayCommand SelectActiveModsCommand { get; }

        public RelayCommand SelectInactiveModsCommand { get; }

        public RelayCommand ActivateSelectedModpacksCommand { get; }

        public RelayCommand DeactivateSelectedModpacksCommand { get; }

        public RelayCommand DeleteSelectedModpacksCommand { get; }

        public RelayCommand SelectActiveModpacksCommand { get; }

        public RelayCommand SelectInactiveModpacksCommand { get; }

        public RelayCommand DeleteSelectedModsAndModpacksCommand { get; }

        public RelayCommand ClearModFilterCommand { get; }

        public RelayCommand ClearModpackFilterCommand { get; }

        public RelayCommand RefreshCommand { get; }

        #endregion

        volatile bool modpacksLoading;
        volatile bool updating;

        private void LoadFactorioVersions()
        {
            var installedVersions = FactorioVersion.GetInstalledVersions();
            var factorioVersions = new ObservableCollection<FactorioVersion>(installedVersions) { FactorioVersion.Latest };

            FactorioVersion steamVersion;
            if (FactorioSteamVersion.TryLoad(out steamVersion)) factorioVersions.Add(steamVersion);

            string versionString = App.Instance.Settings.SelectedVersion;
            FactorioVersions = factorioVersions;
            SelectedFactorioVersion = string.IsNullOrEmpty(versionString) ? null : FactorioVersions.FirstOrDefault(item => item.VersionString == versionString);
        }

        private void ModpacksCollectionChangedHandler(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (!modpacksLoading)
            {
                ModpackTemplateList.Instance.Update(Modpacks);
                ModpackTemplateList.Instance.Save();
            }
        }

        private void LoadModsAndModpacks()
        {
            modpacksLoading = true;

            if (Mods == null)
            {
                Mods = new ModCollection();
            }
            else
            {
                Mods.Clear();
            }

            if (Modpacks == null)
            {
                Modpacks = new ModpackCollection();
                Modpacks.CollectionChanged += ModpacksCollectionChangedHandler;
            }
            else
            {
                Modpacks.Clear();
            }

            
            Mod.LoadMods(Mods, Modpacks);
            ModpackTemplateList.Instance.PopulateModpackList(Mods, Modpacks, ModpacksView);

            modpacksLoading = false;
        }

        private void Refresh()
        {
            ModManager.LoadTemplates();
            LoadFactorioVersions();
            LoadModsAndModpacks();
        }

        private MainViewModel()
        {
            if (!App.IsInDesignMode) // Make view model designer friendly.
            {
                AvailableCultures = App.Instance.GetAvailableCultures();
                AvailableCulturesView = (ListCollectionView)CollectionViewSource.GetDefaultView(AvailableCultures);
                AvailableCulturesView.CustomSort = new CultureEntrySorter();
                AvailableCultures.First(entry =>
                    string.Equals(entry.LanguageCode, App.Instance.Settings.SelectedLanguage, StringComparison.InvariantCultureIgnoreCase)).Select();

                if (!Environment.Is64BitOperatingSystem && !App.Instance.Settings.WarningShown)
                {
                    MessageBox.Show(
                        App.Instance.GetLocalizedMessage("32Bit", MessageType.Information),
                        App.Instance.GetLocalizedMessageTitle("32Bit", MessageType.Information),
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
                App.Instance.Settings.WarningShown = true;

                Refresh();
                

                modGridLength = App.Instance.Settings.ModGridLength;
                modpackGridLength = App.Instance.Settings.ModpackGridLength;


                // 'File' menu
                DownloadModsCommand = new RelayCommand(async () => await DownloadMods());
                AddModsFromFilesCommand = new RelayCommand(async () => await AddModsFromFiles());
                AddModFromFolderCommand = new RelayCommand(async () => await AddModFromFolder());
                CreateModpackCommand = new RelayCommand(CreateNewModpack);
                CreateLinkCommand = new RelayCommand(CreateLink);

                ExportModpacksCommand = new RelayCommand(ExportModpacks);
                ImportModpacksCommand = new RelayCommand(async () => await ImportModpacks());

                StartGameCommand = new RelayCommand(StartGame, () => SelectedFactorioVersion != null);

                // 'Edit' menu
                UpdateModsCommand = new RelayCommand(async () => await UpdateMods());

                OpenVersionManagerCommand = new RelayCommand(OpenVersionManager);
                OpenSettingsCommand = new RelayCommand(async () => await OpenSettings());

                // 'View' menu
                OpenFactorioFolderCommand = new RelayCommand(() =>
                {
                    var factorioDirectory = App.Instance.Settings.GetFactorioDirectory();
                    if (!factorioDirectory.Exists) factorioDirectory.Create();
                    Process.Start(factorioDirectory.FullName);
                });
                OpenModFolderCommand = new RelayCommand(() =>
                {
                    var modDirectory = App.Instance.Settings.GetModDirectory();
                    if (!modDirectory.Exists) modDirectory.Create();
                    Process.Start(modDirectory.FullName);
                });
                OpenSavegameFolderCommand = new RelayCommand(() =>
                {
                    string savesPath = Path.Combine(App.Instance.AppDataPath, "saves");
                    if (!Directory.Exists(savesPath)) Directory.CreateDirectory(savesPath);
                    Process.Start(savesPath);
                });
                OpenScenarioFolderCommand = new RelayCommand(() =>
                {
                    string scenariosPath = Path.Combine(App.Instance.AppDataPath, "scenarios");
                    if (!Directory.Exists(scenariosPath)) Directory.CreateDirectory(scenariosPath);
                    Process.Start(scenariosPath);
                });

                RefreshCommand = new RelayCommand(Refresh);

                // 'Info' menu
                BrowseFactorioWebsiteCommand = new RelayCommand(() => Process.Start("https://www.factorio.com/"));
                BrowseModWebsiteCommand = new RelayCommand(() => Process.Start("https://mods.factorio.com/"));
                BrowseForumThreadCommand =  new RelayCommand(() => Process.Start("https://forums.factorio.com/viewtopic.php?f=137&t=33370"));

                UpdateCommand = new RelayCommand<bool>(async silent => await Update(silent), () => !updating);
                OpenAboutWindowCommand = new RelayCommand(OpenAboutWindow);
                BrowseWikiCommand = new RelayCommand(() => Process.Start("https://github.com/Artentus/ModMyFactory/wiki"));

                // context menu
                ActivateSelectedModsCommand = new RelayCommand(ActivateSelectedMods, () => Mods.Any(mod => mod.IsSelected));
                DeactivateSelectedModsCommand = new RelayCommand(DeactivateSelectedMods, () => Mods.Any(mod => mod.IsSelected));
                DeleteSelectedModsCommand = new RelayCommand(DeleteSelectedMods, () => Mods.Any(mod => mod.IsSelected));
                SelectActiveModsCommand = new RelayCommand(SelectActiveMods);
                SelectInactiveModsCommand = new RelayCommand(SelectInactiveMods);

                ActivateSelectedModpacksCommand = new RelayCommand(ActivateSelectedModpacks, () => Modpacks.Any(modpack => modpack.IsSelected));
                DeactivateSelectedModpacksCommand = new RelayCommand(DeactivateSelectedModpacks, () => Modpacks.Any(modpack => modpack.IsSelected));
                DeleteSelectedModpacksCommand = new RelayCommand(DeleteSelectedModpacks, () => Modpacks.Any(modpack => modpack.IsSelected));
                SelectActiveModpacksCommand = new RelayCommand(SelectActiveModpacks);
                SelectInactiveModpacksCommand = new RelayCommand(SelectInactiveModpacks);

                DeleteSelectedModsAndModpacksCommand = new RelayCommand(DeleteSelectedModsAndModpacks, () => Mods.Any(mod => mod.IsSelected) || Modpacks.Any(modpack => modpack.IsSelected));

                ClearModFilterCommand = new RelayCommand(() => ModFilterPattern = string.Empty);
                ClearModpackFilterCommand = new RelayCommand(() => ModpackFilterPattern = string.Empty);


                // New ModMyFactory instance started.
                Program.NewInstanceStarted += NewInstanceStartedHandler;
            }
        }

        #region AddMods

        private async Task DownloadMods()
        {
            if (OnlineModsViewModel.Instance.Mods != null)
            {
                var modsWindow = new OnlineModsWindow() { Owner = Window };
                var modsViewModel = (OnlineModsViewModel)modsWindow.ViewModel;
                if (modsViewModel.SelectedMod != null) modsViewModel.UpdateSelectedReleases();
                modsWindow.ShowDialog();
            }
            else
            {
                List<ModInfo> modInfos;
                try
                {
                    modInfos = await ModHelper.FetchMods(Window);
                }
                catch (WebException)
                {
                    MessageBox.Show(Window,
                        App.Instance.GetLocalizedMessage("InternetConnection", MessageType.Error),
                        App.Instance.GetLocalizedMessageTitle("InternetConnection", MessageType.Error),
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    throw;
                }

                if (modInfos != null)
                {
                    var modsWindow = new OnlineModsWindow() { Owner = Window };
                    var modsViewModel = (OnlineModsViewModel)modsWindow.ViewModel;
                    modsViewModel.Mods = modInfos;

                    modsWindow.ShowDialog();
                }
            }
        }

        private async Task AddModFromFile(FileInfo archiveFile, bool move, Window messageOwner)
        {
            Version factorioVersion;
            string name;
            Version version;
            if (Mod.ArchiveFileValid(archiveFile, out factorioVersion, out name, out version))
            {
                if (!Mods.ContainsByFactorioVersion(name, factorioVersion))
                {
                    await Task.Run(() =>
                    {
                        var versionDirectory = App.Instance.Settings.GetModDirectory(factorioVersion);
                        if (!versionDirectory.Exists) versionDirectory.Create();

                        var modFilePath = Path.Combine(versionDirectory.FullName, archiveFile.Name);
                        if (move)
                            archiveFile.MoveTo(modFilePath);
                        else
                            archiveFile.CopyTo(modFilePath);
                    });

                    var mod = new ZippedMod(name, version, factorioVersion, archiveFile, Mods, Modpacks);
                    Mods.Add(mod);
                }
                else
                {
                    switch (App.Instance.Settings.ManagerMode)
                    {
                        case ManagerMode.PerFactorioVersion:
                            MessageBox.Show(messageOwner,
                                string.Format(App.Instance.GetLocalizedMessage("ModExistsPerVersion", MessageType.Information), name, factorioVersion),
                                App.Instance.GetLocalizedMessageTitle("ModExistsPerVersion", MessageType.Information),
                                MessageBoxButton.OK, MessageBoxImage.Information);
                            break;
                        case ManagerMode.Global:
                            MessageBox.Show(messageOwner,
                                string.Format(App.Instance.GetLocalizedMessage("ModExists", MessageType.Information), name),
                                App.Instance.GetLocalizedMessageTitle("ModExists", MessageType.Information),
                                MessageBoxButton.OK, MessageBoxImage.Information);
                            break;
                    }
                }
            }
            else
            {
                MessageBox.Show(messageOwner,
                    string.Format(App.Instance.GetLocalizedMessage("InvalidModArchive", MessageType.Error), archiveFile.Name),
                    App.Instance.GetLocalizedMessageTitle("InvalidModArchive", MessageType.Error),
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task AddModsFromFilesInner(string[] fileNames, bool move, IProgress<Tuple<double, string>> progress, CancellationToken cancellationToken, Window messageOwner)
        {
            int fileCount = fileNames.Length;
            int counter = 0;
            foreach (string fileName in fileNames)
            {
                if (cancellationToken.IsCancellationRequested)
                    return;

                progress.Report(new Tuple<double, string>((double)counter / fileCount, Path.GetFileName(fileName)));

                var archiveFile = new FileInfo(fileName);
                await AddModFromFile(archiveFile, move, messageOwner);

                counter++;
            }

            progress.Report(new Tuple<double, string>(1, string.Empty));
        }

        private async Task AddModsFromFiles()
        {
            var dialog = new VistaOpenFileDialog();
            dialog.Multiselect = true;
            dialog.Filter = App.Instance.GetLocalizedResourceString("ZipDescription") + @" (*.zip)|*.zip";
            bool? result = dialog.ShowDialog(Window);
            if (result.HasValue && result.Value)
            {
                var copyOrMoveWindow = new CopyOrMoveMessageWindow() { Owner = Window };
                ((CopyOrMoveViewModel)copyOrMoveWindow.ViewModel).CopyOrMoveType = CopyOrMoveType.Mods;
                result = copyOrMoveWindow.ShowDialog();
                if (result.HasValue && result.Value)
                {
                    var progressWindow = new ProgressWindow() { Owner = Window };
                    var progressViewModel = (ProgressViewModel)progressWindow.ViewModel;
                    progressViewModel.ActionName = App.Instance.GetLocalizedResourceString("ProcessingModsAction");

                    var cancellationSource = new CancellationTokenSource();
                    progressViewModel.CanCancel = true;
                    progressViewModel.CancelRequested += (sender, e) => cancellationSource.Cancel();

                    var progress = new Progress<Tuple<double, string>>(info =>
                    {
                        progressViewModel.Progress = info.Item1;
                        progressViewModel.ProgressDescription = info.Item2;
                    });

                    Task processModsTask = AddModsFromFilesInner(dialog.FileNames, copyOrMoveWindow.Move, progress, cancellationSource.Token, progressWindow);

                    Task closeWindowTask = processModsTask.ContinueWith(t => progressWindow.Dispatcher.Invoke(progressWindow.Close));
                    progressWindow.ShowDialog();

                    await processModsTask;
                    await closeWindowTask;
                }
            }
        }

        private async Task AddModFromFolder()
        {
            var dialog = new VistaFolderBrowserDialog();
            bool? result = dialog.ShowDialog(Window);
            if (result.HasValue && result.Value)
            {
                var copyOrMoveWindow = new CopyOrMoveMessageWindow() { Owner = Window };
                ((CopyOrMoveViewModel)copyOrMoveWindow.ViewModel).CopyOrMoveType = CopyOrMoveType.Mod;
                result = copyOrMoveWindow.ShowDialog();
                if (result.HasValue && result.Value)
                {
                    var directory = new DirectoryInfo(dialog.SelectedPath);

                    Task moveDirectoryTask;

                    Version factorioVersion;
                    string name;
                    Version version;
                    if (Mod.DirectoryValid(directory, out factorioVersion, out name, out version))
                    {
                        if (!Mods.ContainsByFactorioVersion(name, factorioVersion))
                        {
                            var versionDirectory = App.Instance.Settings.GetModDirectory(factorioVersion);
                            if (!versionDirectory.Exists) versionDirectory.Create();

                            var modDirectoryPath = Path.Combine(versionDirectory.FullName, directory.Name);
                            moveDirectoryTask = copyOrMoveWindow.Move ? directory.MoveToAsync(modDirectoryPath) : directory.CopyToAsync(modDirectoryPath);
                        }
                        else
                        {
                            switch (App.Instance.Settings.ManagerMode)
                            {
                                case ManagerMode.PerFactorioVersion:
                                    MessageBox.Show(Window,
                                        string.Format(App.Instance.GetLocalizedMessage("ModExistsPerVersion", MessageType.Information), name, factorioVersion),
                                        App.Instance.GetLocalizedMessageTitle("ModExistsPerVersion", MessageType.Information),
                                        MessageBoxButton.OK, MessageBoxImage.Information);
                                    break;
                                case ManagerMode.Global:
                                    MessageBox.Show(Window,
                                        string.Format(App.Instance.GetLocalizedMessage("ModExists", MessageType.Information), name),
                                        App.Instance.GetLocalizedMessageTitle("ModExists", MessageType.Information),
                                        MessageBoxButton.OK, MessageBoxImage.Information);
                                    break;
                            }
                            return;
                        }
                    }
                    else
                    {
                        MessageBox.Show(Window,
                            App.Instance.GetLocalizedMessage("InvalidModFolder", MessageType.Error),
                            App.Instance.GetLocalizedMessageTitle("InvalidModFolder", MessageType.Error),
                            MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }

                    var progressWindow = new ProgressWindow() { Owner = Window };
                    var progressViewModel = (ProgressViewModel)progressWindow.ViewModel;
                    progressViewModel.ActionName = App.Instance.GetLocalizedResourceString("ProcessingModAction");
                    progressViewModel.ProgressDescription = directory.Name;
                    progressViewModel.IsIndeterminate = true;

                    moveDirectoryTask = moveDirectoryTask.ContinueWith(t => progressWindow.Dispatcher.Invoke(progressWindow.Close));
                    progressWindow.ShowDialog();
                    await moveDirectoryTask;

                    Mods.Add(new ExtractedMod(name, version, factorioVersion, directory, Mods, Modpacks));
                }
            }
        }

        #endregion

        private bool ContainsModpack(string name)
        {
            return Modpacks.Any(item => item.Name == name);
        }

        private void CreateNewModpack()
        {
            string name = App.Instance.GetLocalizedResourceString("NewModpackName");
            string newName = name;
            int counter = 0;
            while (ContainsModpack(newName))
            {
                counter++;
                newName = $"{name} {counter}";
            }

            Modpack modpack = new Modpack(newName, Modpacks);
            modpack.ParentView = ModpacksView;
            Modpacks.Add(modpack);

            modpack.Editing = true;
            Window.ModpacksListBox.ScrollIntoView(modpack);
        }

        private void CreateLink()
        {
            var propertiesWindow = new LinkPropertiesWindow() { Owner = Window };
            var propertiesViewModel = (LinkPropertiesViewModel)propertiesWindow.ViewModel;
            bool? result = propertiesWindow.ShowDialog();
            if (result.HasValue && result.Value)
            {
                var dialog = new VistaSaveFileDialog();
                dialog.Filter = App.Instance.GetLocalizedResourceString("LnkDescription") + @" (*.lnk)|*.lnk";
                dialog.AddExtension = true;
                dialog.DefaultExt = ".lnk";
                result = dialog.ShowDialog(Window);
                if (result.HasValue && result.Value)
                {
                    string applicationPath = Path.GetFullPath(Assembly.GetExecutingAssembly().Location);
                    string iconPath = Path.Combine(App.Instance.ApplicationDirectoryPath, "Factorio_Icon.ico");
                    string versionString = propertiesViewModel.SelectedVersion.VersionString;
                    string modpackName = propertiesViewModel.SelectedModpack?.Name;

                    string arguments = $"--factorio-version=\"{versionString}\"";
                    if (!string.IsNullOrEmpty(modpackName)) arguments += $" --modpack=\"{modpackName}\"";
                    ShellHelper.CreateShortcut(dialog.FileName, applicationPath, arguments, iconPath);
                }
            }
        }

        private void ExportModpacks()
        {
            var exportWindow = new ModpackExportWindow() { Owner = Window };
            var exportViewModel = (ModpackExportViewModel)exportWindow.ViewModel;
            bool? result = exportWindow.ShowDialog();
            if (result.HasValue && result.Value)
            {
                var dialog = new VistaSaveFileDialog();
                dialog.Filter = App.Instance.GetLocalizedResourceString("FmpDescription") + @" (*.fmp)|*.fmp";
                dialog.AddExtension = true;
                dialog.DefaultExt = ".fmp";
                result = dialog.ShowDialog(Window);
                if (result.HasValue && result.Value)
                {
                    ExportTemplate template = ModpackExport.CreateTemplate(
                        exportWindow.ModpackListBox.SelectedItems.Cast<Modpack>(),
                        exportViewModel.IncludeVersionInfo);
                    ModpackExport.ExportTemplate(template, dialog.FileName);
                }
            }
        }

        #region ModpackImport

        private ModRelease GetNewestRelease(ExtendedModInfo info)
        {
            return info.Releases.MaxBy(release => release.Version, new VersionComparer());
        }

        private async Task<Tuple<List<ModRelease>, List<Tuple<Mod, ModExportTemplate>>>> GetModsToDownload(ExportTemplate template, IProgress<Tuple<double, string>> progress, CancellationToken cancellationToken)
        {
            var toDownload = new List<ModRelease>();
            var conflicting = new List<Tuple<Mod, ModExportTemplate>>();

            int modCount = template.Mods.Length;
            int counter = 0;
            foreach (var modTemplate in template.Mods)
            {
                if (cancellationToken.IsCancellationRequested) return null;

                progress.Report(new Tuple<double, string>((double)counter / modCount, modTemplate.Name));
                counter++;

                ExtendedModInfo modInfo = null;
                try
                {
                    modInfo = await ModWebsite.GetExtendedInfoAsync(modTemplate.Name);
                }
                catch (WebException ex)
                {
                    if (ex.Status != WebExceptionStatus.ProtocolError) throw;
                }

                if (modInfo != null)
                {
                    if (template.IncludesVersionInfo)
                    {
                        if (!Mods.Contains(modTemplate.Name, modTemplate.Version))
                        {
                            Mod[] mods = Mods.Find(modTemplate.Name);

                            ModRelease release = modInfo.Releases.FirstOrDefault(r => r.Version == modTemplate.Version);

                            if (release != null)
                            {
                                if (mods.Length == 0)
                                {
                                    toDownload.Add(release);
                                }
                                else
                                {
                                    if ((App.Instance.Settings.ManagerMode == ManagerMode.PerFactorioVersion) &&
                                        mods.All(mod => mod.FactorioVersion != release.FactorioVersion))
                                    {
                                        toDownload.Add(release);
                                    }
                                    else
                                    {
                                        conflicting.Add(new Tuple<Mod, ModExportTemplate>(mods[0], modTemplate));
                                    }
                                }
                            }
                        }
                    }
                    else
                    {
                        Mod[] mods = Mods.Find(modTemplate.Name);

                        if (mods.Length == 0)
                        {
                            ModRelease newestRelease = GetNewestRelease(modInfo);
                            toDownload.Add(newestRelease);
                        }
                        else
                        {
                            ModRelease newestRelease = GetNewestRelease(modInfo);

                            if (!Mods.Contains(modTemplate.Name, newestRelease.Version))
                            {
                                if ((App.Instance.Settings.ManagerMode == ManagerMode.PerFactorioVersion) &&
                                    mods.All(mod => mod.FactorioVersion != newestRelease.FactorioVersion))
                                {
                                    toDownload.Add(newestRelease);
                                }
                                else
                                {
                                    conflicting.Add(new Tuple<Mod, ModExportTemplate>(mods[0], modTemplate));
                                }
                            }
                        }
                    }
                }
            }

            progress.Report(new Tuple<double, string>(1, string.Empty));

            return new Tuple<List<ModRelease>, List<Tuple<Mod, ModExportTemplate>>>(toDownload, conflicting);
        }

        private async Task DownloadModAsyncInner(ModRelease modRelease, string token, IProgress<double> progress, CancellationToken cancellationToken)
        {
            Mod mod = await ModWebsite.DownloadReleaseAsync(modRelease, GlobalCredentials.Instance.Username, token, progress, cancellationToken, Mods, Modpacks);
            if (!cancellationToken.IsCancellationRequested && (mod != null)) Mods.Add(mod);
        }

        private async Task DownloadModsAsyncInner(List<ModRelease> modReleases, string token, IProgress<Tuple<double, string>> progress, CancellationToken cancellationToken)
        {
            int modCount = modReleases.Count;
            double baseProgressValue = 0;
            foreach (var release in modReleases)
            {
                if (cancellationToken.IsCancellationRequested) return;

                double modProgressValue = 0;
                var modProgress = new Progress<double>(value =>
                {
                    modProgressValue = value / modCount;
                    progress.Report(new Tuple<double, string>(baseProgressValue + modProgressValue, release.FileName));
                });

                try
                {
                    await DownloadModAsyncInner(release, token, modProgress, cancellationToken);
                }
                catch (HttpRequestException)
                {
                    MessageBox.Show(Window,
                        App.Instance.GetLocalizedMessage("InternetConnection", MessageType.Error),
                        App.Instance.GetLocalizedMessageTitle("InternetConnection", MessageType.Error),
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                baseProgressValue += modProgressValue;
            }
        }

        private async Task DownloadModsAsync(List<ModRelease> modReleases)
        {
            string token;
            if (GlobalCredentials.Instance.LogIn(Window, out token))
            {
                var progressWindow = new ProgressWindow() { Owner = Window };
                var progressViewModel = (ProgressViewModel)progressWindow.ViewModel;
                progressViewModel.ActionName = App.Instance.GetLocalizedResourceString("DownloadingAction");

                var progress = new Progress<Tuple<double, string>>(info =>
                {
                    progressViewModel.Progress = info.Item1;
                    progressViewModel.ProgressDescription = string.Format(App.Instance.GetLocalizedResourceString("DownloadingDescription"), info.Item2);
                });

                var cancellationSource = new CancellationTokenSource();
                progressViewModel.CanCancel = true;
                progressViewModel.CancelRequested += (sender, e) => cancellationSource.Cancel();

                Task updateTask = DownloadModsAsyncInner(modReleases, token, progress, cancellationSource.Token);
                Task closeWindowTask = updateTask.ContinueWith(t => progressWindow.Dispatcher.Invoke(progressWindow.Close));
                progressWindow.ShowDialog();

                await updateTask;
                await closeWindowTask;
            }
        }

        private async Task ImportModpackFile(FileInfo modpackFile)
        {
            ExportTemplate template = ModpackExport.ImportTemplate(modpackFile);

            var progressWindow = new ProgressWindow() { Owner = Window };
            var progressViewModel = (ProgressViewModel)progressWindow.ViewModel;
            progressViewModel.ActionName = App.Instance.GetLocalizedResourceString("DownloadingAction");

            var progress = new Progress<Tuple<double, string>>(info =>
            {
                progressViewModel.Progress = info.Item1;
                progressViewModel.ProgressDescription = info.Item2;
            });

            var cancellationSource = new CancellationTokenSource();
            progressViewModel.CanCancel = true;
            progressViewModel.CancelRequested += (sender, e) => cancellationSource.Cancel();

            Tuple<List<ModRelease>, List<Tuple<Mod, ModExportTemplate>>> toDownloadResult;
            try
            {
                Task closeWindowTask = null;
                try
                {
                    var getModsTask = GetModsToDownload(template, progress, cancellationSource.Token);

                    closeWindowTask = getModsTask.ContinueWith(t => progressWindow.Dispatcher.Invoke(progressWindow.Close));
                    progressWindow.ShowDialog();

                    toDownloadResult = await getModsTask;
                }
                finally
                {
                    if (closeWindowTask != null) await closeWindowTask;
                }
            }
            catch (WebException)
            {
                MessageBox.Show(Window,
                    App.Instance.GetLocalizedMessage("InternetConnection", MessageType.Error),
                    App.Instance.GetLocalizedMessageTitle("InternetConnection", MessageType.Error),
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            List<ModRelease> toDownload = toDownloadResult.Item1;
            List<Tuple<Mod, ModExportTemplate>> conflicting = toDownloadResult.Item2;

            if (conflicting.Count > 0)
            {
                MessageBox.Show(Window,
                    App.Instance.GetLocalizedMessage("HasConflicts", MessageType.Warning) + "\n"
                    + string.Join("\n", conflicting.Select(conflict => $"{conflict.Item1.Name} ({conflict.Item1.Version}) <-> {conflict.Item2.Name}"
                    + (template.IncludesVersionInfo ? $" ({conflict.Item2.Version})" : " (latest)"))),
                    App.Instance.GetLocalizedMessageTitle("HasConflicts", MessageType.Warning),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            try
            {
                if (toDownload.Count > 0)
                    await DownloadModsAsync(toDownload);
            }
            catch (HttpRequestException)
            {
                MessageBox.Show(Window,
                    App.Instance.GetLocalizedMessage("InternetConnection", MessageType.Error),
                    App.Instance.GetLocalizedMessageTitle("InternetConnection", MessageType.Error),
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            foreach (var modpackTemplate in template.Modpacks)
            {
                var existingModpack = Modpacks.FirstOrDefault(item => item.Name == modpackTemplate.Name);

                if (existingModpack == null)
                {
                    Modpack modpack = new Modpack(modpackTemplate.Name, Modpacks);
                    modpack.ParentView = ModpacksView;

                    foreach (var modTemplate in modpackTemplate.Mods)
                    {
                        if (template.IncludesVersionInfo)
                        {
                            Mod mod = Mods.Find(modTemplate.Name, modTemplate.Version);
                            if (mod != null) modpack.Mods.Add(new ModReference(mod, modpack));
                        }
                        else
                        {
                            Mod mod = Mods.Find(modTemplate.Name).MaxBy(item => item.Version, new VersionComparer());
                            if (mod != null) modpack.Mods.Add(new ModReference(mod, modpack));
                        }
                    }

                    Modpacks.Add(modpack);
                }
                else
                {
                    foreach (var modTemplate in modpackTemplate.Mods)
                    {
                        if (template.IncludesVersionInfo)
                        {
                            Mod mod = Mods.Find(modTemplate.Name, modTemplate.Version);
                            if ((mod != null) && !existingModpack.Contains(mod)) existingModpack.Mods.Add(new ModReference(mod, existingModpack));
                        }
                        else
                        {
                            Mod mod = Mods.Find(modTemplate.Name).MaxBy(item => item.Version, new VersionComparer());
                            if ((mod != null) && !existingModpack.Contains(mod)) existingModpack.Mods.Add(new ModReference(mod, existingModpack));
                        }
                    }
                }
            }
            foreach (var modpackTemplate in template.Modpacks)
            {
                var existingModpack = Modpacks.FirstOrDefault(item => item.Name == modpackTemplate.Name);

                if (existingModpack != null)
                {
                    foreach (var innerTemplate in modpackTemplate.Modpacks)
                    {
                        Modpack modpack = Modpacks.FirstOrDefault(item => item.Name == innerTemplate);
                        if ((modpack != null) && !existingModpack.Contains(modpack)) existingModpack.Mods.Add(new ModpackReference(modpack, existingModpack));
                    }
                }
            }
        }

        private async Task ImportModpacksInner(IEnumerable<FileInfo> modpackFiles)
        {
            foreach (FileInfo file in modpackFiles)
                await ImportModpackFile(file);
        }

        private async Task ImportModpacks()
        {
            var dialog = new VistaOpenFileDialog();
            dialog.Filter = App.Instance.GetLocalizedResourceString("FmpDescription") + @" (*.fmp)|*.fmp";
            dialog.Multiselect = true;
            bool? result = dialog.ShowDialog(Window);
            if (result.HasValue && result.Value)
            {
                var fileList = new List<FileInfo>();
                foreach (var fileName in dialog.FileNames)
                {
                    var file = new FileInfo(fileName);
                    fileList.Add(file);
                }
                
                if (fileList.Count > 0)
                    await ImportModpacksInner(fileList);
            }
        }

        #endregion

        private void StartGame()
        {
            Process.Start(SelectedFactorioVersion.ExecutablePath);
        }

        #region ModUpdate

        private ModRelease GetNewestRelease(ExtendedModInfo info, Mod current)
        {
            if (App.Instance.Settings.ManagerMode == ManagerMode.PerFactorioVersion)
            {
                return info.Releases.Where(release => release.FactorioVersion == current.FactorioVersion)
                    .MaxBy(release => release.Version, new VersionComparer());
            }
            else
            {
                return info.Releases.MaxBy(release => release.Version, new VersionComparer());
            }
        }

        private async Task<List<ModUpdateInfo>> GetModUpdatesAsync(IProgress<Tuple<double, string>> progress, CancellationToken cancellationToken)
        {
            var modUpdates = new List<ModUpdateInfo>();

            int modCount = Mods.Count;
            int modIndex = 0;
            foreach (var mod in Mods)
            {
                if (cancellationToken.IsCancellationRequested) return null;

                progress.Report(new Tuple<double, string>((double)modIndex / modCount, mod.Title));

                ExtendedModInfo extendedInfo = null;
                try
                {
                    extendedInfo = await ModWebsite.GetExtendedInfoAsync(mod);
                }
                catch (WebException ex)
                {
                    if (ex.Status != WebExceptionStatus.ProtocolError) throw;
                }

                if (extendedInfo != null)
                {
                    ModRelease newestRelease = GetNewestRelease(extendedInfo, mod);
                    if ((newestRelease != null) && (newestRelease.Version > mod.Version))
                        modUpdates.Add(new ModUpdateInfo(mod.Title, mod.Name, mod.Version, newestRelease.Version, mod, newestRelease));
                }

                modIndex++;
            }

            return modUpdates;
        }

        private async Task UpdateModAsyncInner(ModUpdateInfo modUpdate, string token, IProgress<double> progress, CancellationToken cancellationToken)
        {
            FileInfo modFile = await ModWebsite.UpdateReleaseAsync(modUpdate.NewestRelease, GlobalCredentials.Instance.Username, token, progress, cancellationToken);
            Mod oldMod = modUpdate.Mod;
            Mod newMod;

            if (App.Instance.Settings.AlwaysUpdateZipped || (oldMod is ZippedMod))
            {
                newMod = new ZippedMod(oldMod.Name, modUpdate.NewestRelease.Version, modUpdate.NewestRelease.FactorioVersion, modFile, Mods, Modpacks);
            }
            else
            {
                DirectoryInfo modDirectory = await Task.Run(() =>
                {
                    DirectoryInfo modsDirectory = App.Instance.Settings.GetModDirectory(modUpdate.NewestRelease.FactorioVersion);
                    ZipFile.ExtractToDirectory(modFile.FullName, modsDirectory.FullName);
                    modFile.Delete();

                    return new DirectoryInfo(Path.Combine(modsDirectory.FullName, modFile.NameWithoutExtension()));
                });

                newMod = new ExtractedMod(oldMod.Name, modUpdate.NewestRelease.Version, modUpdate.NewestRelease.FactorioVersion, modDirectory, Mods, Modpacks);
            }

            Mods.Add(newMod);
            Modpacks.ExchangeMods(oldMod, newMod);
            oldMod.Update(newMod);

            ModpackTemplateList.Instance.Update(Modpacks);
            ModpackTemplateList.Instance.Save();
        }

        private async Task UpdateModsAsyncInner(List<ModUpdateInfo> modUpdates, string token, IProgress<Tuple<double, string>> progress, CancellationToken cancellationToken)
        {
            int modCount = modUpdates.Count(item => item.IsSelected);
            double baseProgressValue = 0;
            foreach (var modUpdate in modUpdates)
            {
                if (cancellationToken.IsCancellationRequested) return;

                if (modUpdate.IsSelected)
                {
                    double modProgressValue = 0;
                    var modProgress = new Progress<double>(value =>
                    {
                        modProgressValue = value / modCount;
                        progress.Report(new Tuple<double, string>(baseProgressValue + modProgressValue, modUpdate.Title));
                    });

                    try
                    {
                        await UpdateModAsyncInner(modUpdate, token, modProgress, cancellationToken);
                    }
                    catch (HttpRequestException)
                    {
                        MessageBox.Show(Window,
                            App.Instance.GetLocalizedMessage("InternetConnection", MessageType.Error),
                            App.Instance.GetLocalizedMessageTitle("InternetConnection", MessageType.Error),
                            MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }

                    baseProgressValue += modProgressValue;
                }
            }
        }

        private async Task UpdateMods()
        {
            var progressWindow = new ProgressWindow() { Owner = Window };
            var progressViewModel = (ProgressViewModel)progressWindow.ViewModel;
            progressViewModel.ActionName = App.Instance.GetLocalizedResourceString("SearchingForUpdatesAction");

            var progress = new Progress<Tuple<double, string>>(info =>
            {
                progressViewModel.Progress = info.Item1;
                progressViewModel.ProgressDescription = info.Item2;
            });

            var cancellationSource = new CancellationTokenSource();
            progressViewModel.CanCancel = true;
            progressViewModel.CancelRequested += (sender, e) => cancellationSource.Cancel();

            List<ModUpdateInfo> modUpdates;
            try
            {
                Task closeWindowTask = null;
                try
                {
                    Task<List<ModUpdateInfo>> searchForUpdatesTask = GetModUpdatesAsync(progress, cancellationSource.Token);

                    closeWindowTask = searchForUpdatesTask.ContinueWith(t => progressWindow.Dispatcher.Invoke(progressWindow.Close));
                    progressWindow.ShowDialog();

                    modUpdates = await searchForUpdatesTask;
                }
                finally
                {
                    if (closeWindowTask != null) await closeWindowTask;
                }
            }
            catch (WebException)
            {
                MessageBox.Show(Window,
                    App.Instance.GetLocalizedMessage("InternetConnection", MessageType.Error),
                    App.Instance.GetLocalizedMessageTitle("InternetConnection", MessageType.Error),
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (!cancellationSource.IsCancellationRequested)
            {
                if (modUpdates.Count > 0)
                {
                    var updateWindow = new ModUpdateWindow() { Owner = Window };
                    var updateViewModel = (ModUpdateViewModel)updateWindow.ViewModel;
                    updateViewModel.ModsToUpdate = modUpdates;
                    bool? result = updateWindow.ShowDialog();

                    if (result.HasValue && result.Value)
                    {
                        string token;
                        if (GlobalCredentials.Instance.LogIn(Window, out token))
                        {
                            progressWindow = new ProgressWindow() { Owner = Window };
                            progressViewModel = (ProgressViewModel)progressWindow.ViewModel;
                            progressViewModel.ActionName = App.Instance.GetLocalizedResourceString("UpdatingModsAction");

                            progress = new Progress<Tuple<double, string>>(info =>
                            {
                                progressViewModel.Progress = info.Item1;
                                progressViewModel.ProgressDescription = info.Item2;
                            });

                            cancellationSource = new CancellationTokenSource();
                            progressViewModel.CanCancel = true;
                            progressViewModel.CancelRequested += (sender, e) => cancellationSource.Cancel();

                            Task updateTask = UpdateModsAsyncInner(modUpdates, token, progress, cancellationSource.Token);

                            Task closeWindowTask = updateTask.ContinueWith(t => progressWindow.Dispatcher.Invoke(progressWindow.Close));
                            progressWindow.ShowDialog();

                            await updateTask;
                            await closeWindowTask;
                        }
                    }
                }
                else
                {
                    MessageBox.Show(Window,
                        App.Instance.GetLocalizedMessage("NoModUpdates", MessageType.Information),
                        App.Instance.GetLocalizedMessageTitle("NoModUpdates", MessageType.Information),
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
        }

        #endregion

        private void OpenVersionManager()
        {
            var versionManagementWindow = new VersionManagementWindow() { Owner = Window };
            versionManagementWindow.ShowDialog();
        }

        #region Settings

        private async Task MoveFactorioDirectory(DirectoryInfo oldFactorioDirectory, DirectoryInfo newFactorioDirectory)
        {
            if (oldFactorioDirectory.Exists)
            {
                if (!newFactorioDirectory.Exists) newFactorioDirectory.Create();

                DirectoryInfo factorioDirectory = App.Instance.Settings.GetFactorioDirectory();
                foreach (var version in FactorioVersions)
                {
                    version.DeleteLinks();

                    if (version.IsFileSystemEditable)
                    {
                        var versionDirectory = new DirectoryInfo(Path.Combine(factorioDirectory.FullName, version.VersionString));
                        await version.Directory.MoveToAsync(versionDirectory.FullName);
                        version.UpdateDirectory(versionDirectory);
                    }
                }

                oldFactorioDirectory.DeleteIfEmpty();
            }
        }

        private async Task MoveModDirectory(DirectoryInfo oldModDirectory, DirectoryInfo newModDirectory)
        {
            if (oldModDirectory.Exists)
            {
                if (!newModDirectory.Exists) newModDirectory.Create();

                foreach (var mod in Mods)
                {
                    var dir = new DirectoryInfo(Path.Combine(newModDirectory.FullName, mod.FactorioVersion.ToString(2)));
                    if (!dir.Exists) dir.Create();
                    await mod.MoveTo(dir);
                }
                foreach (var version in FactorioVersions)
                {
                    if (!version.IsSpecialVersion)
                    {
                        var dir = new DirectoryInfo(Path.Combine(oldModDirectory.FullName, version.Version.ToString(2)));
                        if (dir.Exists)
                        {
                            var modListFile = new FileInfo(Path.Combine(dir.FullName, "mod-list.json"));
                            var newDir = new DirectoryInfo(Path.Combine(newModDirectory.FullName, version.Version.ToString(2)));
                            if (!newDir.Exists) newDir.Create();
                            if (modListFile.Exists) await modListFile.MoveToAsync(Path.Combine(newDir.FullName, "mod-list.json"));

                            dir.DeleteIfEmpty();
                        }
                    }
                }

                oldModDirectory.DeleteIfEmpty();
            }
        }

        private async Task MoveSavegameDirectory(DirectoryInfo oldSavegameDirectory, DirectoryInfo newSavegameDirectory)
        {
            if (oldSavegameDirectory.Exists)
                await oldSavegameDirectory.MoveToAsync(newSavegameDirectory.FullName);
        }

        private async Task MoveScenarioDirectory(DirectoryInfo oldScenarioDirectory, DirectoryInfo newScenarioDirectory)
        {
            if (oldScenarioDirectory.Exists && !newScenarioDirectory.DirectoryEquals(oldScenarioDirectory))
                await oldScenarioDirectory.MoveToAsync(newScenarioDirectory.FullName);
        }

        private async Task RecreateLinks()
        {
            await Task.Run(() =>
            {
                foreach (var version in FactorioVersions)
                    version.CreateLinks();
            });
        }

        private async Task MoveDirectoriesInternal(
            DirectoryInfo oldFactorioDirectory, DirectoryInfo oldModDirectory, DirectoryInfo oldSavegameDirectory, DirectoryInfo oldScenarioDirectory,
            DirectoryInfo newFactorioDirectory, DirectoryInfo newModDirectory, DirectoryInfo newSavegameDirectory, DirectoryInfo newScenarioDirectory,
            bool moveFactorioDirectory, bool moveModDirectory, bool moveSavegameDirectory, bool moveScenarioDirectory)
        {
            if (moveFactorioDirectory)
                await MoveFactorioDirectory(oldFactorioDirectory, newFactorioDirectory);

            if (moveModDirectory)
                await MoveModDirectory(oldModDirectory, newModDirectory);

            if (moveSavegameDirectory)
                await MoveSavegameDirectory(oldSavegameDirectory, newSavegameDirectory);

            if (moveScenarioDirectory)
                await MoveScenarioDirectory(oldScenarioDirectory, newScenarioDirectory);

            await RecreateLinks();
        }

        private async Task MoveDirectories(
            DirectoryInfo oldFactorioDirectory, DirectoryInfo oldModDirectory, DirectoryInfo oldSavegameDirectory, DirectoryInfo oldScenarioDirectory,
            DirectoryInfo newFactorioDirectory, DirectoryInfo newModDirectory, DirectoryInfo newSavegameDirectory, DirectoryInfo newScenarioDirectory,
            bool moveFactorioDirectory, bool moveModDirectory, bool moveSavegameDirectory, bool moveScenarioDirectory)
        {
            var progressWindow = new ProgressWindow() { Owner = Window };
            var progressViewModel = (ProgressViewModel)progressWindow.ViewModel;
            progressViewModel.ActionName = App.Instance.GetLocalizedResourceString("MovingDirectoriesAction");
            progressViewModel.ProgressDescription = App.Instance.GetLocalizedResourceString("MovingFilesDescription");
            progressViewModel.IsIndeterminate = true;

            Task moveDirectoriesTask = MoveDirectoriesInternal(
                oldFactorioDirectory, oldModDirectory, oldSavegameDirectory, oldScenarioDirectory,
                newFactorioDirectory, newModDirectory, newSavegameDirectory, newScenarioDirectory,
                moveFactorioDirectory, moveModDirectory, moveSavegameDirectory, moveScenarioDirectory);

            Task closeWindowTask = moveDirectoriesTask.ContinueWith(t => progressWindow.Dispatcher.Invoke(progressWindow.Close));
            progressWindow.ShowDialog();

            await moveDirectoriesTask;
            await closeWindowTask;
        }

        private async Task ApplySettings(Settings settings, SettingsViewModel settingsViewModel, SettingsWindow settingsWindow)
        {
            DirectoryInfo oldFactorioDirectory = settings.GetFactorioDirectory();
            DirectoryInfo oldModDirectory = settings.GetModDirectory();
            DirectoryInfo oldSavegameDirectory = settings.GetSavegameDirectory();
            DirectoryInfo oldScenarioDirectory = settings.GetScenarioDirectory();

            // Manager mode
            bool managerModeChanged = (settingsViewModel.ManagerMode != settings.ManagerMode);
            settings.ManagerMode = settingsViewModel.ManagerMode;

            // Update search
            settings.UpdateSearchOnStartup = settingsViewModel.UpdateSearchOnStartup;
            settings.IncludePreReleasesForUpdate = settingsViewModel.IncludePreReleasesForUpdate;

            // Mod update
            settings.AlwaysUpdateZipped = settingsViewModel.AlwaysUpdateZipped;
            settings.KeepOldModVersions = settingsViewModel.KeepOldModVersions;

            // Factorio location
            settings.FactorioDirectoryOption = settingsViewModel.FactorioDirectoryOption;
            settings.FactorioDirectory = (settings.FactorioDirectoryOption == DirectoryOption.Custom)
                ? settingsViewModel.FactorioDirectory : string.Empty;

            // Mod location
            settings.ModDirectoryOption = settingsViewModel.ModDirectoryOption;
            settings.ModDirectory = (settings.ModDirectoryOption == DirectoryOption.Custom)
                ? settingsViewModel.ModDirectory : string.Empty;

            // Savegame location
            settings.SavegameDirectoryOption = settingsViewModel.SavegameDirectoryOption;
            settings.SavegameDirectory = (settings.SavegameDirectoryOption == DirectoryOption.Custom)
                ? settingsViewModel.SavegameDirectory : string.Empty;

            // Scenario location
            settings.ScenarioDirectoryOption = settingsViewModel.ScenarioDirectoryOption;
            settings.ScenarioDirectory = (settings.ScenarioDirectoryOption == DirectoryOption.Custom)
                ? settingsViewModel.ScenarioDirectory : string.Empty;

            // Login credentials
            settings.SaveCredentials = settingsWindow.SaveCredentialsBox.IsChecked ?? false;
            if (settings.SaveCredentials)
            {
                if (settingsWindow.PasswordBox.SecurePassword.Length > 0)
                {
                    GlobalCredentials.Instance.Username = settingsWindow.UsernameBox.Text;
                    GlobalCredentials.Instance.Password = settingsWindow.PasswordBox.SecurePassword;
                    GlobalCredentials.Instance.Save();
                }
            }
            else
            {
                GlobalCredentials.Instance.DeleteSave();
            }

            settings.Save();

            DirectoryInfo newFactorioDirectory = settings.GetFactorioDirectory();
            DirectoryInfo newModDirectory = settings.GetModDirectory();
            DirectoryInfo newSavegameDirectory = settings.GetSavegameDirectory();
            DirectoryInfo newScenarioDirectory = settings.GetScenarioDirectory();


            // Move directories
            bool moveFactorioDirectory = !newFactorioDirectory.DirectoryEquals(oldFactorioDirectory);
            bool moveModDirectory = !newModDirectory.DirectoryEquals(oldModDirectory);
            bool moveSavegameDirectory = !newSavegameDirectory.DirectoryEquals(oldSavegameDirectory);
            bool moveScenarioDirectory = !newScenarioDirectory.DirectoryEquals(oldScenarioDirectory);

            if (moveFactorioDirectory || moveModDirectory || moveSavegameDirectory || moveScenarioDirectory)
            {
                if (MessageBox.Show(Window,
                App.Instance.GetLocalizedMessage("MoveDirectories", MessageType.Question),
                App.Instance.GetLocalizedMessageTitle("MoveDirectories", MessageType.Question),
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                {
                    await MoveDirectories(
                        oldFactorioDirectory, oldModDirectory, oldSavegameDirectory, oldScenarioDirectory,
                        newFactorioDirectory, newModDirectory, newSavegameDirectory, newScenarioDirectory,
                        moveFactorioDirectory, moveModDirectory, moveSavegameDirectory, moveScenarioDirectory);
                }
            }


            // Reload everything if required
            if (managerModeChanged || moveFactorioDirectory || moveModDirectory)
            {
                Refresh();
            }
        }

        private async Task OpenSettings()
        {
            Settings settings = App.Instance.Settings;

            var settingsWindow = new SettingsWindow() { Owner = Window };
            var settingsViewModel = (SettingsViewModel)settingsWindow.ViewModel;
            settingsViewModel.Reset();
            settingsWindow.SaveCredentialsBox.IsChecked = settings.SaveCredentials;

            bool? result = settingsWindow.ShowDialog();
            if (result != null && result.Value)
            {
                await ApplySettings(settings, settingsViewModel, settingsWindow);
            }
        }

        #endregion

        private async Task Update(bool silent)
        {
            updating = true;

            try
            {
                UpdateSearchResult result = null;

                try
                {
                    result = await App.Instance.SearchForUpdateAsync(App.Instance.Settings.IncludePreReleasesForUpdate);
                }
                catch (HttpRequestException)
                {
                    if (!silent)
                    {
                        MessageBox.Show(Window,
                            App.Instance.GetLocalizedMessage("InternetConnection", MessageType.Error),
                            App.Instance.GetLocalizedMessageTitle("InternetConnection", MessageType.Error),
                            MessageBoxButton.OK, MessageBoxImage.Error);
                    }

                    return;
                }

                if (result != null)
                {
                    if (result.UpdateAvailable)
                    {
                        string currentVersionString = App.Version.ToString();
                        string newVersionString = result.Version.ToString();
                        if (MessageBox.Show(Window,
                                string.Format(App.Instance.GetLocalizedMessage("Update", MessageType.Question), currentVersionString, newVersionString),
                                App.Instance.GetLocalizedMessageTitle("Update", MessageType.Question),
                                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                        {
                            Process.Start(result.UpdateUrl);
                        }
                    }
                    else if (!silent)
                    {
                        MessageBox.Show(Window,
                            App.Instance.GetLocalizedMessage("NoUpdate", MessageType.Information),
                            App.Instance.GetLocalizedMessageTitle("NoUpdate", MessageType.Information),
                            MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }
            }
            finally
            {
                updating = false;
            }
        }

        private void OpenAboutWindow()
        {
            var aboutWindow = new AboutWindow() { Owner = Window };
            aboutWindow.ShowDialog();
        }

        #region ContextMenus

        private void SetSelectedModsActiveState(bool state)
        {
            ModManager.BeginUpdateTemplates();

            foreach (Mod mod in Mods)
            {
                if (mod.IsSelected)
                    mod.Active = state;
            }

            ModManager.EndUpdateTemplates(true);
            ModManager.SaveTemplates();
        }

        private void ActivateSelectedMods()
        {
            SetSelectedModsActiveState(true);
        }

        private void DeactivateSelectedMods()
        {
            SetSelectedModsActiveState(false);
        }

        private void DeleteSelectedMods()
        {
            var deletionList = new List<Mod>();
            foreach (Mod mod in Mods)
            {
                if (mod.IsSelected)
                    deletionList.Add(mod);
            }

            if (deletionList.Count > 0 && MessageBox.Show(Window,
                App.Instance.GetLocalizedMessage("DeleteMods", MessageType.Question),
                App.Instance.GetLocalizedMessageTitle("DeleteMods", MessageType.Question),
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                foreach (Mod mod in deletionList)
                    mod.Delete(false);
            }
        }

        private void SelectActiveMods()
        {
            foreach (Mod mod in Mods)
            {
                if (mod.Active)
                    mod.IsSelected = true;
            }
        }

        private void SelectInactiveMods()
        {
            foreach (Mod mod in Mods)
            {
                if (!mod.Active)
                    mod.IsSelected = true;
            }
        }

        private void SetSelectedModpacksActiveState(bool state)
        {
            ModManager.BeginUpdateTemplates();

            foreach (Modpack modpack in Modpacks)
            {
                if (modpack.IsSelected)
                    modpack.Active = state;
            }

            ModManager.EndUpdateTemplates(true);
            ModManager.SaveTemplates();
        }

        private void ActivateSelectedModpacks()
        {
            SetSelectedModpacksActiveState(true);
        }

        private void DeactivateSelectedModpacks()
        {
            SetSelectedModpacksActiveState(false);
        }

        private void DeleteSelectedModpacks()
        {
            var deletionList = new List<Modpack>();
            foreach (Modpack modpack in Modpacks)
            {
                if (modpack.IsSelected)
                    deletionList.Add(modpack);
            }

            if (deletionList.Count > 0 && MessageBox.Show(Window,
                App.Instance.GetLocalizedMessage("DeleteModpacks", MessageType.Question),
                App.Instance.GetLocalizedMessageTitle("DeleteModpacks", MessageType.Question),
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                foreach (Modpack modpack in deletionList)
                    modpack.Delete(false);
            }
        }

        private void SelectActiveModpacks()
        {
            foreach (Modpack modpack in Modpacks)
            {
                if (modpack.Active ?? false)
                    modpack.IsSelected = true;
            }
        }

        private void SelectInactiveModpacks()
        {
            foreach (Modpack modpack in Modpacks)
            {
                if (!(modpack.Active ?? false))
                    modpack.IsSelected = true;
            }
        }

        private void DeleteSelectedModsAndModpacks()
        {
            DeleteSelectedMods();
            DeleteSelectedModpacks();
        }

        #endregion

        private async void NewInstanceStartedHandler(object sender, InstanceStartedEventArgs e)
        {
            await Window.Dispatcher.InvokeAsync(async () => await OnNewInstanceStarted(e.CommandLine, e.GameStarted));
        }

        private async Task OnNewInstanceStarted(CommandLine commandLine, bool gameStarted)
        {
            if (gameStarted)
            {
                Refresh();
            }
            else
            {
                Window.Activate();

                var fileList = new List<FileInfo>();
                foreach (string argument in commandLine.Arguments)
                {
                    if (argument.EndsWith(".fmp") && File.Exists(argument))
                    {
                        var file = new FileInfo(argument);
                        fileList.Add(file);
                    }
                }

                if (fileList.Count > 0)
                    await ImportModpacksInner(fileList);
            }
        }

        protected override void OnPropertyChanged(PropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);

            if ((e.PropertyName == nameof(Window)) && (Window != null))
            {
                Window.Loaded += async (sender, ea) =>
                {
                    if (Program.ImportFileList.Count > 0)
                        await ImportModpacksInner(Program.ImportFileList);
                    else if (Program.UpdateCheckOnStartup && App.Instance.Settings.UpdateSearchOnStartup) // Just skip update check if import list is non-zero
                        await Update(true);
                };
            }
        }
    }
}

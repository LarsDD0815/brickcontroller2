﻿using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Maui.ApplicationModel;
using BrickController2.CreationManagement;
using BrickController2.DeviceManagement;
using BrickController2.Helpers;
using BrickController2.PlatformServices.SharedFileStorage;
using BrickController2.UI.Commands;
using BrickController2.UI.Services.Navigation;
using BrickController2.UI.Services.Dialog;
using BrickController2.UI.Services.Translation;
using BrickController2.PlatformServices.Permission;


using BrickController2.BusinessLogic;
using System.IO;

namespace BrickController2.UI.ViewModels
{
    public class CreationListPageViewModel : PageViewModelBase
    {
        private readonly ICreationManager _creationManager;
        private readonly IDeviceManager _deviceManager;
        private readonly IDialogService _dialogService;
        private readonly IPlayLogic _playLogic;

        private readonly IBluetoothPermission _bluetoothPermission;
        private readonly IReadWriteExternalStoragePermission _readWriteExternalStoragePermission;

        private CancellationTokenSource? _disappearingTokenSource;
        private bool _isLoaded;

        // Permission request fires OnDisappearing somehow (WTF???)
        private bool _isRequestingPermission = false;
        private bool _isBluetoothPermissionRequested = false;
        //private bool _isLocationPermissionRequested = false;
        private bool _isStoragePermissionRequested = false;

        public CreationListPageViewModel(
            INavigationService navigationService,
                        IPlayLogic playLogic,

            ITranslationService translationService,
            ICreationManager creationManager,
            IDeviceManager deviceManager,
            IDialogService dialogService,
            ISharedFileStorageService sharedFileStorageService,
            IBluetoothPermission bluetoothPermission,
            IReadWriteExternalStoragePermission readWriteExternalStoragePermission)
            : base(navigationService, translationService)
        {
            _creationManager = creationManager;
            _deviceManager = deviceManager;
            _dialogService = dialogService;
            _bluetoothPermission = bluetoothPermission;
            _playLogic = playLogic;

            _readWriteExternalStoragePermission = readWriteExternalStoragePermission;
            SharedFileStorageService = sharedFileStorageService;

            ImportCreationCommand = new SafeCommand(async () => await ImportCreationAsync(), () => SharedFileStorageService.IsSharedStorageAvailable);
            OpenSettingsPageCommand = new SafeCommand(async () => await navigationService.NavigateToAsync<SettingsPageViewModel>(), () => !_dialogService.IsDialogOpen);
            AddCreationCommand = new SafeCommand(async () => await AddCreationAsync());
            CreationTappedCommand = new SafeCommand<Creation>(async creation => await NavigationService.NavigateToAsync<ControllerProfilePageViewModel>(new NavigationParameters(("controllerprofile", creation.ControllerProfiles.First()))));
            // CreationTappedCommand = new SafeCommand<Creation>(async creation => await NavigationService.NavigateToAsync<CreationPageViewModel>(new NavigationParameters(("creation", creation))));
            DeleteCreationCommand = new SafeCommand<Creation>(async creation => await DeleteCreationAsync(creation));
            NavigateToDevicesCommand = new SafeCommand(async () => await NavigationService.NavigateToAsync<DeviceListPageViewModel>());
            NavigateToControllerTesterCommand = new SafeCommand(async () => await NavigationService.NavigateToAsync<ControllerTesterPageViewModel>());
            NavigateToSequencesCommand = new SafeCommand(async () => await NavigationService.NavigateToAsync<SequenceListPageViewModel>());
            NavigateToAboutCommand = new SafeCommand(async () => await NavigationService.NavigateToAsync<AboutPageViewModel>());
            PlayCommand = new SafeCommand<Creation>(async creation => await PlayAsync(creation));

        }

        public ObservableCollection<Creation> Creations => _creationManager.Creations;

        public ISharedFileStorageService SharedFileStorageService { get; }

        public ICommand OpenSettingsPageCommand { get; }
        public ICommand AddCreationCommand { get; }
        public ICommand CreationTappedCommand { get; }
        public ICommand DeleteCreationCommand { get; }
        public ICommand ImportCreationCommand { get; }
        public ICommand NavigateToDevicesCommand { get; }
        public ICommand NavigateToControllerTesterCommand { get; }
        public ICommand NavigateToSequencesCommand { get; }
        public ICommand NavigateToAboutCommand { get; }
        public ICommand PlayCommand { get; }

        public override async void OnAppearing()
        {
            if (!_isRequestingPermission)
            {
                _disappearingTokenSource?.Cancel();
                _disappearingTokenSource = new CancellationTokenSource();

                await LoadCreationsAndDevicesAsync();
                await RequestPermissionsAsync();
            }
        }

        public override void OnDisappearing()
        {
            if (!_isRequestingPermission)
            {
                _disappearingTokenSource?.Cancel();
                _disappearingTokenSource = null;
            }
        }

        private async Task RequestPermissionsAsync()
        {
            try
            {
                var bluetoothPermissionStatus = await _bluetoothPermission.CheckStatusAsync();
                if (bluetoothPermissionStatus != PermissionStatus.Granted && !_isBluetoothPermissionRequested)
                {
                    _isRequestingPermission = true;
                    bluetoothPermissionStatus = await _bluetoothPermission.RequestAsync();
                    _isBluetoothPermissionRequested = true;
                    _isRequestingPermission = false;

                    _disappearingTokenSource?.Token.ThrowIfCancellationRequested();
                }

                if (bluetoothPermissionStatus != PermissionStatus.Granted)
                {
                    await _dialogService.ShowMessageBoxAsync(
                        Translate("Warning"),
                        Translate("BluetoothDevicesWillNOTBeAvailable"),
                        Translate("Ok"),
                        _disappearingTokenSource?.Token ?? default);

                    _disappearingTokenSource?.Token.ThrowIfCancellationRequested();
                }

                if (SharedFileStorageService.SharedStorageDirectory != null)
                {
                    var storagePermissionStatus = await _readWriteExternalStoragePermission.CheckStatusAsync();
                    if (storagePermissionStatus != PermissionStatus.Granted && !_isStoragePermissionRequested)
                    {
                        _isRequestingPermission = true;
                        storagePermissionStatus = await _readWriteExternalStoragePermission.RequestAsync();
                        _isStoragePermissionRequested = true;
                        _isRequestingPermission = false;

                        _disappearingTokenSource?.Token.ThrowIfCancellationRequested();
                    }

                    SharedFileStorageService.IsPermissionGranted = storagePermissionStatus == PermissionStatus.Granted;
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private async Task ImportCreationAsync()
        {
            try
            {
                var creationFilesMap = FileHelper.EnumerateDirectoryFilesToFilenameMap(SharedFileStorageService.SharedStorageDirectory!, $"*.{FileHelper.CreationFileExtension}");
                if (creationFilesMap?.Any() ?? false)
                {
                    var result = await _dialogService.ShowSelectionDialogAsync(
                        creationFilesMap.Keys,
                        Translate("Creations"),
                        Translate("Cancel"),
                        _disappearingTokenSource?.Token ?? default);

                    if (result.IsOk)
                    {
                        try
                        {
                            await _creationManager.ImportCreationAsync(creationFilesMap[result.SelectedItem]);
                        }
                        catch (Exception)
                        {
                            await _dialogService.ShowMessageBoxAsync(
                                Translate("Error"),
                                Translate("FailedToImportCreation"),
                                Translate("Ok"),
                                _disappearingTokenSource?.Token ?? default);
                        }
                    }
                }
                else
                {
                    await _dialogService.ShowMessageBoxAsync(
                        Translate("Information"),
                        Translate("NoCreationsToImport"),
                        Translate("Ok"),
                        _disappearingTokenSource?.Token ?? default);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private async Task LoadCreationsAndDevicesAsync()
        {
            try
            {
                if (_isLoaded)
                {
                    return;
                }

                await _dialogService.ShowProgressDialogAsync(
                    false,
                    async (progressDialog, token) =>
                    {
                        await _creationManager.LoadCreationsAndSequencesAsync();
                        await _deviceManager.LoadDevicesAsync();
                        _isLoaded = true;
                    },
                    Translate("Loading"),
                    token: _disappearingTokenSource?.Token ?? default);
            }
            catch (OperationCanceledException)
            {
            }
        }

        private async Task AddCreationAsync()
        {
            try
            {
                var result = await _dialogService.ShowInputDialogAsync(
                    string.Empty,
                    Translate("CreationName"),
                    Translate("Create"),
                    Translate("Cancel"),
                    KeyboardType.Text,
                    (creationName) => !string.IsNullOrEmpty(creationName),
                    _disappearingTokenSource?.Token ?? default);

                if (result.IsOk)
                {
                    if (string.IsNullOrWhiteSpace(result.Result))
                    {
                        await _dialogService.ShowMessageBoxAsync(
                            Translate("Warning"),
                            Translate("CreationNameCanNotBeEmpty"),
                            Translate("Ok"),
                            _disappearingTokenSource?.Token ?? default);

                        return;
                    }

                    Creation? creation = null;
                    await _dialogService.ShowProgressDialogAsync(
                        false,
                        async (progressDialog, token) =>
                        {
                            creation = await _creationManager.AddCreationAsync(result.Result);
                            await _creationManager.AddControllerProfileAsync(creation, Translate("DefaultProfile"));
                        },
                        Translate("Creating"),
                        token: _disappearingTokenSource?.Token ?? default);

                    await NavigationService.NavigateToAsync<CreationPageViewModel>(new NavigationParameters(("creation", creation!)));
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private async Task DeleteCreationAsync(Creation creation)
        {
            try
            {
                if (await _dialogService.ShowQuestionDialogAsync(
                    Translate("Confirm"),
                    $"{Translate("AreYouSureToDeleteCreation")} '{creation.Name}'?",
                    Translate("Yes"),
                    Translate("No"),
                    _disappearingTokenSource?.Token ?? default))
                {
                    await _dialogService.ShowProgressDialogAsync(
                        false,
                        async (progressDialog, token) => await _creationManager.DeleteCreationAsync(creation),
                        Translate("Deleting"),
                        token: _disappearingTokenSource?.Token ?? default);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private async Task PlayAsync(Creation creation)
        {
            try
            {
                var validationResult = _playLogic.ValidateCreation(creation);

                string warning = string.Empty;
                switch (validationResult)
                {
                    case CreationValidationResult.MissingControllerAction:
                        warning = Translate("NoControllerActions");
                        break;

                    case CreationValidationResult.MissingDevice:
                        warning = Translate("MissingDevices");
                        break;

                    case CreationValidationResult.MissingSequence:
                        warning = Translate("MissingSequence");
                        break;
                }

                if (validationResult == CreationValidationResult.Ok)
                {
                    await NavigationService.NavigateToAsync<PlayerPageViewModel>(new NavigationParameters(("creation", creation)));
                }
                else
                {
                    await _dialogService.ShowMessageBoxAsync(
                        Translate("Warning"),
                        warning,
                        Translate("Ok"),
                        _disappearingTokenSource?.Token ?? default);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }
    }
}
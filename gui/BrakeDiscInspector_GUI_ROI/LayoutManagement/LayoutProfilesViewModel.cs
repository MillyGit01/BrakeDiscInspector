using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using BrakeDiscInspector_GUI_ROI.Workflow;

namespace BrakeDiscInspector_GUI_ROI.LayoutManagement
{
    public sealed class LayoutProfilesViewModel : INotifyPropertyChanged
    {
        private readonly ILayoutProfileStore _store;
        private readonly ILayoutHost _host;
        private LayoutProfileInfo? _selectedProfile;
        private readonly AsyncCommand _refreshCommand;
        private readonly AsyncCommand _loadSelectedCommand;
        private readonly AsyncCommand _saveCurrentAsNewCommand;
        private readonly AsyncCommand _deleteSelectedCommand;

        public LayoutProfilesViewModel(ILayoutProfileStore store, ILayoutHost host)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _host = host ?? throw new ArgumentNullException(nameof(host));

            Profiles = new ObservableCollection<LayoutProfileInfo>();

            _refreshCommand = new AsyncCommand(_ => RefreshAsync());
            _loadSelectedCommand = new AsyncCommand(_ => LoadSelectedAsync(), _ => SelectedProfile != null);
            _saveCurrentAsNewCommand = new AsyncCommand(_ => SaveCurrentAsNewAsync());
            _deleteSelectedCommand = new AsyncCommand(_ => DeleteSelectedAsync(), _ => SelectedProfile != null && !(SelectedProfile?.IsLastLayout ?? false));

            RefreshCommand = _refreshCommand;
            LoadSelectedCommand = _loadSelectedCommand;
            SaveCurrentAsNewCommand = _saveCurrentAsNewCommand;
            DeleteSelectedCommand = _deleteSelectedCommand;
        }

        public ObservableCollection<LayoutProfileInfo> Profiles { get; }

        public LayoutProfileInfo? SelectedProfile
        {
            get => _selectedProfile;
            set
            {
                if (!ReferenceEquals(_selectedProfile, value))
                {
                    _selectedProfile = value;
                    OnPropertyChanged();
                    _loadSelectedCommand.RaiseCanExecuteChanged();
                    _deleteSelectedCommand.RaiseCanExecuteChanged();
                }
            }
        }

        public ICommand RefreshCommand { get; }
        public ICommand LoadSelectedCommand { get; }
        public ICommand SaveCurrentAsNewCommand { get; }
        public ICommand DeleteSelectedCommand { get; }

        public event PropertyChangedEventHandler? PropertyChanged;

        private Task RefreshAsync()
        {
            var profiles = _store.GetProfiles();

            Profiles.Clear();
            foreach (var profile in profiles)
            {
                Profiles.Add(profile);
            }

            if (_selectedProfile != null)
            {
                SelectedProfile = Profiles.FirstOrDefault(p => string.Equals(p.FilePath, _selectedProfile.FilePath, StringComparison.OrdinalIgnoreCase));
            }

            return Task.CompletedTask;
        }

        private Task LoadSelectedAsync()
        {
            var profile = SelectedProfile;
            if (profile == null)
            {
                return Task.CompletedTask;
            }

            var layout = _store.LoadLayout(profile);
            _host.ApplyLayout(layout, "manual-load");
            return Task.CompletedTask;
        }

        private async Task SaveCurrentAsNewAsync()
        {
            var layout = _host.CurrentLayout;
            if (layout == null)
            {
                return;
            }

            var saved = _store.SaveNewLayout(layout);
            await RefreshAsync();
            SelectedProfile = Profiles.FirstOrDefault(p => string.Equals(p.FilePath, saved.FilePath, StringComparison.OrdinalIgnoreCase));
        }

        private async Task DeleteSelectedAsync()
        {
            var profile = SelectedProfile;
            if (profile == null)
            {
                return;
            }

            if (profile.IsLastLayout)
            {
                return;
            }

            _store.DeleteLayout(profile);
            await RefreshAsync();
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using TrueFlac.Core;
using Path = System.IO.Path;

namespace TrueFlac.App;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<TrackAuditResult> _verifiedTracks = new();
    private readonly ObservableCollection<TrackAuditResult> _rejectedTracks = new();
    private readonly AudioPlaybackEngine _engine = new();
    private readonly ArchiveOrgClient _archive = new();
    private readonly DispatcherTimer _progressTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly AppPreferences _prefs;
    private readonly bool _isInitialized;
    private bool _isDraggingSeek;
    private bool _suppressArchiveItemSelection;
    private ArchiveSearchItem? _selectedArchiveItem;

    public MainWindow()
    {
        InitializeComponent();
        _prefs = PreferencesStore.Load();

        VerifiedGrid.ItemsSource = _verifiedTracks;
        RejectedGrid.ItemsSource = _rejectedTracks;

        LoadPreferencesToControls();
        LoadAudioDevices();

        _engine.PlaybackEnded += () => Dispatcher.Invoke(PlayNextTrack);
        _engine.OutputChanged += info => Dispatcher.Invoke(() => UpdateOutputBadge(info));
        _progressTimer.Tick += ProgressTimer_Tick;
        _progressTimer.Start();

        _isInitialized = true;
        Loaded += MainWindow_Loaded;
        Closing += (_, _) => { _progressTimer.Stop(); _engine.Dispose(); _archive.Dispose(); SavePreferences(); };
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_prefs.WatchedPaths.Count > 0)
            await AuditPathsAsync(_prefs.WatchedPaths, rememberSources: false);

        string[] args = Environment.GetCommandLineArgs().Skip(1).Where(a => !string.IsNullOrWhiteSpace(a)).ToArray();
        if (args.Length == 0) return;

        var localArgs = new List<string>();
        foreach (string arg in args)
        {
            if (IsHttpUrl(arg))
                await HandleRemoteLinkAsync(arg);
            else
                localArgs.Add(arg);
        }

        if (localArgs.Count > 0)
            await AuditPathsAsync(localArgs, rememberSources: false);
    }

    private void LoadPreferencesToControls()
    {
        ExclusiveCheck.IsChecked = _prefs.PreferWasapiExclusive;
        RejectTranscodeCheck.IsChecked = _prefs.RejectSuspectedTranscodes;
        RequireMd5Check.IsChecked = _prefs.RequireValidMd5;
        _engine.PreferExclusive = _prefs.PreferWasapiExclusive;

        string targetTag = $"{_prefs.MinimumBitsPerSample}|{_prefs.MinimumSampleRate}";
        foreach (ComboBoxItem item in ThresholdCombo.Items)
        {
            if (string.Equals(item.Tag?.ToString(), targetTag, StringComparison.Ordinal))
            {
                ThresholdCombo.SelectedItem = item;
                break;
            }
        }
    }

    private void LoadAudioDevices()
    {
        var devices = AudioPlaybackEngine.GetOutputDevices();
        DeviceCombo.ItemsSource = devices;
        DeviceCombo.DisplayMemberPath = nameof(AudioDeviceOption.Name);
        var chosen = devices.FirstOrDefault(d => d.Id == _prefs.PreferredDeviceId) ?? devices.FirstOrDefault(d => d.IsDefault);
        if (chosen is not null)
        {
            DeviceCombo.SelectedItem = chosen;
            _engine.SetOutputDevice(chosen.Id);
        }
    }

    private QualityPolicy CurrentPolicy()
    {
        int bits = 16, rate = 44100;
        if (ThresholdCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            var parts = tag.Split('|');
            if (parts.Length == 2) { int.TryParse(parts[0], out bits); int.TryParse(parts[1], out rate); }
        }
        return new QualityPolicy
        {
            MinimumBitsPerSample = bits,
            MinimumSampleRate = rate,
            RejectSuspectedTranscodes = RejectTranscodeCheck.IsChecked == true,
            RequireValidMd5 = RequireMd5Check.IsChecked == true
        };
    }

    private void SavePreferences()
    {
        var p = CurrentPolicy();
        _prefs.MinimumBitsPerSample = p.MinimumBitsPerSample;
        _prefs.MinimumSampleRate = p.MinimumSampleRate;
        _prefs.RejectSuspectedTranscodes = p.RejectSuspectedTranscodes;
        _prefs.RequireValidMd5 = p.RequireValidMd5;
        _prefs.PreferWasapiExclusive = ExclusiveCheck.IsChecked == true;
        _prefs.PreferredDeviceId = (DeviceCombo.SelectedItem as AudioDeviceOption)?.Id;
        PreferencesStore.Save(_prefs);
    }

    private void DeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isInitialized) return;
        if (DeviceCombo.SelectedItem is AudioDeviceOption opt)
            _engine.SetOutputDevice(opt.Id);
        SavePreferences();
    }

    private void ExclusiveCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized) return;
        _engine.PreferExclusive = ExclusiveCheck.IsChecked == true;
        SavePreferences();
    }

    private async void PolicyControl_Changed(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized) return;
        SavePreferences();
        await ReapplyPolicyToLoadedTracksAsync();
    }

    private async Task ReapplyPolicyToLoadedTracksAsync()
    {
        var all = _verifiedTracks.Concat(_rejectedTracks).ToList();
        if (all.Count == 0) return;

        TrackAuditResult? preferred = (VerifiedGrid.SelectedItem as TrackAuditResult)
            ?? (RejectedGrid.SelectedItem as TrackAuditResult)
            ?? _engine.CurrentTrack;

        QualityPolicy policy = CurrentPolicy();
        var updated = await Task.Run(() =>
            all.Select(t => FlacGatekeeper.ReevaluatePolicy(t, policy)).ToList());

        _verifiedTracks.Clear();
        _rejectedTracks.Clear();
        foreach (var track in updated)
        {
            if (track.IsPlayable) _verifiedTracks.Add(track);
            else _rejectedTracks.Add(track);
        }

        UpdateTabCounts();
        TrackAuditResult? active = preferred is not null
            ? updated.FirstOrDefault(t => string.Equals(t.FilePath, preferred.FilePath, StringComparison.OrdinalIgnoreCase))
            : updated.FirstOrDefault();
        if (active is not null) ShowTrackInInspector(active);
    }

    private static bool IsHttpUrl(string input) =>
        Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
            await AuditPathsAsync(paths, rememberSources: true);
    }

    private void InputLinkBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) AuditLinkButton_Click(sender, e);
    }

    private async void AuditLinkButton_Click(object sender, RoutedEventArgs e)
    {
        string raw = InputLinkBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(raw)) return;

        if (IsHttpUrl(raw))
        {
            await HandleRemoteLinkAsync(raw);
            return;
        }
        await AuditPathsAsync(new[] { raw }, rememberSources: true);
    }

    private async void OpenFilesButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Multiselect = true,
            Title = "Seleccionar pistas de audio para auditar",
            Filter = "Archivos de audio|*.flac;*.mp3;*.m4a;*.aac;*.ogg;*.opus;*.wav;*.wma|Todos los archivos|*.*"
        };
        if (dlg.ShowDialog() == true)
            await AuditPathsAsync(dlg.FileNames, rememberSources: true);
    }

    private async void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Seleccionar carpeta de música para auditar" };
        if (dlg.ShowDialog() == true)
            await AuditPathsAsync(new[] { dlg.FolderName }, rememberSources: true);
    }

    private async void ReauditAllButton_Click(object sender, RoutedEventArgs e)
    {
        var allPaths = _verifiedTracks.Select(t => t.FilePath)
            .Concat(_rejectedTracks.Select(t => t.FilePath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (allPaths.Count == 0 && _prefs.WatchedPaths.Count > 0)
            allPaths = _prefs.WatchedPaths.ToList();

        _verifiedTracks.Clear();
        _rejectedTracks.Clear();
        await AuditPathsAsync(allPaths, rememberSources: false);
    }

    private async Task AuditPathsAsync(IEnumerable<string> rawInputs, bool rememberSources)
    {
        var inputList = rawInputs.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        if (inputList.Count == 0) return;

        if (rememberSources)
        {
            foreach (var p in inputList)
                if (!_prefs.WatchedPaths.Contains(p, StringComparer.OrdinalIgnoreCase))
                    _prefs.WatchedPaths.Add(p);
            SavePreferences();
        }

        QualityPolicy policy = CurrentPolicy();
        InspectorStatusText.Text = "AUDITANDO ARCHIVOS...";

        var results = await Task.Run(() =>
            LinkAndSourceResolver.ExpandLocalInputs(inputList)
                .Select(f => FlacGatekeeper.AuditFile(f, policy))
                .ToList());

        foreach (var r in results)
        {
            RemoveExistingByPath(r.FilePath);
            if (r.IsPlayable) _verifiedTracks.Add(r);
            else _rejectedTracks.Add(r);
        }

        UpdateTabCounts();
        if (results.Count > 0) ShowTrackInInspector(results[0]);
    }

    private void RemoveExistingByPath(string filePath)
    {
        for (int i = _verifiedTracks.Count - 1; i >= 0; i--)
            if (string.Equals(_verifiedTracks[i].FilePath, filePath, StringComparison.OrdinalIgnoreCase)) _verifiedTracks.RemoveAt(i);
        for (int i = _rejectedTracks.Count - 1; i >= 0; i--)
            if (string.Equals(_rejectedTracks[i].FilePath, filePath, StringComparison.OrdinalIgnoreCase)) _rejectedTracks.RemoveAt(i);
    }

    private void UpdateTabCounts()
    {
        VerifiedTabHeader.Header = $"True FLAC / Aptos ({_verifiedTracks.Count})";
        RejectedTabHeader.Header = $"Rechazados / Cuarentena ({_rejectedTracks.Count})";
    }
}

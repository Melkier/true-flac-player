using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Win32;
using TrueFlac.Core;
using Path = System.IO.Path;

namespace TrueFlac.App;

public partial class MainWindow
{
    private void TrackGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is DataGrid grid && grid.SelectedItem is TrackAuditResult track)
            ShowTrackInInspector(track);
    }

    private void VerifiedGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (VerifiedGrid.SelectedItem is TrackAuditResult track)
            PlayTrack(track);
    }

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_engine.CurrentTrack is not null && (_engine.IsPlaying || _engine.IsPaused))
        {
            _engine.TogglePauseResume();
            PlayPauseBtn.Content = _engine.IsPlaying ? "⏸ Pausar" : "▶ Reproducir";
            return;
        }

        if (VerifiedGrid.SelectedItem is TrackAuditResult selected)
            PlayTrack(selected);
        else if (_verifiedTracks.Count > 0)
        {
            VerifiedGrid.SelectedIndex = 0;
            PlayTrack(_verifiedTracks[0]);
        }
    }

    private void PlayTrack(TrackAuditResult track)
    {
        try
        {
            _engine.Volume = (float)(VolumeSlider.Value / 100.0);
            _engine.PreferExclusive = ExclusiveCheck.IsChecked == true;
            var info = _engine.Play(track);
            NowPlayingTitle.Text = $"{track.Title} — {track.Artist}";
            PlayPauseBtn.Content = "⏸ Pausar";
            UpdateOutputBadge(info);
            ShowTrackInInspector(track);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Bloqueo True FLAC", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        _engine.Stop();
        PlayPauseBtn.Content = "▶ Reproducir";
        NowPlayingTitle.Text = "Sin reproducción activa";
        SeekSlider.Value = 0;
        CurrentTimeText.Text = "0:00";
    }

    private void PrevButton_Click(object sender, RoutedEventArgs e) => StepTrack(-1);
    private void NextButton_Click(object sender, RoutedEventArgs e) => PlayNextTrack();
    private void PlayNextTrack() => StepTrack(1);

    private void StepTrack(int delta)
    {
        if (_verifiedTracks.Count == 0) return;
        int currentIdx = _engine.CurrentTrack is not null ? _verifiedTracks.IndexOf(_engine.CurrentTrack) : VerifiedGrid.SelectedIndex;
        int nextIdx = currentIdx < 0 ? 0 : (currentIdx + delta + _verifiedTracks.Count) % _verifiedTracks.Count;
        VerifiedGrid.SelectedIndex = nextIdx;
        PlayTrack(_verifiedTracks[nextIdx]);
    }

    private void ProgressTimer_Tick(object? sender, EventArgs e)
    {
        if (_isDraggingSeek || _engine.CurrentTrack is null) return;
        var cur = _engine.CurrentTime;
        var tot = _engine.TotalTime;
        CurrentTimeText.Text = FormatTime(cur);
        TotalTimeText.Text = FormatTime(tot);
        SeekSlider.Maximum = Math.Max(1, tot.TotalSeconds);
        SeekSlider.Value = Math.Min(SeekSlider.Maximum, cur.TotalSeconds);
    }

    private void SeekSlider_DragStarted(object sender, MouseButtonEventArgs e) => _isDraggingSeek = true;

    private void SeekSlider_DragCompleted(object sender, MouseButtonEventArgs e)
    {
        _isDraggingSeek = false;
        _engine.Seek(TimeSpan.FromSeconds(SeekSlider.Value));
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_isInitialized) return;
        int pct = (int)Math.Round(VolumeSlider.Value);
        _engine.Volume = (float)(VolumeSlider.Value / 100.0);
        VolumeLabel.Text = pct >= 100 ? "Vol: 100% (Bit-Perfect)" : $"Vol: {pct}% (DSP activo)";
    }

    private void VolumeSlider_Commit(object sender, MouseButtonEventArgs e) => CommitVolumeMode();

    private void VolumeSlider_KeyUp(object sender, KeyEventArgs e) => CommitVolumeMode();

    private void CommitVolumeMode()
    {
        if (!_isInitialized) return;
        try
        {
            _engine.CommitVolumeChange();
            UpdateOutputBadge(_engine.CurrentOutput);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"No se pudo actualizar el modo de volumen: {ex.Message}", "True FLAC Player", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void UpdateOutputBadge(PlaybackOutputInfo? info)
    {
        if (info is null)
        {
            OutputModeBadge.Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x29, 0x3B));
            OutputModeText.Foreground = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8));
            OutputModeText.Text = "MOTOR DETENIDO";
            return;
        }

        OutputModeText.Text = $"{info.ModeBadge} · {info.DeviceName}";
        if (info.IsExclusive && info.IsBitPerfect)
        {
            OutputModeBadge.Background = new SolidColorBrush(Color.FromRgb(0x06, 0x4E, 0x3B));
            OutputModeText.Foreground = new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99));
        }
        else if (info.IsExclusive)
        {
            OutputModeBadge.Background = new SolidColorBrush(Color.FromRgb(0x0E, 0x2A, 0x38));
            OutputModeText.Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0xD3, 0xEE));
        }
        else
        {
            OutputModeBadge.Background = new SolidColorBrush(Color.FromRgb(0x45, 0x1A, 0x03));
            OutputModeText.Foreground = new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24));
        }
    }

    private static string FormatTime(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
}

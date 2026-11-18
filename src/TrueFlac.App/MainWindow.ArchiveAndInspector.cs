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
    private void ShowTrackInInspector(TrackAuditResult track)
    {
        InspectorTitle.Text = track.Title;
        InspectorArtist.Text = $"{track.Artist} · {track.Album}";
        InspectorStatusText.Text = $"{track.StatusLabel} · {track.VerdictHeadline}";
        InspectorDetailText.Text = track.TechnicalDetail;

        (Color bg, Color fg) = track.Status switch
        {
            AuditStatus.VerifiedTrueFlac => (Color.FromRgb(0x06, 0x4E, 0x3B), Color.FromRgb(0x34, 0xD3, 0x99)),
            AuditStatus.SuspectTranscode => (Color.FromRgb(0x45, 0x1A, 0x03), Color.FromRgb(0xFB, 0xBF, 0x24)),
            _ => (Color.FromRgb(0x45, 0x0A, 0x0A), Color.FromRgb(0xF8, 0x71, 0x71))
        };
        InspectorStatusBadge.Background = new SolidColorBrush(bg);
        InspectorStatusText.Foreground = new SolidColorBrush(fg);

        if (track.StreamInfo is { } si)
        {
            string md5Label = track.Md5Verified switch
            {
                true => "Íntegro (coincide)",
                false => "ERROR (corrupto)",
                null => si.HasNonZeroMd5 ? "No verificado" : "No declarado"
            };
            string cutoffLabel = track.Spectral is not null ? $"{track.Spectral.EstimatedCutoffKhz:0.0} kHz" : "--";
            InspectorSpecsText.Text =
                $"Formato: {si.FormatBadge} ({si.Channels} ch)\n" +
                $"Bitrate promedio: {si.AverageBitrateKbps:0} kbps ({si.CompressionRatioPercent:0.0}% raw)\n" +
                $"MD5 PCM: {md5Label}\n" +
                $"Corte FFT: {cutoffLabel}";
        }
        else
        {
            InspectorSpecsText.Text = $"Archivo: {track.FileName}\nTamaño: {track.FileSizeBytes / 1024.0:0.0} KB\nFormato: Rechazado antes de decodificar";
        }

        LoadCoverImage(track.CoverArtBytes);
        DrawSpectrum(track.Spectral);
    }

    private void LoadCoverImage(byte[]? coverBytes)
    {
        if (coverBytes is null || coverBytes.Length == 0)
        {
            CoverImage.Source = null;
            CoverPlaceholderText.Visibility = Visibility.Visible;
            return;
        }
        try
        {
            using var ms = new MemoryStream(coverBytes);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            CoverImage.Source = bmp;
            CoverPlaceholderText.Visibility = Visibility.Collapsed;
        }
        catch
        {
            CoverImage.Source = null;
            CoverPlaceholderText.Visibility = Visibility.Visible;
        }
    }

    private void DrawSpectrum(SpectralAnalysisResult? spectral)
    {
        SpectrumCanvas.Children.Clear();
        double w = SpectrumCanvas.ActualWidth > 20 ? SpectrumCanvas.ActualWidth : 316;
        double h = SpectrumCanvas.ActualHeight > 20 ? SpectrumCanvas.ActualHeight : 116;

        if (spectral is null || spectral.BandSpectrumDb.Length == 0) return;

        // Reference vertical lines at 16 kHz and 20 kHz
        DrawFreqMarker(w, h, 16.0, "16k", Color.FromRgb(0xF5, 0x9E, 0x0B));
        DrawFreqMarker(w, h, 20.0, "20k", Color.FromRgb(0x10, 0xB9, 0x81));

        int count = spectral.BandSpectrumDb.Length;
        double barW = Math.Max(3, (w - 4) / count - 2);
        for (int i = 0; i < count; i++)
        {
            double db = Math.Clamp(spectral.BandSpectrumDb[i], -85.0, 5.0);
            double norm = (db + 85.0) / 90.0;
            double barH = Math.Max(2, norm * (h - 14));
            double freq = spectral.BandFrequenciesKhz[i];

            Color c = spectral.IsLikelyTranscode && freq >= spectral.EstimatedCutoffKhz
                ? Color.FromRgb(0xEF, 0x44, 0x44)
                : (freq >= 16.0 ? Color.FromRgb(0x10, 0xB9, 0x81) : Color.FromRgb(0x22, 0xD3, 0xEE));

            var rect = new Rectangle
            {
                Width = barW,
                Height = barH,
                Fill = new SolidColorBrush(c),
                RadiusX = 1.5,
                RadiusY = 1.5
            };
            Canvas.SetLeft(rect, 2 + i * (barW + 2));
            Canvas.SetTop(rect, h - barH - 2);
            SpectrumCanvas.Children.Add(rect);
        }
    }

    private void DrawFreqMarker(double w, double h, double freqKhz, string label, Color color)
    {
        double x = Math.Clamp((freqKhz - 0.5) / 21.55 * w, 4, w - 20);
        var line = new Line
        {
            X1 = x, X2 = x, Y1 = 0, Y2 = h,
            Stroke = new SolidColorBrush(Color.FromArgb(120, color.R, color.G, color.B)),
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 3, 2 }
        };
        SpectrumCanvas.Children.Add(line);
        var txt = new TextBlock { Text = label, FontSize = 9, Foreground = new SolidColorBrush(color) };
        Canvas.SetLeft(txt, x + 2);
        Canvas.SetTop(txt, 1);
        SpectrumCanvas.Children.Add(txt);
    }

    private async Task HandleRemoteLinkAsync(string url)
    {
        var (kind, val) = ArchiveOrgClient.ClassifyUrl(url);

        if (kind == ArchiveLinkKind.SearchQuery)
        {
            MainTabs.SelectedItem = ArchiveTab;
            ArchiveQueryBox.Text = val;
            await ExecuteArchiveSearchAsync(val);
            return;
        }

        if (kind == ArchiveLinkKind.ItemIdentifier)
        {
            MainTabs.SelectedItem = ArchiveTab;
            ArchiveQueryBox.Text = val;
            ArchiveStatusText.Text = $"Cargando archivos FLAC de '{val}' en Archive.org...";
            try
            {
                var (item, files) = await _archive.GetItemWithFlacFilesAsync(val);
                _selectedArchiveItem = item;
                _suppressArchiveItemSelection = true;
                try
                {
                    ArchiveItemsGrid.ItemsSource = item is not null ? new[] { item } : null;
                    if (item is not null) ArchiveItemsGrid.SelectedIndex = 0;
                }
                finally
                {
                    _suppressArchiveItemSelection = false;
                }
                ArchiveFilesGrid.ItemsSource = files;
                ArchiveStatusText.Text = files.Count > 0
                    ? $"Se encontraron {files.Count} archivos .flac en '{val}'."
                    : $"El ítem '{val}' de Archive.org no contiene archivos .flac (rechazado por política True FLAC).";
            }
            catch (Exception ex) { ArchiveStatusText.Text = $"Error consultando Archive.org: {ex.Message}"; }
            return;
        }

        string downloadUrl = kind == ArchiveLinkKind.DirectFlacUrl ? val : url;
        bool isDirectFlac = kind == ArchiveLinkKind.DirectFlacUrl ||
                            (kind == ArchiveLinkKind.None && url.Split('?')[0].EndsWith(".flac", StringComparison.OrdinalIgnoreCase));

        if (!isDirectFlac)
        {
            var rejected = new TrackAuditResult
            {
                FilePath = url,
                FileName = Path.GetFileName(Uri.TryCreate(url, UriKind.Absolute, out var badUri) ? badUri.LocalPath : url),
                Title = url,
                Artist = "Enlace remoto",
                Album = "Bloqueado",
                Status = AuditStatus.Rejected,
                Reason = RejectionReason.RemoteNotFlac,
                VerdictHeadline = "Enlace remoto no FLAC rechazado",
                TechnicalDetail = "Solo se admiten enlaces directos .flac o búsquedas/ítems de Archive.org con archivos FLAC."
            };
            RemoveExistingByPath(rejected.FilePath);
            _rejectedTracks.Add(rejected);
            UpdateTabCounts();
            ShowTrackInInspector(rejected);
            MessageBox.Show(rejected.TechnicalDetail, "True FLAC Gatekeeper", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            string fileName = Path.GetFileName(new Uri(downloadUrl).LocalPath);
            if (string.IsNullOrWhiteSpace(fileName)) fileName = "remote_track.flac";
            string dest = Path.Combine(_prefs.DownloadDirectory, "Enlaces", fileName);
            InspectorStatusText.Text = "DESCARGANDO (0%)...";
            var progress = new Progress<double>(pct => InspectorStatusText.Text = $"DESCARGANDO ({pct:0}%)...");
            await _archive.DownloadFileAsync(downloadUrl, dest, progress);
            await AuditPathsAsync(new[] { dest }, rememberSources: true);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"No se pudo descargar/auditar el enlace: {ex.Message}", "Error de enlace", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ArchiveQueryBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) ArchiveSearchButton_Click(sender, e);
    }

    private async void ArchiveSearchButton_Click(object sender, RoutedEventArgs e)
    {
        string q = ArchiveQueryBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(q)) return;
        await ExecuteArchiveSearchAsync(q);
    }

    private async Task ExecuteArchiveSearchAsync(string query)
    {
        ArchiveStatusText.Text = $"Consultando Archive.org ({query})...";
        ArchiveItemsGrid.ItemsSource = null;
        ArchiveFilesGrid.ItemsSource = null;
        _selectedArchiveItem = null;

        try
        {
            var items = await _archive.SearchFlacItemsAsync(query);
            ArchiveItemsGrid.ItemsSource = items;
            if (items.Count > 0)
            {
                ArchiveStatusText.Text = $"Se encontraron {items.Count} ítems con etiqueta FLAC. Cargando pistas del primero...";
                ArchiveItemsGrid.SelectedIndex = 0;
            }
            else
            {
                ArchiveStatusText.Text = "Sin resultados FLAC en Archive.org para esa búsqueda.";
            }
        }
        catch (Exception ex) { ArchiveStatusText.Text = $"Error en consulta Archive.org: {ex.Message}"; }
    }

    private async void ArchiveItemsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressArchiveItemSelection) return;
        if (ArchiveItemsGrid.SelectedItem is not ArchiveSearchItem item) return;
        _selectedArchiveItem = item;
        ArchiveStatusText.Text = $"Leyendo metadatos de '{item.Title}'...";
        try
        {
            var files = await _archive.GetFlacFilesForItemAsync(item.Identifier);
            ArchiveFilesGrid.ItemsSource = files;
            ArchiveStatusText.Text = files.Count > 0
                ? $"{files.Count} archivos .flac disponibles en '{item.Identifier}'."
                : "Este ítem no contiene archivos .flac reales en sus metadatos.";
        }
        catch (Exception ex) { ArchiveStatusText.Text = $"Error leyendo metadatos: {ex.Message}"; }
    }

    private void ChangeDownloadFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Carpeta destino para descargas verificadas",
            InitialDirectory = Directory.Exists(_prefs.DownloadDirectory) ? _prefs.DownloadDirectory : null
        };
        if (dlg.ShowDialog() == true)
        {
            _prefs.DownloadDirectory = dlg.FolderName;
            SavePreferences();
            ArchiveStatusText.Text = $"Destino actualizado: {_prefs.DownloadDirectory}";
        }
    }

    private async void ArchiveDownloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (ArchiveFilesGrid.ItemsSource is not IReadOnlyList<ArchiveFlacFile> allFiles || allFiles.Count == 0)
        {
            ArchiveStatusText.Text = "Primero selecciona un ítem con archivos .flac.";
            return;
        }

        var selected = ArchiveFilesGrid.SelectedItems.Cast<ArchiveFlacFile>().ToList();
        var toDownload = selected.Count > 0 ? selected : allFiles.ToList();
        string itemDir = Path.Combine(_prefs.DownloadDirectory, SanitizeFolder(_selectedArchiveItem?.Identifier ?? "ArchiveItem"));

        ArchiveDownloadBtn.IsEnabled = false;
        var downloadedPaths = new List<string>();
        try
        {
            for (int i = 0; i < toDownload.Count; i++)
            {
                var f = toDownload[i];
                string safeName = Path.GetFileName(f.FileName.Replace('/', Path.DirectorySeparatorChar));
                string dest = Path.Combine(itemDir, safeName);
                int idx = i + 1;
                ArchiveStatusText.Text = $"Descargando ({idx}/{toDownload.Count}): {safeName} (0%)...";
                var progress = new Progress<double>(pct =>
                    ArchiveStatusText.Text = $"Descargando ({idx}/{toDownload.Count}): {safeName} ({pct:0}%)...");
                await _archive.DownloadFileAsync(f.DownloadUrl, dest, progress);
                downloadedPaths.Add(dest);
            }

            ArchiveStatusText.Text = $"Auditando {downloadedPaths.Count} pistas con True FLAC Gatekeeper...";
            await AuditPathsAsync(downloadedPaths, rememberSources: true);
            ArchiveStatusText.Text = $"Completado: {downloadedPaths.Count} pistas descargadas y auditadas en {itemDir}.";
        }
        catch (Exception ex)
        {
            ArchiveStatusText.Text = $"Error en descarga: {ex.Message}";
        }
        finally
        {
            ArchiveDownloadBtn.IsEnabled = true;
        }
    }

    private static string SanitizeFolder(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "ArchiveFLAC" : name;
    }
}

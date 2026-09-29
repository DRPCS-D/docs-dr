using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using DocsDR.App.Services;
using DocsDR.App.Views;
using DocsDR.Core;
using DocsDR.TableExtraction;
using Microsoft.Win32;

namespace DocsDR.App.ViewModels;

/// <summary>Herramientas de uso diario: buscar y reemplazar, OCR a PDF buscable, exportar páginas como imágenes.</summary>
public sealed partial class DocumentViewModel
{
    /// <summary>Página a la que ir en cuanto la vista del documento esté lista (al reabrir la sesión anterior).</summary>
    public int? PendingPage { get; set; }

    // ================= Buscar y reemplazar =================

    private sealed record LineEdit(int Page, TextBlockInfo Block, TextLineInfo Line, string NewText, int Matches);

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void ReplaceDialog() =>
        Dialogs.ShowReplace(SearchText.Trim(), (find, replace, matchCase) => ReplaceAll(find, replace, matchCase));

    /// <summary>Reemplaza en todos los renglones del documento. Devuelve el mensaje de resultado.</summary>
    public string ReplaceAll(string find, string replace, bool matchCase)
    {
        if (Editor is not { } ed) return "Este documento no se puede modificar.";
        var cmp = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        // 1) Se localizan los renglones con coincidencias (sin tocar nada todavía).
        var targets = new List<LineEdit>();
        for (int page = 0; page < PageCount; page++)
            foreach (var block in ed.GetTextBlocks(page))
                foreach (var line in block.Lines)
                {
                    int matches = CountOccurrences(line.Text, find, cmp);
                    if (matches == 0) continue;
                    var newText = ReplaceOccurrences(line.Text, find, replace, cmp);
                    if (newText != line.Text) targets.Add(new LineEdit(page, block, line, newText, matches));
                }

        if (targets.Count == 0) return "No se encontraron coincidencias dentro de un renglón.";

        // 2) Todo se aplica como un solo paso de «deshacer». Un renglón que no cabe se deja como estaba.
        int replaced = 0, skipped = 0;
        string? lastError = null;
        var pages = targets.Select(t => t.Page).Distinct().ToArray();
        bool ok = Edit(e =>
        {
            foreach (var t in targets)
            {
                try
                {
                    RewriteLine(e, t);
                    replaced += t.Matches;
                }
                catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
                {
                    skipped += t.Matches;
                    lastError = ex.Message;
                }
            }
            if (replaced == 0) throw new OperationCanceledException(); // nada cambió: no se registra ningún paso
        }, false, pages);
        if (!ok) return $"No se pudo reemplazar: {lastError ?? "el texto nuevo no cabe en el espacio disponible."}";

        int pageCount = pages.Length;
        var msg = $"Se reemplazaron {replaced} coincidencia(s) en {pageCount} página(s).";
        if (skipped > 0) msg += $" {skipped} no se pudieron cambiar: {lastError}";
        SearchStatus = $"Reemplazadas {replaced} coincidencia(s)";
        return msg;
    }

    /// <summary>Reescribe un renglón con el texto nuevo, en su misma línea base y conservando los formatos de lo que no cambió.</summary>
    private void RewriteLine(IPdfEditor ed, LineEdit t)
    {
        var line = t.Line;
        var format = line.Format;
        var align = NumberParser.TryParse(line.Text, DecimalSeparator.Auto, out _, out _) ? TextAlign.Right : TextAlign.Left;
        double size = line.Runs?.Max(r => r.Format.Size) ?? format.Size;

        // Solo una franja alrededor de la línea base (ver CommitTextEdit): no debe rozar los renglones vecinos.
        var b = line.Box;
        double inset = Math.Min(1.5, b.Height * 0.15);
        var erase = new PdfRect(b.X0, Math.Max(b.Y0 + inset, line.Baseline - 0.72 * size), b.X1, Math.Min(b.Y1 - inset, line.Baseline));
        var place = PlaceBoxOf(b, false, t.Block, Pages[t.Page].PageWidth, size, align);
        IReadOnlyList<TextRun> runs = line.Runs is { Count: > 1 }
            ? TextRunMapper.Remap(line.Runs, t.NewText)
            : [new TextRun(t.NewText, format)];
        ed.ReplaceText(t.Page, erase, place, t.NewText, format, align, 0, line.Baseline, runs);
    }

    private static int CountOccurrences(string text, string find, StringComparison cmp)
    {
        int count = 0, i = 0;
        while ((i = text.IndexOf(find, i, cmp)) >= 0) { count++; i += find.Length; }
        return count;
    }

    private static string ReplaceOccurrences(string text, string find, string replace, StringComparison cmp)
    {
        var sb = new System.Text.StringBuilder();
        int i = 0, j;
        while ((j = text.IndexOf(find, i, cmp)) >= 0)
        {
            sb.Append(text, i, j - i).Append(replace);
            i = j + find.Length;
        }
        return sb.Append(text, i, text.Length - i).ToString();
    }

    // ================= OCR: PDF escaneado → PDF buscable =================

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task MakeSearchableAsync()
    {
        if (Editor is not { } ed) return;
        if (!ed.OcrAvailable)
        {
            MessageBox.Show(
                "No se encontraron los datos de idioma del OCR (tessdata).\n\n" +
                $"Copia spa.traineddata y eng.traineddata en:\n{Path.Combine(App.DataFolder, "tessdata")}\n" +
                "(o ejecuta scripts/get-tessdata.ps1 desde el código fuente).",
                "PDF buscable (OCR)", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SearchStatus = "Buscando páginas escaneadas…";
        int total = PageCount;
        var candidates = await Task.Run(() => Enumerable.Range(0, total).Where(ed.NeedsOcr).ToList());
        if (candidates.Count == 0)
        {
            SearchStatus = "";
            MessageBox.Show("No hay páginas escaneadas sin texto: este documento ya es buscable.", "PDF buscable (OCR)",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show(
                $"Se reconocerá el texto de {candidates.Count} página(s) escaneada(s). Puede tardar unos segundos por página.\n\n" +
                "La imagen no cambia: se agrega una capa de texto invisible para poder buscar y copiar. Podrás deshacerlo con Ctrl+Z.\n\n¿Continuar?",
                "PDF buscable (OCR)", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            SearchStatus = "";
            return;
        }

        int versionAtStart = _version;
        var recognized = new Dictionary<int, IReadOnlyList<TextWord>>();
        int done = 0;
        var dispatcher = Application.Current.Dispatcher;
        await Task.Run(() =>
        {
            foreach (var page in candidates)
            {
                var words = ed.RecognizePage(page);
                if (words is { Count: > 0 }) recognized[page] = words;
                int n = Interlocked.Increment(ref done);
                dispatcher.BeginInvoke(() => SearchStatus = $"Reconociendo texto… {n} de {candidates.Count} página(s)");
            }
        });

        if (_version != versionAtStart)
        {
            SearchStatus = "";
            MessageBox.Show("El documento cambió mientras se reconocía el texto. Vuelve a ejecutar el comando.", "PDF buscable (OCR)",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (recognized.Count == 0)
        {
            SearchStatus = "No se reconoció texto";
            MessageBox.Show("No se pudo reconocer texto en las páginas escaneadas.", "PDF buscable (OCR)", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        int wordCount = recognized.Values.Sum(w => w.Count);
        if (Edit(e => { foreach (var (page, words) in recognized) e.AddInvisibleText(page, words); }, false, recognized.Keys.ToArray()))
            SearchStatus = $"PDF buscable: {recognized.Count} página(s), {wordCount} palabras. Guarda el documento para conservarlo.";
    }

    // ================= Exportar páginas como imágenes =================

    [RelayCommand]
    private async Task ExportImagesAsync()
    {
        var options = Dialogs.AskExportImages(PageCount, SelectedPageIndexes);
        if (options is null) return;
        var dlg = new OpenFolderDialog { Title = "Carpeta donde guardar las imágenes", InitialDirectory = Path.GetDirectoryName(FilePath) };
        if (dlg.ShowDialog() != true) return;

        var folder = dlg.FolderName;
        var baseName = Path.GetFileNameWithoutExtension(Title);
        string ext = options.Jpeg ? ".jpg" : ".png";
        var doc = Document;
        int done = 0;
        var dispatcher = Application.Current.Dispatcher;
        try
        {
            var files = await Task.Run(() =>
            {
                var written = new List<string>();
                foreach (var page in options.Pages)
                {
                    var rendered = doc.Render(page, options.Dpi / 72.0);
                    var bmp = PageImages.ToBitmap(rendered);
                    BitmapEncoder encoder = options.Jpeg ? new JpegBitmapEncoder { QualityLevel = 92 } : new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bmp));
                    var path = UniquePath(folder, $"{baseName}_p{page + 1:D3}", ext);
                    using (var fs = File.Create(path)) encoder.Save(fs);
                    written.Add(path);
                    int n = Interlocked.Increment(ref done);
                    dispatcher.BeginInvoke(() => SearchStatus = $"Exportando imágenes… {n} de {options.Pages.Count}");
                }
                return written;
            });
            SearchStatus = $"Se exportaron {files.Count} imagen(es) a {folder}";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { ShowError("No se pudieron exportar las imágenes", ex); }
    }

    /// <summary>Ruta libre en la carpeta: nunca pisa un archivo que ya exista.</summary>
    private static string UniquePath(string folder, string name, string ext)
    {
        var path = Path.Combine(folder, name + ext);
        for (int i = 2; File.Exists(path); i++) path = Path.Combine(folder, $"{name} ({i}){ext}");
        return path;
    }
}

using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DocsDR.App.Converter;
using DocsDR.App.Services;
using DocsDR.App.Views;
using DocsDR.Core;
using Microsoft.Win32;

namespace DocsDR.App.ViewModels;

public sealed partial class MainViewModel(IPdfService pdfService, SettingsService settings, UpdateService updates) : ObservableObject
{
    public ObservableCollection<DocumentViewModel> Documents { get; } = [];
    public ObservableCollection<string> RecentFiles { get; } = new(settings.RecentFiles);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDocument))]
    [NotifyCanExecuteChangedFor(nameof(ConvertToExcelCommand), nameof(PrintCommand), nameof(CloseDocumentCommand), nameof(SaveCommand), nameof(SaveAsCommand))]
    private DocumentViewModel? _selectedDocument;

    [ObservableProperty] private string _statusText = "Listo";

    public bool HasDocument => SelectedDocument is not null;

    [RelayCommand]
    private void Open()
    {
        var dlg = new OpenFileDialog { Filter = "Documentos PDF (*.pdf)|*.pdf", Multiselect = true, Title = "Abrir PDF" };
        if (dlg.ShowDialog() == true)
            foreach (var f in dlg.FileNames) OpenPath(f);
    }

    [RelayCommand]
    public void OpenPath(string path)
    {
        var existing = Documents.FirstOrDefault(d => string.Equals(d.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) { SelectedDocument = existing; return; }
        if (!File.Exists(path))
        {
            MessageBox.Show($"No se encontró el archivo:\n{path}", "DOCS-DR", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        IPdfDocument? doc = null;
        try
        {
            doc = pdfService.Open(path);
        }
        catch (PdfPasswordRequiredException)
        {
            var pwd = PasswordDialog.Ask(Path.GetFileName(path));
            if (pwd is null) return;
            try { doc = pdfService.Open(path, pwd); }
            catch (PdfPasswordRequiredException)
            {
                MessageBox.Show("Contraseña incorrecta.", "DOCS-DR", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "No se pudo abrir {Path}", path);
            MessageBox.Show($"No se pudo abrir el archivo:\n{ex.Message}", "DOCS-DR", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var vm = new DocumentViewModel(doc);
        Documents.Add(vm);
        SelectedDocument = vm;
        RememberRecent(path);
        StatusText = $"{vm.Title} — {vm.PageCount} página(s)";
    }

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private void CloseDocument(DocumentViewModel? doc)
    {
        doc ??= SelectedDocument;
        if (doc is null || !ConfirmClose(doc)) return;
        int idx = Documents.IndexOf(doc);
        Documents.Remove(doc);
        doc.Dispose();
        SelectedDocument = Documents.Count == 0 ? null : Documents[Math.Min(idx, Documents.Count - 1)];
    }

    /// <summary>Si el documento tiene cambios sin guardar pregunta qué hacer. Devuelve false si se cancela.</summary>
    public bool ConfirmClose(DocumentViewModel doc)
    {
        if (!doc.IsModified) return true;
        SelectedDocument = doc;
        var answer = MessageBox.Show($"¿Guardar los cambios en «{doc.Title}»?", "DOCS-DR",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return answer switch
        {
            MessageBoxResult.Yes => doc.Save(),
            MessageBoxResult.No => true,
            _ => false,
        };
    }

    /// <summary>Se llama al cerrar la ventana: recorre los documentos modificados.</summary>
    public bool ConfirmCloseAll() => Documents.ToList().All(ConfirmClose);

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private void Save()
    {
        if (SelectedDocument is { CanEdit: true } doc && doc.Save()) StatusText = $"Guardado: {doc.FilePath}";
    }

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private void SaveAs()
    {
        if (SelectedDocument is not { CanEdit: true } doc || !doc.SaveAs()) return;
        RememberRecent(doc.FilePath);
        StatusText = $"Guardado: {doc.FilePath}";
    }

    [RelayCommand]
    private void MergePdfs()
    {
        var files = Dialogs.AskMerge();
        if (files is null) return;
        var dlg = new SaveFileDialog
        {
            Filter = "Documentos PDF (*.pdf)|*.pdf",
            FileName = "unido.pdf",
            InitialDirectory = Path.GetDirectoryName(files[0]),
            Title = "Guardar PDF unido",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            pdfService.Merge(files, dlg.FileName);
        }
        catch (PdfPasswordRequiredException ex)
        {
            MessageBox.Show($"Uno de los archivos está protegido con contraseña y no se puede unir:\n{ex.Message}", "Unir PDFs",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show($"No se pudo guardar el archivo:\n{ex.Message}", "Unir PDFs", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        OpenPath(dlg.FileName);
        StatusText = $"Se unieron {files.Count} archivos";
    }

    private void RememberRecent(string path)
    {
        settings.AddRecent(path);
        RecentFiles.Clear();
        foreach (var r in settings.RecentFiles) RecentFiles.Add(r);
    }

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private void ConvertToExcel()
    {
        if (SelectedDocument is null) return;
        var window = new ConverterWindow(new ConverterViewModel(SelectedDocument.Document))
        {
            Owner = Application.Current.MainWindow,
        };
        window.Show();
    }

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private void Print()
    {
        if (SelectedDocument is null) return;
        PrintService.Print(SelectedDocument.Document, SelectedDocument.Title);
    }

    [RelayCommand]
    private void Exit() => Application.Current.Shutdown();

    // ================= Ayuda =================

    [RelayCommand]
    private void ShowAbout() => new AboutWindow { Owner = Application.Current.MainWindow }.ShowDialog();

    [RelayCommand]
    private void OpenSupport() => AppInfo.OpenUrl(AppInfo.SupportMailto);

    [RelayCommand]
    private void ReportIssue() => AppInfo.OpenUrl(AppInfo.IssuesUrl);

    [RelayCommand]
    private void OpenRepository() => AppInfo.OpenUrl(AppInfo.RepositoryUrl);

    [RelayCommand]
    private async Task CheckUpdatesAsync()
    {
        if (!updates.IsInstalled)
        {
            // Copia sin instalar (código fuente o carpeta copiada): no puede actualizarse sola.
            if (MessageBox.Show(
                    "Esta copia de DOCS-DR no fue instalada con el instalador, por eso no puede actualizarse sola.\n\n" +
                    "¿Abrir la página de descargas para ver la última versión?",
                    "Buscar actualizaciones", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                AppInfo.OpenUrl(AppInfo.ReleasesUrl);
            return;
        }

        try
        {
            StatusText = "Buscando actualizaciones…";
            var update = await updates.CheckAsync();
            if (update is null)
            {
                StatusText = "DOCS-DR está actualizado";
                MessageBox.Show($"Ya tienes la última versión ({AppInfo.Version}).", "Buscar actualizaciones",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var version = UpdateService.VersionOf(update);
            if (MessageBox.Show(
                    $"Hay una versión nueva: {version} (tienes la {AppInfo.Version}).\n\n" +
                    "Se descargará, se cerrará DOCS-DR y se volverá a abrir ya actualizado. ¿Continuar?",
                    "Buscar actualizaciones", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                StatusText = "Actualización pospuesta";
                return;
            }

            // Antes de reiniciar se ofrece guardar los documentos con cambios.
            if (!ConfirmCloseAll()) { StatusText = "Actualización cancelada"; return; }

            var progress = new Progress<int>(p => StatusText = $"Descargando la versión {version}… {p} %");
            await updates.DownloadAsync(update, p => ((IProgress<int>)progress).Report(p));
            updates.ApplyAndRestart(update);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "No se pudo buscar o instalar la actualización");
            StatusText = "No se pudo comprobar si hay actualizaciones";
            MessageBox.Show($"No se pudo comprobar o instalar la actualización. Revisa tu conexión a internet e inténtalo de nuevo.\n\n{ex.Message}",
                "Buscar actualizaciones", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}

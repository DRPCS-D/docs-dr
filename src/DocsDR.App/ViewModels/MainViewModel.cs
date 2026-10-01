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

    /// <summary>La pestaña «Páginas» de la cinta está activa: los documentos muestran la cuadrícula de organizar páginas.</summary>
    [ObservableProperty] private bool _isOrganizing;

    public bool HasDocument => SelectedDocument is not null;

    // ---- Tema claro / oscuro ----
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLightTheme))]
    private bool _isDarkTheme = settings.Theme != "Light";

    public bool IsLightTheme => !IsDarkTheme;

    [RelayCommand]
    private void SetDefaultReader()
    {
        if (DefaultReader.InstalledExePath() is not { } exe)
        {
            Msg.Show("Esta opción está disponible en la versión instalada de DOCS-DR (no en la portable ni en compilaciones de prueba).",
                "Lector de PDF predeterminado", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try { DefaultReader.Register(exe); }
        catch (Exception ex) { Serilog.Log.Warning(ex, "No se pudo registrar DOCS-DR para abrir PDF"); }

        var answer = Msg.Show(
            "Windows no permite que una aplicación se ponga como predeterminada por sí sola: lo eliges tú.\n\n" +
            "Se abrirá «Aplicaciones predeterminadas». Elige DOCS-DR en la lista y pulsa «Establecer como predeterminada» " +
            "(o busca «.pdf» y selecciona DOCS-DR).",
            "Lector de PDF predeterminado", MessageBoxButton.OKCancel, MessageBoxImage.Information);
        if (answer == MessageBoxResult.OK) DefaultReader.OpenWindowsSettings();
    }

    [RelayCommand]
    private void SetTheme(string theme)
    {
        settings.SetTheme(theme);
        IsDarkTheme = settings.Theme == "Dark";
        App.ApplyTheme(settings.Theme);
    }

    [RelayCommand]
    private void Open()
    {
        var dlg = new OpenFileDialog { Filter = "Documentos PDF (*.pdf)|*.pdf", Multiselect = true, Title = "Abrir PDF" };
        if (dlg.ShowDialog() == true)
            foreach (var f in dlg.FileNames) OpenPath(f);
    }

    [RelayCommand]
    public void OpenPath(string path) => TryOpen(path, interactive: true);

    /// <summary>Abre un PDF. Con <paramref name="interactive"/> false (reabrir la sesión anterior) no pregunta contraseñas ni avisa de archivos que ya no existen.</summary>
    private DocumentViewModel? TryOpen(string path, bool interactive)
    {
        var existing = Documents.FirstOrDefault(d => string.Equals(d.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) { SelectedDocument = existing; return existing; }
        if (!File.Exists(path))
        {
            if (interactive) Msg.Show($"No se encontró el archivo:\n{path}", "DOCS-DR", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }

        IPdfDocument? doc = null;
        try
        {
            doc = pdfService.Open(path);
        }
        catch (PdfPasswordRequiredException)
        {
            if (!interactive) return null;
            var pwd = PasswordDialog.Ask(Path.GetFileName(path));
            if (pwd is null) return null;
            try { doc = pdfService.Open(path, pwd); }
            catch (PdfPasswordRequiredException)
            {
                Msg.Show("Contraseña incorrecta.", "DOCS-DR", MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "No se pudo abrir {Path}", path);
            if (interactive) Msg.Show($"No se pudo abrir el archivo:\n{ex.Message}", "DOCS-DR", MessageBoxButton.OK, MessageBoxImage.Error);
            return null;
        }

        var vm = new DocumentViewModel(doc);
        Documents.Add(vm);
        SelectedDocument = vm;
        RememberRecent(path);
        StatusText = $"{vm.Title} — {vm.PageCount} página(s)";
        return vm;
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
        var answer = Msg.Show($"¿Guardar los cambios en «{doc.Title}»?", "DOCS-DR",
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
            Msg.Show($"Uno de los archivos está protegido con contraseña y no se puede unir:\n{ex.Message}", "Unir PDFs",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Msg.Show($"No se pudo guardar el archivo:\n{ex.Message}", "Unir PDFs", MessageBoxButton.OK, MessageBoxImage.Warning);
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

    // ================= Imágenes → PDF =================

    [RelayCommand]
    private void ImagesToPdf()
    {
        var files = Dialogs.AskFiles("Crear PDF desde imágenes", "Agrega las imágenes y ordénalas: cada una será una página.",
            "Imágenes (*.jpg;*.jpeg;*.png;*.bmp;*.tif;*.tiff;*.gif)|*.jpg;*.jpeg;*.png;*.bmp;*.tif;*.tiff;*.gif", "Crear…", minCount: 1);
        if (files is null) return;
        var dlg = new SaveFileDialog
        {
            Filter = "Documentos PDF (*.pdf)|*.pdf",
            FileName = Path.GetFileNameWithoutExtension(files[0]) + ".pdf",
            InitialDirectory = Path.GetDirectoryName(files[0]),
            Title = "Guardar PDF",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            pdfService.ImagesToPdf(files, dlg.FileName);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "No se pudo crear el PDF desde imágenes");
            Msg.Show($"No se pudo crear el PDF (¿alguna imagen está dañada o no es compatible?):\n{ex.Message}", "Crear PDF desde imágenes",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        OpenPath(dlg.FileName);
        StatusText = $"PDF creado con {files.Count} imagen(es)";
    }

    // ================= Recordar la vista =================

    [ObservableProperty] private bool _restoreSession = settings.RestoreSession;

    partial void OnRestoreSessionChanged(bool value) => settings.SetRestoreSession(value);

    public WindowPlacement? WindowPlacement => settings.Window;

    /// <summary>Guarda la ventana y los documentos abiertos para reabrirlos en el próximo inicio.</summary>
    public void SaveView(WindowPlacement window, double? sidePanelWidth)
    {
        var session = Documents
            .Where(d => File.Exists(d.FilePath))
            .Select(d => new SessionEntry(d.FilePath, d.CurrentPage - 1, d.Zoom))
            .ToList();
        int selected = SelectedDocument is null ? 0 : Math.Max(0, session.FindIndex(e => e.Path == SelectedDocument.FilePath));
        settings.SaveView(window, session, selected, sidePanelWidth);
    }

    /// <summary>Reabre los documentos de la sesión anterior (si la opción está activa). Los que ya no existen se omiten.</summary>
    public void RestorePreviousSession()
    {
        if (!settings.RestoreSession) return;
        foreach (var entry in settings.Session)
        {
            var vm = TryOpen(entry.Path, interactive: false);
            if (vm is null) continue;
            vm.SetZoom(entry.Zoom);
            vm.PendingPage = entry.Page;
        }
        if (Documents.Count > 0) SelectedDocument = Documents[Math.Clamp(settings.SessionSelected, 0, Documents.Count - 1)];
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

    /// <summary>
    /// Una vez al día (en el primer arranque), busca en segundo plano si hay una versión nueva y lo avisa
    /// solo en la barra de estado, sin ventanas. Si falla (sin internet), calla y se reintenta en el próximo arranque.
    /// </summary>
    public async Task CheckUpdatesQuietlyAsync()
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        if (!updates.IsInstalled || settings.LastUpdateCheck == today) return;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(4)); // que el arranque termine primero
            var update = await updates.CheckAsync();
            settings.SetLastUpdateCheck(today);
            if (update is not null)
                StatusText = $"Hay una versión nueva de DOCS-DR ({UpdateService.VersionOf(update)}). Instálala desde Ayuda › Buscar actualizaciones";
        }
        catch (Exception ex)
        {
            Serilog.Log.Information(ex, "No se pudo comprobar las actualizaciones en segundo plano");
        }
    }

    [RelayCommand]
    private async Task CheckUpdatesAsync()
    {
        if (!updates.IsInstalled)
        {
            // Copia sin instalar (código fuente o carpeta copiada): no puede actualizarse sola.
            if (Msg.Show(
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
                Msg.Show($"Ya tienes la última versión ({AppInfo.Version}).", "Buscar actualizaciones",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var version = UpdateService.VersionOf(update);
            if (Msg.Show(
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
            Msg.Show($"No se pudo comprobar o instalar la actualización. Revisa tu conexión a internet e inténtalo de nuevo.\n\n{ex.Message}",
                "Buscar actualizaciones", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}

using System.Globalization;
using System.IO;
using DocsDR.App.Services;
using DocsDR.App.ViewModels;
using DocsDR.Core;
using DocsDR.Pdf;
using MuPDF.NET;

namespace DocsDR.App.Tests;

/// <summary>Buscar y reemplazar, y recordar la vista (ViewModel), con coma decimal como en es-ES/es-PY.</summary>
public class ToolsViewModelTests : IDisposable
{
    private readonly CultureInfo _previous = CultureInfo.CurrentCulture;

    public ToolsViewModelTests() => CultureInfo.CurrentCulture = new CultureInfo("es-ES");

    public void Dispose() => CultureInfo.CurrentCulture = _previous;

    private static DocumentViewModel Open()
    {
        var path = Path.Combine(Path.GetTempPath(), "docsdr-app-tests", $"{Guid.NewGuid():N}.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var culture = CultureInfo.CurrentCulture;
        lock (MuPdfDocument.NativeLock)
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            try
            {
                var d = new Document();
                var p = d.NewPage(width: 400, height: 400);
                // Un renglón con dos colores: «gato» en rojo.
                p.InsertText(new Point(30, 60), "El ", fontSize: 12, fontName: "helv", color: [0f, 0f, 0f]);
                p.InsertText(new Point(30 + Utils.GetTextLength("El ", "helv", 12), 60), "gato", fontSize: 12, fontName: "hebo", color: [0.8f, 0f, 0f]);
                p.InsertText(new Point(30 + Utils.GetTextLength("El gato", "hebo", 12) + 3, 60), " duerme", fontSize: 12, fontName: "helv", color: [0f, 0f, 0f]);
                p.InsertText(new Point(30, 90), "Total del gato", fontSize: 11.5f, fontName: "helv");
                p.InsertText(new Point(30, 120), "Nada que ver", fontSize: 11.5f, fontName: "helv");
                d.Save(path);
                d.Close();
            }
            finally { CultureInfo.CurrentCulture = culture; }
        }
        return new DocumentViewModel(new MuPdfService().Open(path));
    }

    private static List<TextLineInfo> Lines(DocumentViewModel vm) =>
        ((IPdfEditor)vm.Document).GetTextBlocks(0).SelectMany(b => b.Lines).ToList();

    [Fact]
    public void Replace_all_changes_every_line_keeps_colors_and_undoes_in_one_step()
    {
        var vm = Open();

        var message = vm.ReplaceAll("gato", "perro", matchCase: false);

        Assert.Contains("2 coincidencia", message);
        var lines = Lines(vm);
        Assert.Contains(lines, l => l.Text == "Total del perro");
        var mixed = Assert.Single(lines, l => l.Text.StartsWith("El perro"));
        Assert.Equal("El perro duerme", mixed.Text);
        // «perro» hereda el rojo de «gato»; el resto sigue en negro.
        Assert.Equal(new PdfColor(0, 0, 0), mixed.Runs![0].Format.Color);
        Assert.Equal(204, mixed.Runs[1].Format.Color.R);
        Assert.Equal("perro", mixed.Runs[1].Text);
        Assert.Contains(lines, l => l.Text == "Nada que ver"); // lo demás no se toca

        vm.UndoCommand.Execute(null); // un solo paso deshace todo
        var after = Lines(vm).Select(l => l.Text).ToList();
        Assert.Contains("Total del gato", after);
        Assert.Contains("El gato duerme", after);
    }

    [Fact]
    public void Replace_all_respects_case_sensitivity_and_reports_no_matches_without_touching_history()
    {
        var vm = Open();

        var message = vm.ReplaceAll("GATO", "perro", matchCase: true);

        Assert.Contains("No se encontraron", message);
        Assert.False(vm.IsModified);
        Assert.Contains(Lines(vm), l => l.Text == "Total del gato"); // nada cambió
    }

    [Fact]
    public void Replace_all_skips_lines_where_the_new_text_does_not_fit_and_keeps_them_intact()
    {
        var vm = Open();
        var huge = new string('W', 120);

        var message = vm.ReplaceAll("Nada", huge, matchCase: false);

        Assert.Contains("No se pudo reemplazar", message);
        Assert.Contains(Lines(vm), l => l.Text == "Nada que ver"); // el renglón original sigue igual
        Assert.False(vm.IsModified); // y no quedó ningún paso en el historial
    }

    [Fact]
    public void Settings_remember_the_session_the_window_and_the_restore_option()
    {
        var file = Path.Combine(Path.GetTempPath(), $"docsdr-test-{Guid.NewGuid():N}.json");
        try
        {
            var s = new SettingsService(file);
            Assert.False(s.RestoreSession);
            Assert.Empty(s.Session);
            Assert.Null(s.LastUpdateCheck);

            s.SaveView(new WindowPlacement(100, 50, 1200, 800, true), [new SessionEntry(@"C:.pdf", 4, 1.5), new SessionEntry(@"C:.pdf", 0, 1.0)], 1, null);
            s.SetRestoreSession(true);
            s.SetLastUpdateCheck("2026-10-01");

            var again = new SettingsService(file);
            Assert.True(again.RestoreSession);
            Assert.Equal(2, again.Session.Count);
            Assert.Equal(new SessionEntry(@"C:.pdf", 4, 1.5), again.Session[0]);
            Assert.Equal(1, again.SessionSelected);
            Assert.Equal(new WindowPlacement(100, 50, 1200, 800, true), again.Window);
            Assert.Equal("2026-10-01", again.LastUpdateCheck);
        }
        finally { File.Delete(file); }
    }
}

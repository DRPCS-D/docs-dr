using DocsDR.App.Services;

namespace DocsDR.App.Tests;

public class HelpTests
{
    [Fact]
    public void App_info_has_the_company_data_and_version()
    {
        Assert.Matches(@"^\d+\.\d+\.\d+$", AppInfo.Version);
        Assert.Equal("DRPCS E.A.S.", AppInfo.Company);
        Assert.Equal("Diago Rene Ruiz Diaz Rios", AppInfo.Author);
        Assert.Equal("diagorr@gmail.com", AppInfo.SupportEmail);
        Assert.Equal("https://github.com/DRPCS-D/docs-dr", AppInfo.RepositoryUrl);
        Assert.StartsWith("mailto:diagorr@gmail.com?subject=", AppInfo.SupportMailto);
        Assert.Contains("DRPCS E.A.S.", AppInfo.Copyright);
    }

    [Fact]
    public void Third_party_list_includes_mupdf_with_its_agpl_license()
    {
        var mupdf = Assert.Single(AppInfo.ThirdParty, l => l.Name.StartsWith("MuPDF"));
        Assert.Equal("AGPL-3.0", mupdf.License);
        Assert.All(AppInfo.ThirdParty, l => Assert.False(string.IsNullOrWhiteSpace(l.License)));
    }

    [Fact]
    public void A_copy_that_was_not_installed_reports_it_instead_of_trying_to_update()
    {
        // Ejecutando desde las pruebas no hay instalación de Velopack.
        Assert.False(new UpdateService().IsInstalled);
    }

    [Fact]
    public void Theme_is_dark_by_default_and_the_choice_is_remembered()
    {
        var file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"docsdr-test-{Guid.NewGuid():N}.json");
        try
        {
            var settings = new DocsDR.App.Services.SettingsService(file);
            Assert.Equal("Dark", settings.Theme);

            settings.SetTheme("Light");
            Assert.Equal("Light", new DocsDR.App.Services.SettingsService(file).Theme);
        }
        finally { System.IO.File.Delete(file); }
    }
}

using System.Windows;
using DocsDR.App.Services;

namespace DocsDR.App.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        NameText.Text = AppInfo.ProductName;
        VersionText.Text = $"Versión {AppInfo.Version}";
        DescriptionText.Text = AppInfo.Description;
        CompanyText.Text = AppInfo.Company;
        AuthorText.Text = AppInfo.Author;
        MailText.Text = AppInfo.SupportEmail;
        RepoText.Text = AppInfo.RepositoryUrl;
        CopyrightText.Text = AppInfo.Copyright + " Todos los derechos reservados.";
        Libraries.ItemsSource = AppInfo.ThirdParty;
    }

    private void OnMailClick(object sender, RoutedEventArgs e) => AppInfo.OpenUrl(AppInfo.SupportMailto);

    private void OnRepoClick(object sender, RoutedEventArgs e) => AppInfo.OpenUrl(AppInfo.RepositoryUrl);
}

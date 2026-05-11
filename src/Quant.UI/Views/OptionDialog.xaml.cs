using Quant.Core.Infrastructure;
using Quant.Core.Models;
using System.Windows;

namespace Quant.UI.Views;

public partial class OptionDialog : Window
{
    private readonly DbManager _db = DbManager.Instance;

    public AppOptions Result { get; private set; } = new();

    public OptionDialog()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var opts = _db.LoadOptions();
        ChkAutoAppend.IsChecked = opts.AutoAppendHistory;
        TxtPdfFolder.Text       = opts.ReportPdfFolder;
		DB_Folder.Text = DbManager.DbPath;
		ChkQueryFilterPreferred.IsChecked = opts.QueryFilterPreferred;
		ChkQueryFilterCovered.IsChecked = opts.QueryFilterCovered;
		ChkQueryFilterCheap.IsChecked = opts.QueryFilterCheap;
		ChkExcludeSpac.IsChecked = opts.ExcludeSpac;
		ChkExcludePrefStock.IsChecked = opts.ExcludePrefStock;
		ChkExcludeHalted.IsChecked = opts.ExcludeHalted;
	}

    private void BtnBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title            = "PDF 리포트 폴더 선택",
            InitialDirectory = TxtPdfFolder.Text.Length > 0
                                   ? TxtPdfFolder.Text
                                   : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dlg.ShowDialog() == true)
            TxtPdfFolder.Text = dlg.FolderName;
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        Result = new AppOptions
        {
            AutoAppendHistory = ChkAutoAppend.IsChecked == true,
            ReportPdfFolder   = TxtPdfFolder.Text.Trim(),
            QueryFilterPreferred = ChkQueryFilterPreferred.IsChecked == true,
            QueryFilterCovered   = ChkQueryFilterCovered.IsChecked == true,
            QueryFilterCheap     = ChkQueryFilterCheap.IsChecked == true,
            ExcludeSpac          = ChkExcludeSpac.IsChecked == true,
            ExcludePrefStock     = ChkExcludePrefStock.IsChecked == true,
            ExcludeHalted        = ChkExcludeHalted.IsChecked == true,
		};
        _db.SaveOptions(Result);
        DialogResult = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}

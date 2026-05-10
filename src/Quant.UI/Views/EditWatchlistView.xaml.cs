using Quant.Core.Infrastructure;
using System.Data;
using System.Windows;
using System.Windows.Controls;

namespace Quant.UI.Views;

public partial class EditWatchlistView : UserControl
{
    public event Action<string, string>? StatusChanged;

    private readonly DbManager _db;
    private DataTable? _table;

    public EditWatchlistView(DbManager db)
    {
        _db = db;
        InitializeComponent();
        Loaded += (_, _) => LoadData();
    }

    public void LoadData()
    {
        try
        {
            _table = _db.Query(
                "SELECT watchlist_id, name, description, is_active, created_at, updated_at " +
                "FROM watchlists ORDER BY name");
            Grid.ItemsSource = _table.DefaultView;
            TxtRowCount.Text = $"{_table.Rows.Count:N0} rows";
            StatusChanged?.Invoke($"워치리스트 {_table.Rows.Count}건", "#A6E3A1");
        }
        catch (Exception ex) { StatusChanged?.Invoke($"오류: {ex.Message}", "#F38BA8"); }
    }

    private void BtnNew_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new WatchlistEditDialog(null);
        if (dlg.ShowDialog() == true) LoadData();
    }

    private void BtnEdit_Click(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is not DataRowView row) { TxtStatus.Text = "행을 선택하세요"; return; }
        var dlg = new WatchlistEditDialog(row);
        if (dlg.ShowDialog() == true) LoadData();
    }

    private void BtnDelete_Click(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is not DataRowView row) { TxtStatus.Text = "행을 선택하세요"; return; }
        var name = row["name"]?.ToString() ?? "";
        var id   = row["watchlist_id"]?.ToString() ?? "0";
        if (MessageBox.Show($"워치리스트 [{name}]을 삭제하시겠습니까?\n(items도 함께 삭제됩니다)",
                "삭제 확인", MessageBoxButton.YesNo, MessageBoxImage.Warning)
            != MessageBoxResult.Yes) return;
        try
        {
            _db.Execute($"DELETE FROM watchlist_items WHERE watchlist_id = {id}");
            _db.Execute($"DELETE FROM watchlists WHERE watchlist_id = {id}");
            StatusChanged?.Invoke($"삭제됨: {name}", "#F38BA8");
            LoadData();
        }
        catch (Exception ex) { StatusChanged?.Invoke($"삭제 오류: {ex.Message}", "#F38BA8"); }
    }
}

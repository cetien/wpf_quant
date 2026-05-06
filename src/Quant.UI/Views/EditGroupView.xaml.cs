using Quant.Core.Infrastructure;
using System.Data;
using System.Windows;
using System.Windows.Controls;

namespace Quant.UI.Views;

public partial class EditGroupView : UserControl
{
    public event Action<string, string>? StatusChanged;

    private readonly DbManager _db = DbManager.Instance;
    private DataTable? _table;

    public EditGroupView()
    {
        InitializeComponent();
        Loaded += (_, _) => LoadData();
    }

    public void LoadData()
    {
        try
        {
            _table = _db.Query(
                "SELECT group_id, kind, name, description, rating, is_active, created_at, updated_at " +
                "FROM groups ORDER BY kind, name");
            Grid.ItemsSource = _table.DefaultView;
            TxtRowCount.Text = $"{_table.Rows.Count:N0} rows";
            StatusChanged?.Invoke($"그룹 {_table.Rows.Count}건", "#A6E3A1");
        }
        catch (Exception ex) { StatusChanged?.Invoke($"오류: {ex.Message}", "#F38BA8"); }
    }

    private void BtnNew_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new GroupEditDialog(null);
        if (dlg.ShowDialog() == true) LoadData();
    }

    private void BtnEdit_Click(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is not DataRowView row) { TxtStatus.Text = "행을 선택하세요"; return; }
        var dlg = new GroupEditDialog(row);
        if (dlg.ShowDialog() == true) LoadData();
    }

    private void BtnDelete_Click(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is not DataRowView row) { TxtStatus.Text = "행을 선택하세요"; return; }
        var name = row["name"]?.ToString() ?? "";
        var id   = row["group_id"]?.ToString() ?? "0";
        if (MessageBox.Show($"그룹 [{name}]을 삭제하시겠습니까?",
                "삭제 확인", MessageBoxButton.YesNo, MessageBoxImage.Warning)
            != MessageBoxResult.Yes) return;
        try
        {
            _db.Execute($"DELETE FROM groups WHERE group_id = {id}");
            StatusChanged?.Invoke($"삭제됨: {name}", "#F38BA8");
            LoadData();
        }
        catch (Exception ex) { StatusChanged?.Invoke($"삭제 오류: {ex.Message}", "#F38BA8"); }
    }
}

using Quant.Core.Infrastructure;
using System.Data;
using System.Windows;
using System.Windows.Controls;

namespace Quant.UI.Views;

public partial class GroupEditDialog : Window
{
    private readonly DbManager    _db    = DbManager.Instance;
    private readonly DataRowView? _row;
    private readonly bool         _isEdit;

    /// <param name="row">null → 신규, non-null → 수정</param>
    public GroupEditDialog(DataRowView? row)
    {
        InitializeComponent();
        _row    = row;
        _isEdit = row is not null;
        if (_isEdit) PopulateFields();
    }

    private void PopulateFields()
    {
        var kind = _row!["kind"]?.ToString() ?? "sector";
        foreach (ComboBoxItem item in CmbKind.Items)
            if (item.Content?.ToString() == kind) { CmbKind.SelectedItem = item; break; }
        TxtName.Text   = _row["name"]?.ToString() ?? "";
        TxtDesc.Text   = _row["description"]?.ToString() ?? "";
        TxtRating.Text = _row["rating"]?.ToString() ?? "5";
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        var kind   = (CmbKind.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "sector";
        var name   = TxtName.Text.Trim();
        var desc   = TxtDesc.Text.Trim();
        var rating = int.TryParse(TxtRating.Text, out var r) ? Math.Clamp(r, 1, 10) : 5;
        if (string.IsNullOrEmpty(name)) { MessageBox.Show("Name을 입력하세요"); return; }
        try
        {
            if (_isEdit)
            {
                var id = _row!["group_id"]?.ToString() ?? "0";
                _db.Execute(
                    $"UPDATE groups SET kind='{kind}', name='{Esc(name)}', " +
                    $"description='{Esc(desc)}', rating={rating}, " +
                    $"updated_at=CURRENT_TIMESTAMP WHERE group_id={id}");
            }
            else
            {
                _db.Execute(
                    $"INSERT INTO groups (kind, name, description, rating) " +
                    $"VALUES ('{kind}', '{Esc(name)}', '{Esc(desc)}', {rating})");
            }
            DialogResult = true;
        }
        catch (Exception ex) { MessageBox.Show($"저장 오류: {ex.Message}"); }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private static string Esc(string s) => s.Replace("'", "''");
}

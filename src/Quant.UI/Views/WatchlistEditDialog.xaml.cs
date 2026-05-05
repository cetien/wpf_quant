using DuckDB.NET.Data;
using System.Data;
using System.Windows;

namespace Quant.UI.Views;

public partial class WatchlistEditDialog : Window
{
    private readonly string _dbPath;
    private readonly DataRowView? _row;
    private readonly bool _isEdit;

    public WatchlistEditDialog(string dbPath, DataRowView? row)
    {
        InitializeComponent();
        _dbPath = dbPath;
        _row    = row;
        _isEdit = row is not null;
        if (_isEdit)
        {
            TxtName.Text = _row!["name"]?.ToString() ?? "";
            TxtDesc.Text = _row["description"]?.ToString() ?? "";
        }
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        var name = TxtName.Text.Trim();
        var desc = TxtDesc.Text.Trim();
        if (string.IsNullOrEmpty(name)) { MessageBox.Show("Name을 입력하세요"); return; }
        try
        {
            if (_isEdit)
            {
                var id = _row!["watchlist_id"]?.ToString() ?? "0";
                Execute($"UPDATE watchlists SET name='{Esc(name)}', description='{Esc(desc)}', " +
                        $"updated_at=CURRENT_TIMESTAMP WHERE watchlist_id={id}");
            }
            else
            {
                Execute($"INSERT INTO watchlists (name, description) " +
                        $"VALUES ('{Esc(name)}', '{Esc(desc)}')");
            }
            DialogResult = true;
        }
        catch (Exception ex) { MessageBox.Show($"저장 오류: {ex.Message}"); }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Execute(string sql)
    {
        using var conn = new DuckDBConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static string Esc(string s) => s.Replace("'", "''");
}

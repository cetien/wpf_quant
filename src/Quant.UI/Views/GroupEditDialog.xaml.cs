using DuckDB.NET.Data;
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
        var kind = _row!["kind"]?.ToString() ?? "watch";
        foreach (ComboBoxItem item in CmbKind.Items)
            if (item.Content?.ToString() == kind) { CmbKind.SelectedItem = item; break; }
        TxtName.Text      = _row["name"]?.ToString() ?? "";
        TxtDesc.Text      = _row["description"]?.ToString() ?? "";
        RatingCtrl.Rating = int.TryParse(_row["rating"]?.ToString(), out var r0) ? Math.Clamp(r0, 0, 10) : 5;
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        var kind   = (CmbKind.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "watch";
        var name   = TxtName.Text.Trim();
        var desc   = TxtDesc.Text.Trim();
        var rating = RatingCtrl.Rating;
        if (string.IsNullOrEmpty(name)) { MessageBox.Show("Name을 입력하세요"); return; }
        try
        {
            using var conn = _db.OpenNativeConnection();
            using var cmd  = conn.CreateCommand();

            if (_isEdit)
            {
                var id = long.Parse(_row!["group_id"]?.ToString() ?? "0");
                cmd.CommandText =
                    "UPDATE groups " +
                    "SET kind=$1, name=$2, description=$3, rating=$4, updated_at=CURRENT_TIMESTAMP " +
                    "WHERE group_id=$5";
                cmd.Parameters.Add(new DuckDBParameter { Value = kind   });
                cmd.Parameters.Add(new DuckDBParameter { Value = name   });
                cmd.Parameters.Add(new DuckDBParameter { Value = desc   });
                cmd.Parameters.Add(new DuckDBParameter { Value = rating });
                cmd.Parameters.Add(new DuckDBParameter { Value = id     });
            }
            else
            {
                cmd.CommandText =
                    "INSERT INTO groups (kind, name, description, rating) " +
                    "VALUES ($1, $2, $3, $4)";
                cmd.Parameters.Add(new DuckDBParameter { Value = kind   });
                cmd.Parameters.Add(new DuckDBParameter { Value = name   });
                cmd.Parameters.Add(new DuckDBParameter { Value = desc   });
                cmd.Parameters.Add(new DuckDBParameter { Value = rating });
            }

            cmd.ExecuteNonQuery();
            DialogResult = true;
        }
        catch (Exception ex) { MessageBox.Show($"저장 오류: {ex.Message}"); }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}

using Quant.UI.Common;

using System.Data;
using System.Diagnostics;
using System.Reflection.Emit;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Quant.UI.Common;

// 메뉴 빌더: 마우스 우클릭 시 상황에 맞는 컨텍스트 메뉴를 동적으로 생성
//
// 흐름:
//   MouseRightButtonUp (Grid 등)
//     → MenuBuilder.Build(MenuCategory.Ticker, ExecuteAction)
//     → ContextMenu { IsOpen = true }
//     → 사용자 클릭 → MenuItem.Click
//     → onAction(MenuAction) 콜백
//     → ExecuteAction(MenuAction)  ← 각 View에서 구현
//
// 사용 예:
//   private void GridCompany_RightClick(object sender, MouseButtonEventArgs e)
//   {
//       var menu = MenuBuilder.Build(MenuCategory.Ticker, ExecuteAction);
//       menu.PlacementTarget = (UIElement)sender;
//       menu.IsOpen = true;
//   }
//
//   Category는 OR 조합 가능:
//   MenuBuilder.Build(MenuCategory.Ticker | MenuCategory.Db, ExecuteAction)


//<DataGrid local:PopupMenuProps.TargetColumn="ticker"
//          local:PopupMenuProps.MenuCategory="Ticker,Db"
//          MouseRightButtonUp="OnGridRightClick" ... />
public static class PopupMenuProps
{
    public static readonly DependencyProperty TextColumnProperty =
        DependencyProperty.RegisterAttached("TextColumn", typeof(string), typeof(PopupMenuProps), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty IdColumnProperty =
        DependencyProperty.RegisterAttached("IdColumn", typeof(string), typeof(PopupMenuProps), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty MenuCategoryProperty =
        DependencyProperty.RegisterAttached("MenuCategory", typeof(MenuCategory), typeof(PopupMenuProps), new PropertyMetadata(default(MenuCategory)));

    public static void SetTextColumn(DependencyObject element, string value) => element.SetValue(TextColumnProperty, value);
    public static string GetTextColumn(DependencyObject element) => (string)element.GetValue(TextColumnProperty);

    public static void SetIdColumn(DependencyObject element, string value) => element.SetValue(IdColumnProperty, value);
    public static string GetIdColumn(DependencyObject element) => (string)element.GetValue(IdColumnProperty);

    public static void SetMenuCategory(DependencyObject element, MenuCategory value) => element.SetValue(MenuCategoryProperty, value);
    public static MenuCategory GetMenuCategory(DependencyObject element) => (MenuCategory)element.GetValue(MenuCategoryProperty);
}


[Flags]
public enum MenuCategory
{
    None   = 0,
    Ticker = 1 << 0,   // 종목 관련 메뉴
    Group  = 1 << 1,   // 그룹 관련 메뉴
    Report = 1 << 2,   // 보고서 관련 메뉴
    Db = 1 << 3,   // DB 관련 메뉴
}

public enum MenuAction
{
    DrawChart,
    FindGroup,
    WebInfo,

    NewGroup,
    GroupInfo,
    DeleteGroup,

    OpenReport,
    DeleteReport,

    Db_QueryInfo,
    Db_DeletePrices,
    Db_DeleteSupply,
    Db_DeleteFundamentals,
    Db_RemoveTicker,
}

public static class ContextMenuHelper// ContextMenuBuilder
{
    // HasContext = true 인 항목은 context 문자열이 있으면 ": {context}" 를 헤더에 덧붙임
    private record MenuItemDef(string Header, MenuCategory Category, MenuAction Action,
                               bool HasContext = false);

    private static readonly List<MenuItemDef> _items =
    [
        new("Draw Chart",    MenuCategory.Ticker, MenuAction.DrawChart,    HasContext: true),
        new("Find Group", MenuCategory.Ticker, MenuAction.FindGroup, HasContext: true),
        new("Web Info", MenuCategory.Ticker, MenuAction.WebInfo, HasContext: true),

        new("New Group",     MenuCategory.Group,  MenuAction.NewGroup),
        new("Group Info",  MenuCategory.Group,  MenuAction.GroupInfo,  HasContext: true),
        new("DeleteGroup",  MenuCategory.Group,  MenuAction.DeleteGroup,  HasContext: true),

        new("Open Report",  MenuCategory.Report,  MenuAction.OpenReport),
        new("Delete Report",  MenuCategory.Report,  MenuAction.DeleteReport),

        new("Query Info",     MenuCategory.Db,     MenuAction.Db_QueryInfo,  HasContext: true),
        new("DEL prices",     MenuCategory.Db,     MenuAction.Db_DeletePrices,  HasContext: true),
        new("DEL supply",    MenuCategory.Db,     MenuAction.Db_DeleteSupply,  HasContext: true),
        new("DEL fundamentals", MenuCategory.Db, MenuAction.Db_DeleteFundamentals,  HasContext: true),
        new("Remove ticker",    MenuCategory.Db,     MenuAction.Db_RemoveTicker,  HasContext: true),
    ];

    /// <summary>
    /// 지정한 Category에 해당하는 항목만 포함한 ContextMenu를 생성합니다.
    /// </summary>
    /// <param name="category">표시할 카테고리 (OR 조합 가능)</param>
    /// <param name="onAction">메뉴 클릭 시 호출되는 콜백</param>
    /// <param name="context">
    /// HasContext 항목의 헤더에 덧붙일 문자열 (선택).
    /// 여러 값은 호출부에서 조합: $"{name} ({code})"
    /// </param>
    public static ContextMenu Build(MenuCategory category, string textValue, string idValue, Action<MenuAction, string?> onAction)
    {
        var menu = new ContextMenu();

        MenuItemDef? prev = null;
        foreach (var def in _items)
        {
            if ((def.Category & category) == 0) continue;

            // 카테고리가 바뀌는 경계에 구분선 추가 (첫 항목 제외)
            if (prev is not null && prev.Category != def.Category)
                menu.Items.Add(new Separator());

            //var header = def.Header;
            var header = (def.HasContext && textValue is not null)
                ? $"{def.Header}: {textValue}"
                : def.Header;

            var action = def.Action;        // 클로저 캡처용 지역 변수
            var mi = new MenuItem { Header = header };
            mi.Click += (_, _) => onAction(action, idValue);
            menu.Items.Add(mi);

            prev = def;
        }

        return menu;
    }
//}

//public static class ContextMenuHelper
//{
    //<DataGrid local:PopupMenuProps.TargetColumn="ticker"
    //          local:PopupMenuProps.MenuCategory="Ticker,Db"
    //          MouseRightButtonUp="OnGridRightClick" ... />
    public static void ShowGridMenu(object sender, MouseButtonEventArgs e, Action<MenuAction, string, object> executeAction)
    {
        if (sender is not DataGrid grid) return;

        // 1. Property at XAML 탐색: MenuCategory, TargetColumn
        MenuCategory category = PopupMenuProps.GetMenuCategory(grid);   // Ticker, Group, Report, Db
        string textColumn = PopupMenuProps.GetTextColumn(grid);     // "name"
        string idColumn = PopupMenuProps.GetIdColumn(grid);         // ticker or group_id
        //if (string.IsNullOrEmpty(textColumn) || string.IsNullOrEmpty(idColumn)) return;

        // 2. find Row -> select Row
        var row = Helpers.FindParent<DataGridRow>(e.OriginalSource as DependencyObject);
        if (row == null) return;

        grid.SelectedItem = row.Item;
        if (grid.SelectedItem is not DataRowView rowView) return;

        // 3. Grid[currentRow][targetColumn] value
        string textValue = rowView[textColumn]?.ToString() ?? "";   // "SK Hynix"
        string idValue = rowView[idColumn]?.ToString() ?? "";       // '000660'
        if (string.IsNullOrEmpty(idValue)) return;
        //if (string.IsNullOrEmpty(keyValue) || keyValue.StartsWith("-")) return;

        // 4. 메뉴 생성 및 출력
        var menu =  Build(category, textValue, idValue, (action, ctx) => executeAction(action, ctx ?? "", sender));
        if (menu == null) return;

        menu.PlacementTarget = grid;
        menu.IsOpen = true;
        e.Handled = true;
    }

}


/*
  
PopupMenuBuilder.Create(MyGrid)
    .Add("차트", OpenChart)
    .Add("뉴스", OpenNews)
    .Separator()
    .Add("삭제", DeleteItem)
    .Show();


public class PopupMenuBuilder
{
    private readonly ContextMenu _menu = new();

    private PopupMenuBuilder(UIElement target)
    {
        _menu.PlacementTarget = target;
    }

    public static PopupMenuBuilder Create(UIElement target)
        => new(target);

    public PopupMenuBuilder Add(
        string header,
        Action action)
    {
        var item = new MenuItem
        {
            Header = header
        };

        item.Click += (_, _) => action();

        _menu.Items.Add(item);

        return this;
    }

    public PopupMenuBuilder Separator()
    {
        _menu.Items.Add(new Separator());
        return this;
    }

    public void Show()
    {
        _menu.IsOpen = true;
    }
}

 */

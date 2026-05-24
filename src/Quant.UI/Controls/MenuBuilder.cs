using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows.Controls;

namespace Quant.UI;

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


[Flags]
public enum MenuCategory
{
    None   = 0,
    Ticker = 1 << 0,   // 종목 관련 메뉴
    Group  = 1 << 1,   // 그룹 관련 메뉴
    Db     = 1 << 2,   // DB 관련 메뉴
}

public enum MenuAction
{
    DrawChart,
    FindGroup,

    NewGroup,
    GroupInfo,
    DeleteGroup,

    Db_QueryInfo,
    Db_DeletePrices,
    Db_DeleteSupply,
    Db_DeleteFundamentals,
    Db_RemoveTicker,
}

public static class MenuBuilder
{
    // HasContext = true 인 항목은 context 문자열이 있으면 ": {context}" 를 헤더에 덧붙임
    private record MenuItemDef(string Header, MenuCategory Category, MenuAction Action,
                               bool HasContext = false);

    private static readonly List<MenuItemDef> _items =
    [
        new("Draw Chart",    MenuCategory.Ticker, MenuAction.DrawChart,    HasContext: true),
        new("Find Group", MenuCategory.Ticker, MenuAction.FindGroup, HasContext: true),

        new("New Group",     MenuCategory.Group,  MenuAction.NewGroup),
        new("Group Info",  MenuCategory.Group,  MenuAction.GroupInfo,  HasContext: true),
        new("DeleteGroup",  MenuCategory.Group,  MenuAction.DeleteGroup,  HasContext: true),

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
    public static ContextMenu Build(MenuCategory category, Action<MenuAction> onAction,
                                    string? context = null)
    {
        var menu = new ContextMenu();

        MenuItemDef? prev = null;
        foreach (var def in _items)
        {
            if ((def.Category & category) == 0) continue;

            // 카테고리가 바뀌는 경계에 구분선 추가 (첫 항목 제외)
            if (prev is not null && prev.Category != def.Category)
                menu.Items.Add(new Separator());

            var header = (def.HasContext && context is not null)
                ? $"{def.Header}: {context}"
                : def.Header;

            var action = def.Action;        // 클로저 캡처용 지역 변수
            var mi = new MenuItem { Header = header };
            mi.Click += (_, _) => onAction(action);
            menu.Items.Add(mi);

            prev = def;
        }

        return menu;
    }
}

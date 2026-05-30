using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

/*
    <Border Grid.Column="1" Margin="8,0,0,0">
        <ScrollViewer VerticalScrollBarVisibility="Auto">
            <StackPanel x:Name="Panel_ReturnRatio" Margin="12"/>
        </ScrollViewer>
    </Border>
or
    <Border Grid.Column="1" Margin="8,0,0,0"
            BorderBrush="#404040" BorderThickness="1" CornerRadius="8">
        <ScrollViewer VerticalScrollBarVisibility="Auto">
            <WrapPanel x:Name="Panel_ReturnRatio" Margin="12" />    // for 카드 레이아웃
        </ScrollViewer>
    </Border>

var ui = new InfoPanelBuilder(MyPanel);

ui.AddHeader("삼성전자");
ui.AddMetric("PER", "12.3");
ui.AddMetric("ROE", "18%");
ui.AddSeparator();

ui.AddText("""
AI 분석 결과:
상대강도와 거래량이 동시에 증가 중.
""");

TODO:
    AddSubHeader(...)
    AddBadge(...)
    AddProgressBar(...)
    AddChart(...)
    AddTable(...)
    AddMarkdown(...)
    AddExpander(...)
    AddImage(...)
    AddTag(...)
    AddSignal(...)

InfoPanelBuilder
 ├── AddMetric()
 ├── AddSignal()
 ├── AddStockSummary()
 ├── AddAIAnalysis()
 └── AddFactorScores()

*/

public class InfoPanelBuilder
{
    private readonly Panel _panel;
    public SolidColorBrush DefaultColor { get; set; }

    public enum InfoTone
    {
        Normal, Good, Warning, Danger, Muted
    }

    public enum HeaderLevel
    {
        H1, H2, H3
    }

    private static SolidColorBrush GetToneColor(InfoTone tone) => tone switch
    {
        InfoTone.Good => Brushes.LimeGreen,
        InfoTone.Warning => Brushes.Gold,
        InfoTone.Danger => Brushes.Red,
        InfoTone.Muted => Brushes.Gray,
        _ => Brushes.White
    };


    public InfoPanelBuilder(Panel panel)
    {
        _panel = panel;
        DefaultColor = Brushes.LightGray;
    }

    public InfoPanelBuilder Clear()
    {
        _panel.Children.Clear();
        //scrollViewer.ScrollToTop();
        return this;
    }

    // 대량 추가 시 깜빡임 감소.
    public InfoPanelBuilder BeginUpdate()
    {
        _panel.Visibility = Visibility.Collapsed;
        return this;
    }

    public InfoPanelBuilder EndUpdate()
    {
        _panel.Visibility = Visibility.Visible;
        return this;
    }

    /*
        ui.ReplaceContent(x =>
        {
            x.AddHeader("삼성전자");
            x.AddMetric("PER", "12.3");
        });
    */
    public InfoPanelBuilder ReplaceContent(Action<InfoPanelBuilder> build)
    {
        Clear();
        build(this);
        return this;
    }

    /*
        // 가로의 경우: <StackPanel Orientation="Horizontal"/>

        ui.AddCard(card =>
        {
            card.AddHeader("SK하이닉스", HeaderLevel.H3);
            card.AddMetric("RS", "98");
            card.AddMetric("PER", "12.3");
        });
    */
    public InfoPanelBuilder AddCard(int width = 0, int height = 0)
    {
        var innerPanel = new StackPanel();

        var border = new Border
        {
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(6),
            Margin = new Thickness(6),            
            Child = innerPanel
        };

        if (width > 0)
            border.Width = width;

        if (height > 0)
            border.Height = height;

        _panel.Children.Add(border);

        return new InfoPanelBuilder(innerPanel);
    }

    public InfoPanelBuilder AddCard(Action<InfoPanelBuilder> build)
    {
        var card = AddCard();
        build(card);
        return this;
    }

    // 구분선
    public InfoPanelBuilder Separator()
    {
        _panel.Children.Add(new Border
        {
            Height = 1,
            Background = Brushes.Gray,
            Margin = new Thickness(0, 5, 0, 5)
        });
        return this;
    }

    // 제목 --------------------------------------------------------------------------
    // ui.Header("text", 14);   // fontSize
    // ui.Header("text", 14, Brushes.Red);   // fontSize, color

    // ui.Header("text");  
    // ui.Header("text", Brushes.Red);  // color
    // ui.Header("text", Brushes.Red, HeaderLevel.H1);  // color, level

    // ui.Header1("text");      ui.Header1("text", InfoTone.Normal);  
    // ui.Header2("text");      ui.Header2("text", InfoTone.Normal);    
    // ui.Header3("text");      ui.Header3("text", InfoTone.Normal);    

    public InfoPanelBuilder Header(string text, int fontSize, SolidColorBrush? color = null,
        FontWeight weight = default, Thickness margin = default)
    {
        _panel.Children.Add(new TextBlock
        {
            Style = Application.Current.TryFindResource("BoldText") as Style,
            Text = text,
            FontSize = fontSize,
            FontWeight = weight == default ? FontWeights.Bold : weight,
            Margin = margin == default ? new Thickness(0, 12, 0, 8) : margin,
            Foreground = color ?? DefaultColor
        });
        return this;
    }

    public InfoPanelBuilder Header(string text, SolidColorBrush? color = null, HeaderLevel level = HeaderLevel.H1) => level switch
    {
        HeaderLevel.H1 => Header(text, 26, color, FontWeights.Bold, new Thickness(0, 20, 0, 12)),
        HeaderLevel.H2 => Header(text, 20, color, FontWeights.SemiBold, new Thickness(0, 16, 0, 8)),
        _ => Header(text, 16, color, FontWeights.Medium, new Thickness(0, 12, 0, 6))
    };

    public InfoPanelBuilder Header1(string text, InfoTone tone = InfoTone.Normal) => Header(text, GetToneColor(tone), HeaderLevel.H1);
    public InfoPanelBuilder Header2(string text, InfoTone tone = InfoTone.Normal) => Header(text, GetToneColor(tone), HeaderLevel.H2);
    public InfoPanelBuilder Header3(string text, InfoTone tone = InfoTone.Normal) => Header(text, GetToneColor(tone), HeaderLevel.H3);

    // TEXT --------------------------------------------------------------------------
    // ui.Text("text");  
    // ui.Text("text", Brushes.Red);  // color
    // ui.Text("text", InfoTone.Normal);  
    public InfoPanelBuilder Text(string text, SolidColorBrush? color = null)
    {
        _panel.Children.Add(new TextBlock
        {
            Style = Application.Current.TryFindResource("NormalText") as Style,
            Text = text,
            Foreground = color ?? DefaultColor,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0)
        });
        return this;
    }

    public InfoPanelBuilder Text(string text, InfoTone tone) => Text(text, GetToneColor(tone));
    
    // metric 한 줄
    public InfoPanelBuilder Metric(string label, string value, int labelWidth = 40, SolidColorBrush? color = null)
    {
        var grid = new Grid
        {
            Margin = new Thickness(0, 2, 0, 0)
        };

        grid.ColumnDefinitions.Add(
            new ColumnDefinition
            {
                Width = new GridLength(labelWidth)
            });

        grid.ColumnDefinitions.Add(
            new ColumnDefinition
            {
                Width = GridLength.Auto
            });

        var tb1 = new TextBlock
        {
            Style = Application.Current.TryFindResource("NormalText") as Style,
            Text = label,
            Foreground = DefaultColor,
            FontWeight = FontWeights.SemiBold
        };

        var tb2 = new TextBlock
        {
            Style = Application.Current.TryFindResource("NormalText") as Style,
            Text = value,
            Foreground = color ?? DefaultColor
        };

        Grid.SetColumn(tb2, 1);

        grid.Children.Add(tb1);
        grid.Children.Add(tb2);

        _panel.Children.Add(grid);
        return this;
    }

    public InfoPanelBuilder MetricColoredDigit(
        string label,
        double value,
        int labelWidth = 40,
        string? format = "+0.0;-0.0;0",
        string? suffix = "%")
    {
        var tone = value switch
        {
            > 0 => InfoTone.Good,
            < 0 => InfoTone.Danger,
            _ => InfoTone.Normal
        };

        var text = string.IsNullOrEmpty(format) ? value.ToString() : value.ToString(format);
        if (!string.IsNullOrEmpty(suffix))
        {
            text += suffix;
        }

        Metric(label, text, labelWidth, GetToneColor(tone));
        return this;
    }

}

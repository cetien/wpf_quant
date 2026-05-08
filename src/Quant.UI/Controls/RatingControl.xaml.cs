using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Quant.UI.Controls;

// ──────────────────────────────────────────────────────────
//  StarItem  (ItemsControl 에 바인딩되는 단일 별 데이터)
// ──────────────────────────────────────────────────────────
public class StarItem : INotifyPropertyChanged
{
    public int Index { get; init; }   // 1-based

    private bool _filled;
    public bool Filled
    {
        get => _filled;
        set
        {
            if (_filled == value) return;
            _filled = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Filled)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Glyph)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FillBrush)));
        }
    }

    public string Glyph => Filled ? "★" : "☆";
	//public Brush  Color  => Filled
	//    ? new SolidColorBrush(Color.FromRgb(0xF9, 0xE2, 0xAF))  // Catppuccin Yellow
	//    : new SolidColorBrush(Color.FromRgb(0x45, 0x47, 0x5A));  // Muted grey
	
    private static readonly Brush FilledBrush =
		new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF9, 0xE2, 0xAF));

	private static readonly Brush EmptyBrush =
		new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x45, 0x47, 0x5A));

	public Brush FillBrush => Filled ? FilledBrush : EmptyBrush;

	public event PropertyChangedEventHandler? PropertyChanged;
}

// ──────────────────────────────────────────────────────────
//  RatingControl
// ──────────────────────────────────────────────────────────
public partial class RatingControl : UserControl
{
    // ── DependencyProperty ────────────────────────────────
    public static readonly DependencyProperty RatingProperty =
        DependencyProperty.Register(
            nameof(Rating), typeof(int), typeof(RatingControl),
            new FrameworkPropertyMetadata(0,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnRatingChanged));

    public static readonly DependencyProperty MaxRatingProperty =
        DependencyProperty.Register(
            nameof(MaxRating), typeof(int), typeof(RatingControl),
            new PropertyMetadata(10, OnMaxRatingChanged));

    // ── 이벤트 ───────────────────────────────────────────
    public event Action<int>? RatingChanged;

    // ── 프로퍼티 ─────────────────────────────────────────
    public int Rating
    {
        get => (int)GetValue(RatingProperty);
        set => SetValue(RatingProperty, Math.Clamp(value, 0, MaxRating));
    }

    public int MaxRating
    {
        get => (int)GetValue(MaxRatingProperty);
        set => SetValue(MaxRatingProperty, Math.Max(1, value));
    }

    public ObservableCollection<StarItem> Stars { get; } = [];

    // ── 생성자 ───────────────────────────────────────────
    public RatingControl()
    {
        InitializeComponent();
        BuildStars();
    }

    // ── 내부 메서드 ──────────────────────────────────────
    private void BuildStars()
    {
        Stars.Clear();
        for (int i = 1; i <= MaxRating; i++)
            Stars.Add(new StarItem { Index = i, Filled = i <= Rating });
    }

    private void RefreshStars()
    {
        // MaxRating 변경 없이 Rating만 바뀐 경우 Filled만 갱신
        if (Stars.Count != MaxRating) { BuildStars(); return; }
        foreach (var s in Stars)
            s.Filled = s.Index <= Rating;
    }

    // ── DependencyProperty 콜백 ──────────────────────────
    private static void OnRatingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is RatingControl ctrl)
            ctrl.RefreshStars();
    }

    private static void OnMaxRatingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is RatingControl ctrl)
            ctrl.BuildStars();
    }

    // ── 클릭 핸들러 ──────────────────────────────────────
    private void Star_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (fe.Tag is not int idx) return;

        // 동일 별 재클릭 → 0으로 초기화
        Rating = (idx == Rating) ? 0 : idx;
        RatingChanged?.Invoke(Rating);
    }
}

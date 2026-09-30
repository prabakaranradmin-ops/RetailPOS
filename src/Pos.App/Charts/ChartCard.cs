using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Pos.App.Charts;

/// <summary>
/// The card a chart sits on: its title, a line saying what it covers, and three buttons - read it as
/// a table, save it as a picture, and show everything again after zooming in.
/// </summary>
/// <remarks>
/// <para>
/// The table is the same figures the chart draws, one row per bar or slice. It is there for the owner
/// who wants the numbers rather than the shape, and for anybody the chart does not work for: a
/// table can be read aloud, copied, and read at any size.
/// </para>
/// <para>
/// The picture is saved where the owner chooses, through the ordinary save box. It is drawn at twice
/// the screen's size so it stays sharp pasted into a message to a partner or an accountant.
/// </para>
/// <para>
/// The look lives in Theme.xaml with the rest of the theme; this is only what the buttons do.
/// </para>
/// </remarks>
[TemplatePart(Name = "PART_Save", Type = typeof(ButtonBase))]
[TemplatePart(Name = "PART_Table", Type = typeof(ButtonBase))]
[TemplatePart(Name = "PART_Reset", Type = typeof(ButtonBase))]
[TemplatePart(Name = "PART_Grid", Type = typeof(DataGrid))]
public sealed class ChartCard : ContentControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(ChartCard), new PropertyMetadata(string.Empty, (d, _) => ((ChartCard)d).NameChart()));

    public static readonly DependencyProperty SubtitleProperty = DependencyProperty.Register(
        nameof(Subtitle), typeof(string), typeof(ChartCard), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty CaptionProperty = DependencyProperty.Register(
        nameof(Caption), typeof(string), typeof(ChartCard), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(string), typeof(ChartCard), new PropertyMetadata(Glyphs.Chart));

    public static readonly DependencyProperty IsShowingTableProperty = DependencyProperty.Register(
        nameof(IsShowingTable), typeof(bool), typeof(ChartCard), new PropertyMetadata(false, (d, _) => ((ChartCard)d).FillTable()));

    public static readonly DependencyProperty IsZoomedProperty = DependencyProperty.Register(
        nameof(IsZoomed), typeof(bool), typeof(ChartCard), new PropertyMetadata(false));

    public static readonly DependencyProperty NoticeProperty = DependencyProperty.Register(
        nameof(Notice), typeof(string), typeof(ChartCard), new PropertyMetadata(string.Empty));

    private readonly DispatcherTimer _noticeTimer;

    private ButtonBase? _save;
    private ButtonBase? _table;
    private ButtonBase? _reset;
    private DataGrid? _grid;
    private ChartSurface? _chart;

    public ChartCard()
    {
        _noticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _noticeTimer.Tick += (_, _) =>
        {
            _noticeTimer.Stop();
            Notice = string.Empty;
        };
    }

    /// <summary>What the chart shows, in a few words: "Takings, day by day".</summary>
    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>A line under the title: what the period is, or how to read it.</summary>
    public string Subtitle
    {
        get => (string)GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    /// <summary>A note under the chart, for what the shape means. Hidden when empty.</summary>
    public string Caption
    {
        get => (string)GetValue(CaptionProperty);
        set => SetValue(CaptionProperty, value);
    }

    /// <summary>The icon in the card's corner, from <see cref="Glyphs"/>.</summary>
    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    /// <summary>True while the figures are shown as a table instead of drawn.</summary>
    public bool IsShowingTable
    {
        get => (bool)GetValue(IsShowingTableProperty);
        set => SetValue(IsShowingTableProperty, value);
    }

    /// <summary>True while the chart shows only part of its figures.</summary>
    public bool IsZoomed
    {
        get => (bool)GetValue(IsZoomedProperty);
        private set => SetValue(IsZoomedProperty, value);
    }

    /// <summary>What the last button press did - "Saved to …" - for a few seconds.</summary>
    public string Notice
    {
        get => (string)GetValue(NoticeProperty);
        private set => SetValue(NoticeProperty, value);
    }

    /// <summary>The chart on the card.</summary>
    public ChartSurface? Chart => _chart;

    public override void OnApplyTemplate()
    {
        if (_save is not null)
            _save.Click -= Save_Click;
        if (_table is not null)
            _table.Click -= Table_Click;
        if (_reset is not null)
            _reset.Click -= Reset_Click;
        if (_grid is not null)
            _grid.AutoGeneratingColumn -= Grid_AutoGeneratingColumn;

        base.OnApplyTemplate();

        _save = GetTemplateChild("PART_Save") as ButtonBase;
        _table = GetTemplateChild("PART_Table") as ButtonBase;
        _reset = GetTemplateChild("PART_Reset") as ButtonBase;
        _grid = GetTemplateChild("PART_Grid") as DataGrid;

        if (_save is not null)
            _save.Click += Save_Click;
        if (_table is not null)
            _table.Click += Table_Click;
        if (_reset is not null)
            _reset.Click += Reset_Click;
        if (_grid is not null)
            _grid.AutoGeneratingColumn += Grid_AutoGeneratingColumn;

        FillTable();
    }

    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);

        if (_chart is not null)
        {
            _chart.ZoomChanged -= Chart_ZoomChanged;
            _chart.FiguresChanged -= Chart_FiguresChanged;
        }

        _chart = newContent as ChartSurface;

        if (_chart is not null)
        {
            _chart.ZoomChanged += Chart_ZoomChanged;
            _chart.FiguresChanged += Chart_FiguresChanged;
            IsZoomed = _chart.IsZoomed;
            NameChart();
        }
    }

    /// <summary>The chart takes the card's title, for its saved picture and for a screen reader.</summary>
    private void NameChart()
    {
        if (_chart is not null && string.IsNullOrWhiteSpace(_chart.Title))
            _chart.Title = Title;
    }

    private void Chart_ZoomChanged(object? sender, EventArgs e) => IsZoomed = _chart?.IsZoomed ?? false;

    private void Chart_FiguresChanged(object? sender, EventArgs e) => FillTable();

    private void Table_Click(object sender, RoutedEventArgs e) => IsShowingTable = !IsShowingTable;

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _chart?.ResetZoom();
        _chart?.Focus();
    }

    private void FillTable()
    {
        if (_grid is null)
            return;

        _grid.ItemsSource = IsShowingTable && _chart is not null ? _chart.ToTable().DefaultView : null;
    }

    /// <summary>
    /// Each column bound by name through the row's indexer, so a heading such as "7-day average" -
    /// which the ordinary property path would read as something else - still finds its figures.
    /// Figures are set to the right so they line up; the first column is what they are for.
    /// </summary>
    private void Grid_AutoGeneratingColumn(object? sender, DataGridAutoGeneratingColumnEventArgs e)
    {
        var first = _grid?.Columns.Count == 0;
        var style = new Style(typeof(TextBlock));
        style.Setters.Add(new Setter(TextBlock.HorizontalAlignmentProperty, first ? HorizontalAlignment.Left : HorizontalAlignment.Right));
        style.Setters.Add(new Setter(TextBlock.MarginProperty, new Thickness(4, 2, 8, 2)));

        e.Column = new DataGridTextColumn
        {
            Header = e.PropertyName,
            Binding = new Binding($"[{e.PropertyName}]"),
            ElementStyle = style,
        };
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_chart is null)
            return;

        var dialog = new SaveFileDialog
        {
            Title = "Save the chart as a picture",
            Filter = "PNG picture (*.png)|*.png",
            DefaultExt = ".png",
            AddExtension = true,
            FileName = $"{SafeName(string.IsNullOrWhiteSpace(Title) ? "Chart" : Title)} {DateTime.Today:yyyy-MM-dd}.png",
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;

        try
        {
            _chart.SavePng(dialog.FileName);
            Say($"Saved to {Path.GetFileName(dialog.FileName)}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Say($"Could not save it: {ex.Message}");
        }
    }

    private void Say(string notice)
    {
        Notice = notice;
        _noticeTimer.Stop();
        _noticeTimer.Start();
    }

    private static string SafeName(string title)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(title.Select(c => invalid.Contains(c) ? '-' : c).ToArray()).Trim();
    }
}

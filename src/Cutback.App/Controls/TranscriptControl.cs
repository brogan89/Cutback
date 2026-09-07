using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Utilities;
using Cutback.Core;
using Cutback.Core.Models;
using Cutback.Core.Transcript;

namespace Cutback.App.Controls;

/// <summary>
/// The transcript as flowing text. Cut words are struck through and dimmed; the word under the
/// playhead is highlighted.
/// </summary>
/// <remarks>
/// Interactions:
/// <list type="bullet">
/// <item>Click a word: cut it, or restore it if it is cut.</item>
/// <item>Drag across words: cut or restore the whole run, decided by the word the drag started on.</item>
/// <item>Cmd/Ctrl+click a word: play from it. Right-click: context menu with Play from here and Cut / Restore.</item>
/// </list>
/// One <see cref="TextLayout"/> holds the whole transcript, with a style override per cut word,
/// and hit-testing goes through the layout, so there is one visual for thousands of words.
/// </remarks>
public sealed class TranscriptControl : Control
{
    public static readonly StyledProperty<IReadOnlyList<Word>?> WordsProperty =
        AvaloniaProperty.Register<TranscriptControl, IReadOnlyList<Word>?>(nameof(Words));

    public static readonly StyledProperty<SegmentList?> SegmentsProperty =
        AvaloniaProperty.Register<TranscriptControl, SegmentList?>(nameof(Segments));

    public static readonly StyledProperty<int> CurrentWordIndexProperty =
        AvaloniaProperty.Register<TranscriptControl, int>(nameof(CurrentWordIndex), -1);

    public static readonly StyledProperty<ICommand?> CutWordsCommandProperty =
        AvaloniaProperty.Register<TranscriptControl, ICommand?>(nameof(CutWordsCommand));

    public static readonly StyledProperty<ICommand?> SeekToWordCommandProperty =
        AvaloniaProperty.Register<TranscriptControl, ICommand?>(nameof(SeekToWordCommand));

    private const double FontSize = 14;
    private const double LineHeight = 24;
    private const double DragThreshold = 4;

    private static readonly Typeface Font = new(FontFamily.Default);
    private static readonly IBrush KeptBrush = new SolidColorBrush(Color.Parse("#E4E4EA"));
    private static readonly IBrush CutBrush = new SolidColorBrush(Color.Parse("#6A6A75"));
    private static readonly IBrush CurrentBrush = new SolidColorBrush(Color.Parse("#3A5A8C"));
    private static readonly IBrush SelectionBrush = new SolidColorBrush(Color.Parse("#4F7BD1"), 0.55);

    private string _text = string.Empty;
    private int[] _wordStarts = [];
    private int[] _wordLengths = [];
    private bool[] _cutStates = [];
    private TextLayout? _layout;
    private double _layoutWidth = -1;
    private SegmentList? _subscribed;

    private int _pressWord = -1;
    private int _selectionAnchor = -1;
    private int _selectionEnd = -1;
    private bool _dragging;
    private Point _pressPoint;
    private int _contextWord = -1;
    private readonly MenuItem _playItem;
    private readonly MenuItem _cutItem;

    static TranscriptControl()
    {
        AffectsRender<TranscriptControl>(CurrentWordIndexProperty);
        FocusableProperty.OverrideDefaultValue<TranscriptControl>(true);
    }

    public TranscriptControl()
    {
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Hand);

        // Built in code so the target word can be resolved from the pointer position when the
        // menu is requested, like TimelineControl's menu.
        _playItem = new MenuItem { Header = "Play from here" };
        _playItem.Click += (_, _) => Seek(_contextWord);
        _cutItem = new MenuItem { Header = "Cut" };
        _cutItem.Click += (_, _) => CutWords(_contextWord, _contextWord, _contextWord);
        ContextFlyout = new MenuFlyout { Items = { _playItem, _cutItem } };
        AddHandler(ContextRequestedEvent, OnContextRequested, RoutingStrategies.Tunnel);
    }

    public IReadOnlyList<Word>? Words
    {
        get => GetValue(WordsProperty);
        set => SetValue(WordsProperty, value);
    }

    public SegmentList? Segments
    {
        get => GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public int CurrentWordIndex
    {
        get => GetValue(CurrentWordIndexProperty);
        set => SetValue(CurrentWordIndexProperty, value);
    }

    public ICommand? CutWordsCommand
    {
        get => GetValue(CutWordsCommandProperty);
        set => SetValue(CutWordsCommandProperty, value);
    }

    public ICommand? SeekToWordCommand
    {
        get => GetValue(SeekToWordCommandProperty);
        set => SetValue(SeekToWordCommandProperty, value);
    }

    // ---- property changes ---------------------------------------------------------------------

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == WordsProperty)
        {
            RebuildText();
        }
        else if (change.Property == SegmentsProperty)
        {
            if (_subscribed is not null)
            {
                _subscribed.Changed -= OnSegmentsChanged;
            }

            _subscribed = change.GetNewValue<SegmentList?>();
            if (_subscribed is not null)
            {
                _subscribed.Changed += OnSegmentsChanged;
            }

            RefreshCutStates();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_subscribed is not null)
        {
            _subscribed.Changed -= OnSegmentsChanged;
            _subscribed = null;
        }
    }

    private void OnSegmentsChanged(object? sender, EventArgs e) => RefreshCutStates();

    private void RebuildText()
    {
        var words = Words ?? [];
        _wordStarts = new int[words.Count];
        _wordLengths = new int[words.Count];
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < words.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(' ');
            }

            _wordStarts[i] = sb.Length;
            _wordLengths[i] = words[i].Text.Length;
            sb.Append(words[i].Text);
        }

        _text = sb.ToString();
        _cutStates = new bool[words.Count];
        _selectionAnchor = _selectionEnd = _pressWord = -1;
        _layout = null;
        RefreshCutStates();
        InvalidateMeasure();
    }

    /// <summary>Recomputes which words are cut; only rebuilds the layout when a word's state actually changed.</summary>
    private void RefreshCutStates()
    {
        var words = Words ?? [];
        var states = Segments is { } segments ? TranscriptView.CutStates(words, segments.Segments) : new bool[words.Count];
        if (states.AsSpan().SequenceEqual(_cutStates))
        {
            return;
        }

        _cutStates = states;
        _layout = null;
        InvalidateVisual();
    }

    // ---- layout -------------------------------------------------------------------------------

    private TextLayout EnsureLayout(double width)
    {
        var maxWidth = double.IsFinite(width) && width > 0 ? width : double.PositiveInfinity;
        if (_layout is not null && _layoutWidth == maxWidth)
        {
            return _layout;
        }

        var cutProperties = new GenericTextRunProperties(
            Font,
            fontRenderingEmSize: FontSize,
            textDecorations: TextDecorations.Strikethrough,
            foregroundBrush: CutBrush);
        var overrides = new List<ValueSpan<TextRunProperties>>();
        for (var i = 0; i < _wordStarts.Length; i++)
        {
            if (_cutStates[i])
            {
                overrides.Add(new ValueSpan<TextRunProperties>(_wordStarts[i], _wordLengths[i], cutProperties));
            }
        }

        _layout = new TextLayout(
            _text,
            Font,
            FontSize,
            KeptBrush,
            textWrapping: TextWrapping.Wrap,
            maxWidth: maxWidth,
            lineHeight: LineHeight,
            textStyleOverrides: overrides);
        _layoutWidth = maxWidth;
        return _layout;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (_wordStarts.Length == 0)
        {
            return new Size(0, 0);
        }

        var layout = EnsureLayout(availableSize.Width);
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : layout.Width;
        return new Size(width, layout.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        EnsureLayout(finalSize.Width);
        return finalSize;
    }

    // ---- rendering ----------------------------------------------------------------------------

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (_wordStarts.Length == 0)
        {
            return;
        }

        var layout = EnsureLayout(Bounds.Width);

        if (_selectionAnchor >= 0 && _selectionEnd >= 0)
        {
            foreach (var rect in WordRects(layout, Math.Min(_selectionAnchor, _selectionEnd), Math.Max(_selectionAnchor, _selectionEnd)))
            {
                context.FillRectangle(SelectionBrush, rect);
            }
        }

        var current = CurrentWordIndex;
        if (current >= 0 && current < _wordStarts.Length)
        {
            foreach (var rect in WordRects(layout, current, current))
            {
                context.FillRectangle(CurrentBrush, rect);
            }
        }

        layout.Draw(context, new Point(0, 0));
    }

    private IEnumerable<Rect> WordRects(TextLayout layout, int first, int last)
    {
        var start = _wordStarts[first];
        var length = _wordStarts[last] + _wordLengths[last] - start;
        return layout.HitTestTextRange(start, length).Select(r => r.Inflate(new Thickness(2, 1)));
    }

    // ---- hit testing --------------------------------------------------------------------------

    /// <summary>Word at a point, or -1 with no words. Whitespace resolves to the word before it.</summary>
    private int WordAt(Point p)
    {
        if (_wordStarts.Length == 0)
        {
            return -1;
        }

        var position = EnsureLayout(Bounds.Width).HitTestPoint(p).TextPosition;
        int lo = 0, hi = _wordStarts.Length - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) >> 1;
            if (_wordStarts[mid] <= position)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return lo;
    }

    // ---- pointer ------------------------------------------------------------------------------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        var word = WordAt(point.Position);
        if (word < 0)
        {
            return;
        }

        Focus();
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta))
        {
            Seek(word);
            e.Handled = true;
            return;
        }

        _pressWord = word;
        _pressPoint = point.Position;
        _dragging = false;
        _selectionAnchor = _selectionEnd = word;
        e.Pointer.Capture(this);
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_pressWord < 0)
        {
            return;
        }

        var p = e.GetPosition(this);
        if (!_dragging)
        {
            if (Math.Abs(p.X - _pressPoint.X) < DragThreshold && Math.Abs(p.Y - _pressPoint.Y) < DragThreshold)
            {
                return;
            }

            _dragging = true;
        }

        var word = WordAt(p);
        if (word >= 0 && word != _selectionEnd)
        {
            _selectionEnd = word;
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_pressWord < 0)
        {
            return;
        }

        var first = Math.Min(_selectionAnchor, _selectionEnd);
        var last = Math.Max(_selectionAnchor, _selectionEnd);
        var anchor = _pressWord;

        _pressWord = _selectionAnchor = _selectionEnd = -1;
        _dragging = false;

        // Capture(null) raises PointerCaptureLost synchronously, and its handler resets the
        // selection fields above, so state must be read and cleared before releasing capture.
        e.Pointer.Capture(null);
        e.Handled = true;

        CutWords(first, last, anchor);
        InvalidateVisual();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_pressWord < 0)
        {
            // Re-entrant call from OnPointerReleased's own Capture(null): no gesture is active.
            return;
        }

        _pressWord = _selectionAnchor = _selectionEnd = -1;
        _dragging = false;
        InvalidateVisual();
    }

    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        _contextWord = e.TryGetPosition(this, out var p) ? WordAt(p) : -1;
        if (_contextWord < 0)
        {
            e.Handled = true;
            return;
        }

        _cutItem.Header = _cutStates[_contextWord] ? "Restore" : "Cut";
    }

    // ---- commands -----------------------------------------------------------------------------

    private void CutWords(int first, int last, int anchor)
    {
        if (first < 0 || last < 0 || anchor < 0)
        {
            return;
        }

        var range = new WordRange(first, last, anchor);
        if (CutWordsCommand?.CanExecute(range) == true)
        {
            CutWordsCommand.Execute(range);
        }
    }

    private void Seek(int word)
    {
        if (word >= 0 && SeekToWordCommand?.CanExecute(word) == true)
        {
            SeekToWordCommand.Execute(word);
        }
    }
}

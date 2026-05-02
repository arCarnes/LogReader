namespace LogReader.App.Controls;

using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using LogReader.Core.Models;

public sealed class SearchResultTextBlock : TextBlock
{
    public static readonly DependencyProperty LineTextProperty = DependencyProperty.Register(
        nameof(LineText),
        typeof(string),
        typeof(SearchResultTextBlock),
        new PropertyMetadata(string.Empty, OnRenderingPropertyChanged));

    public static readonly DependencyProperty MatchesProperty = DependencyProperty.Register(
        nameof(Matches),
        typeof(IEnumerable),
        typeof(SearchResultTextBlock),
        new PropertyMetadata(null, OnRenderingPropertyChanged));

    public static readonly DependencyProperty MatchHighlightBrushProperty = DependencyProperty.Register(
        nameof(MatchHighlightBrush),
        typeof(Brush),
        typeof(SearchResultTextBlock),
        new PropertyMetadata(null, OnRenderingPropertyChanged));

    public static readonly DependencyProperty IsMatchHighlightingEnabledProperty = DependencyProperty.Register(
        nameof(IsMatchHighlightingEnabled),
        typeof(bool),
        typeof(SearchResultTextBlock),
        new PropertyMetadata(true, OnRenderingPropertyChanged));

    public string LineText
    {
        get => (string)GetValue(LineTextProperty);
        set => SetValue(LineTextProperty, value);
    }

    public IEnumerable? Matches
    {
        get => (IEnumerable?)GetValue(MatchesProperty);
        set => SetValue(MatchesProperty, value);
    }

    public Brush? MatchHighlightBrush
    {
        get => (Brush?)GetValue(MatchHighlightBrushProperty);
        set => SetValue(MatchHighlightBrushProperty, value);
    }

    public bool IsMatchHighlightingEnabled
    {
        get => (bool)GetValue(IsMatchHighlightingEnabledProperty);
        set => SetValue(IsMatchHighlightingEnabledProperty, value);
    }

    private static void OnRenderingPropertyChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        ((SearchResultTextBlock)dependencyObject).RebuildInlines();
    }

    private void RebuildInlines()
    {
        var lineText = LineText ?? string.Empty;
        Inlines.Clear();

        var spans = IsMatchHighlightingEnabled
            ? NormalizeSpans(Matches, lineText.Length)
            : new List<NormalizedSpan>();
        if (spans.Count == 0 || MatchHighlightBrush == null)
        {
            Inlines.Add(new Run(lineText));
            return;
        }

        var cursor = 0;
        foreach (var span in spans)
        {
            if (span.Start > cursor)
                Inlines.Add(new Run(lineText.Substring(cursor, span.Start - cursor)));

            Inlines.Add(new Run(lineText.Substring(span.Start, span.Length))
            {
                Background = MatchHighlightBrush
            });
            cursor = span.Start + span.Length;
        }

        if (cursor < lineText.Length)
            Inlines.Add(new Run(lineText.Substring(cursor)));
    }

    private static List<NormalizedSpan> NormalizeSpans(IEnumerable? matches, int lineLength)
    {
        if (matches == null || lineLength <= 0)
            return [];

        var spans = new List<NormalizedSpan>();
        foreach (var item in matches)
        {
            if (item is not SearchMatchSpan match)
                continue;

            var start = Math.Clamp(match.MatchStart, 0, lineLength);
            var end = Math.Clamp(match.MatchStart + match.MatchLength, 0, lineLength);
            if (end <= start)
                continue;

            spans.Add(new NormalizedSpan(start, end - start));
        }

        if (spans.Count <= 1)
            return spans;

        spans.Sort(static (left, right) =>
        {
            var startComparison = left.Start.CompareTo(right.Start);
            return startComparison != 0
                ? startComparison
                : left.Length.CompareTo(right.Length);
        });

        var merged = new List<NormalizedSpan> { spans[0] };
        foreach (var span in spans.Skip(1))
        {
            var previous = merged[^1];
            var previousEnd = previous.Start + previous.Length;
            if (span.Start <= previousEnd)
            {
                merged[^1] = new NormalizedSpan(previous.Start, Math.Max(previousEnd, span.Start + span.Length) - previous.Start);
                continue;
            }

            merged.Add(span);
        }

        return merged;
    }

    private readonly record struct NormalizedSpan(int Start, int Length);
}

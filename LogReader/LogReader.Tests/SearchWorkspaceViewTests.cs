using System.Windows;
using System.Windows.Controls;
using LogReader.App.ViewModels;
using LogReader.App.Views;
using LogReader.Core.Models;

namespace LogReader.Tests;

public class SearchWorkspaceViewTests
{
    [Fact]
    public void GetSelectedHitLineTexts_ReturnsSelectedHitsInDisplayOrder()
    {
        RunSta(() =>
        {
            var first = CreateHit(10, "ten");
            var second = CreateHit(20, "twenty");
            var third = CreateHit(30, "thirty");
            var listBox = CreateSearchHitsListBox(first, second, third);

            listBox.SelectedItems.Add(third);
            listBox.SelectedItems.Add(first);

            var lines = SearchWorkspaceView.GetSelectedHitLineTexts(listBox);

            Assert.Equal(new[] { "ten", "thirty" }, lines);
        });
    }

    [Fact]
    public void TryPrepareSelectionForContextMenu_SelectsOnlyClickedUnselectedHit()
    {
        RunSta(() =>
        {
            var first = CreateHit(10, "ten");
            var second = CreateHit(20, "twenty");
            var third = CreateHit(30, "thirty");
            var listBox = CreateSearchHitsListBox(first, second, third);
            listBox.SelectedItems.Add(first);
            listBox.SelectedItems.Add(third);

            var changed = SearchWorkspaceView.TryPrepareSelectionForContextMenu(
                listBox,
                second);

            Assert.True(changed);
            Assert.Single(listBox.SelectedItems);
            Assert.Same(second, listBox.SelectedItem);
        });
    }

    [Fact]
    public void TryPrepareSelectionForContextMenu_PreservesExistingMultiSelectionForSelectedHit()
    {
        RunSta(() =>
        {
            var first = CreateHit(10, "ten");
            var second = CreateHit(20, "twenty");
            var third = CreateHit(30, "thirty");
            var listBox = CreateSearchHitsListBox(first, second, third);
            listBox.SelectedItems.Add(first);
            listBox.SelectedItems.Add(third);

            var changed = SearchWorkspaceView.TryPrepareSelectionForContextMenu(
                listBox,
                third);

            Assert.False(changed);
            Assert.Equal(2, listBox.SelectedItems.Count);
            Assert.Contains(first, listBox.SelectedItems.Cast<SearchHitViewModel>());
            Assert.Contains(third, listBox.SelectedItems.Cast<SearchHitViewModel>());
        });
    }

    [Fact]
    public void GetNavigableSelectedHit_ReturnsSelectedItem()
    {
        RunSta(() =>
        {
            var first = CreateHit(10, "ten");
            var second = CreateHit(20, "twenty");
            var listBox = CreateSearchHitsListBox(first, second);
            listBox.SelectedItem = second;

            var hit = SearchWorkspaceView.GetNavigableSelectedHit(listBox);

            Assert.Same(second, hit);
        });
    }

    [Fact]
    public void TryCopySelectedHits_ReturnsFalseWhenNothingIsSelected()
    {
        RunSta(() =>
        {
            var listBox = CreateSearchHitsListBox(CreateHit(10, "ten"));

            var copied = SearchWorkspaceView.TryCopySelectedHits(listBox);

            Assert.False(copied);
        });
    }

    private static SearchHitViewModel CreateHit(long lineNumber, string lineText)
    {
        return new SearchHitViewModel(new SearchHit
        {
            LineNumber = lineNumber,
            LineText = lineText,
            MatchStart = 0,
            MatchLength = lineText.Length
        });
    }

    private static ListBox CreateSearchHitsListBox(params SearchHitViewModel[] hits)
    {
        var listBox = new ListBox
        {
            ItemsSource = hits,
            SelectionMode = SelectionMode.Extended,
            Width = 400,
            Height = 200
        };

        listBox.ApplyTemplate();
        listBox.Measure(new Size(400, 200));
        listBox.Arrange(new Rect(0, 0, 400, 200));
        listBox.UpdateLayout();

        return listBox;
    }

    private static void RunSta(Action action)
    {
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                exception = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (exception != null)
            throw exception;
    }
}

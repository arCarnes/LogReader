using System.Collections.Specialized;
using System.ComponentModel;
using LogReader.App.ViewModels;

namespace LogReader.Tests;

public class BulkObservableCollectionTests
{
    [Fact]
    public void ReplaceAll_ReplacesItemsAndRaisesSingleReset()
    {
        var collection = new BulkObservableCollection<int> { 1, 2, 3 };
        var collectionChanges = new List<NotifyCollectionChangedEventArgs>();
        var propertyChanges = new List<string?>();

        collection.CollectionChanged += (_, e) => collectionChanges.Add(e);
        ((INotifyPropertyChanged)collection).PropertyChanged += (_, e) => propertyChanges.Add(e.PropertyName);

        collection.ReplaceAll(new[] { 4, 5 });

        Assert.Equal(new[] { 4, 5 }, collection.ToArray());
        Assert.Single(collectionChanges);
        Assert.Equal(NotifyCollectionChangedAction.Reset, collectionChanges[0].Action);
        Assert.Contains("Count", propertyChanges);
        Assert.Contains("Item[]", propertyChanges);
    }
}

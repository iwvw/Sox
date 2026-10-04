using Microsoft.UI.Xaml.Controls;
using Sox.App.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Sox.App.Services;

/// <summary>
/// Shared drag-out support for the result lists (spotlight and the file-dialog panel): a result row can
/// be dragged straight to Explorer or another drop target as a real file/folder.
/// </summary>
internal static class ResultDragDrop
{
    /// <summary>
    /// Fills <paramref name="e"/>'s data package with the file-system items behind the dragged rows.
    /// </summary>
    /// <remarks>
    /// The package advertises the StorageItems format through a deferred provider rather than a pre-built
    /// list: <see cref="DragItemsStartingEventArgs"/> cannot await, and StorageFile/StorageFolder are
    /// resolved from a path asynchronously. The provider runs only if the target actually asks for files,
    /// and its own request carries a deferral, so the async resolution finishes before the drop is
    /// consumed. Instant rows (applications, URLs, windows, commands) have no file path and are skipped;
    /// when nothing droppable remains the drag is cancelled rather than started empty.
    /// </remarks>
    public static void OnDragItemsStarting(DragItemsStartingEventArgs e)
    {
        var items = e.Items.OfType<ResultItem>().Where(item => !item.IsInstant && !string.IsNullOrEmpty(item.Path)).ToList();
        if (items.Count == 0)
        {
            e.Cancel = true;
            return;
        }

        e.Data.RequestedOperation = DataPackageOperation.Copy;
        e.Data.SetDataProvider(StandardDataFormats.StorageItems, request => _ = ProvideStorageItemsAsync(request, items));
    }

    private static async Task ProvideStorageItemsAsync(DataProviderRequest request, IReadOnlyList<ResultItem> items)
    {
        var deferral = request.GetDeferral();
        try
        {
            var storageItems = new List<IStorageItem>();
            foreach (var item in items)
            {
                try
                {
                    storageItems.Add(item.IsDir
                        ? await StorageFolder.GetFolderFromPathAsync(item.Path)
                        : await StorageFile.GetFileFromPathAsync(item.Path));
                }
                catch
                {
                    // A stale index row (deleted/moved since indexing, or a path the shell cannot resolve)
                    // is skipped; the rest of the selection still drags.
                }
            }

            request.SetData(storageItems);
        }
        finally
        {
            deferral.Complete();
        }
    }
}

using System.IO.Pipes;
using Sox.Core.Indexer.Usn;

using Sox.Core.Wire;
namespace Sox.Core.Services.Search;

public static class SearchStatusStream
{
    public static async Task SubscribeAsync(Action<UsnIndexer.IndexerStatus> onStatus, CancellationToken token)
    {
        using var pipe = new NamedPipeClientStream(".", "SoxPipe", PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(2000, token).ConfigureAwait(false);
        await SearchRequestBinarySerializer.WriteSearchRequestAsync(pipe, new SearchRequestMessage
        {
            Id = SearchRequestId.SubscribeStatus
        }, token).ConfigureAwait(false);

        while (!token.IsCancellationRequested && pipe.IsConnected)
        {
            var response = await PipeResponseBinarySerializer.ReadAsync(pipe, token).ConfigureAwait(false);
            if (response.Kind != PipeResponseKind.Status || response.Status == null)
                break;

            onStatus(response.Status);
        }
    }
}

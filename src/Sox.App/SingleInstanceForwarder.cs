using System.IO.Pipes;
using System.Text;

namespace Sox.App;

internal static class SingleInstanceForwarder
{
    private const string PipeName = "Sox.App.Activation";

    public static void ForwardActivation(string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(1000);
            var payload = args.Length > 0 ? string.Join('\u001f', args) : string.Empty;
            var bytes = Encoding.UTF8.GetBytes(payload);
            client.Write(bytes, 0, bytes.Length);
        }
        catch
        {
            // A second instance that cannot reach the first simply exits.
        }
    }
}

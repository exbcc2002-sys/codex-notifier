using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace CodexNotifier.Core;
public sealed record WireMessage(string Command, TurnEvent? Event = null);
public static class Ipc
{
    public static string PipeName => "CodexNotifier-" + JsonStore.Hash(Environment.UserDomainName + "\\" + Environment.UserName + ":" + System.Diagnostics.Process.GetCurrentProcess().SessionId);
    public static async Task<bool> Send(WireMessage message, int timeout = 600, string? pipeName = null)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName ?? PipeName, PipeDirection.Out, PipeOptions.Asynchronous);
            using var cancel = new CancellationTokenSource(timeout);
            await pipe.ConnectAsync(cancel.Token);
            byte[] payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message) + "\n");
            await pipe.WriteAsync(payload, cancel.Token); await pipe.FlushAsync(cancel.Token); return true;
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or UnauthorizedAccessException or TimeoutException) { return false; }
    }
    public static async Task Listen(Action<WireMessage> receive, CancellationToken token, string? pipeName = null)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(pipeName ?? PipeName, PipeDirection.In, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(token);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(1000);
                var data = new List<byte>(); byte[] one = new byte[1];
                while (data.Count < 8192 && await pipe.ReadAsync(one, deadline.Token) > 0)
                { if (one[0] == 10) break; data.Add(one[0]); }
                if (data.Count >= 8192) continue;
                var msg = JsonSerializer.Deserialize<WireMessage>(Encoding.UTF8.GetString(data.ToArray()), JsonStore.Options);
                if (msg != null) receive(msg);
            }
            catch (OperationCanceledException) { if (token.IsCancellationRequested) return; }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        }
    }
}

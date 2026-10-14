using System.Diagnostics;
using System.IO.Pipes;
using System.Text;

namespace Inkwell.Services;

/// <summary>
/// One Inkwell process per signed-in user runs the windows: when Inkwell is started again (a double-clicked
/// document, the Start menu), the new process hands its command line to the running one over a named pipe
/// and exits. The running one opens a window for it, or brings forward the window that already has that file.
/// Exports and tests run in their own process.
/// </summary>
internal static class InstanceManager
{
    private static Mutex? s_mutex;
    private static CancellationTokenSource? s_cts;

    private static string Name
    {
        get
        {
            var session = 0;
            try
            {
                using var me = Process.GetCurrentProcess();
                session = me.SessionId;
            }
            catch
            {
            }
            return "Inkwell-app-" + Environment.UserName + "-" + session;
        }
    }

    /// <summary>
    /// Returns true when this process should run the UI; false when the command was handed to the running
    /// Inkwell (this process should exit).
    /// </summary>
    public static bool TryBecomePrimary(IReadOnlyList<string> args)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var mutex = new Mutex(false, @"Local\" + Name);
            bool owned;
            try { owned = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { owned = true; }

            if (owned)
            {
                s_mutex = mutex;
                return true;
            }
            mutex.Dispose();
            if (Send(args, 2500)) return false;
            Thread.Sleep(250); // the primary may be starting or closing
        }
        return true; // couldn't reach it: run on our own
    }

    /// <summary>Starts listening for command lines from later launches (handled on the UI thread by <paramref name="onCommand"/>).</summary>
    public static void Listen(Action<string[]> onCommand)
    {
        if (s_mutex == null) return;
        s_cts = new CancellationTokenSource();
        var token = s_cts.Token;
        var pipeName = Name;
        var thread = new Thread(() =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    server.WaitForConnectionAsync(token).GetAwaiter().GetResult();
                    var buffer = new MemoryStream();
                    var chunk = new byte[4096];
                    int read;
                    // The client sends its arguments then a zero byte.
                    while ((read = server.Read(chunk, 0, chunk.Length)) > 0)
                    {
                        var end = Array.IndexOf(chunk, (byte)0, 0, read);
                        buffer.Write(chunk, 0, end >= 0 ? end : read);
                        if (end >= 0) break;
                    }
                    var args = Encoding.UTF8.GetString(buffer.ToArray()).Split('\n').Where(a => a.Length > 0).ToArray();
                    server.WriteByte(1);
                    server.Flush();
                    onCommand(args);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Instance pipe");
                    Thread.Sleep(200);
                }
            }
        })
        {
            IsBackground = true,
            Name = "Inkwell instance pipe",
        };
        thread.Start();
    }

    public static void Stop()
    {
        s_cts?.Cancel();
        if (s_mutex == null) return;
        try { s_mutex.ReleaseMutex(); } catch { /* not owned */ }
        s_mutex.Dispose();
        s_mutex = null;
    }

    private static bool Send(IReadOnlyList<string> args, int timeoutMs)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", Name, PipeDirection.InOut);
            client.Connect(timeoutMs);
            var bytes = Encoding.UTF8.GetBytes(string.Join("\n", args));
            client.Write(bytes, 0, bytes.Length);
            client.WriteByte(0);
            client.Flush();
            return client.ReadByte() == 1;
        }
        catch
        {
            return false;
        }
    }
}

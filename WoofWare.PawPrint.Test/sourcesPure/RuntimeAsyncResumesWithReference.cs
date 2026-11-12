using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

// A runtime-async method returning a reference suspending at an await of an `IValueTaskSource`
// and resuming once a connection arrives. On .NET 11, `TcpListener.AcceptTcpClientAsync`
// returns the task-returning thunk of its local function `WaitAndWrap` (`ValueTask<TcpClient>`),
// which awaits `AcceptSocketAsync`'s `ValueTask<Socket>` (backed by the socket's
// `SocketAsyncEventArgs`) outside any `try` region, and returns a `TcpClient` wrapping the
// accepted socket. Nothing has connected when the guest asks, so the method suspends; the
// guest then connects.
//
// .NET 10 compiles the same method as a state machine, with the same results.
//
// The exit code is the index of the first check that failed; 0 means all passed.
public class Program
{
    public static int Main (string[] args)
    {
        var listener = new TcpListener (IPAddress.Loopback, 0);
        listener.Start (1);
        var endpoint = (IPEndPoint) listener.LocalEndpoint;

        Task<TcpClient> accepting = listener.AcceptTcpClientAsync ();
        if (accepting.IsCompleted) return 1;

        using var client = new TcpClient (AddressFamily.InterNetwork);
        client.Connect (endpoint);

        using TcpClient accepted = accepting.Result;
        if (accepted == null) return 2;
        if (!accepted.Connected) return 3;

        var clientLocal = (IPEndPoint) client.Client.LocalEndPoint;
        var acceptedRemote = (IPEndPoint) accepted.Client.RemoteEndPoint;
        if (clientLocal.Port != acceptedRemote.Port) return 4;

        listener.Stop ();
        return 0;
    }
}

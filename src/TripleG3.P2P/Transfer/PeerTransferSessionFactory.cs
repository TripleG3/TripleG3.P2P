using System.Net;
using System.Net.Sockets;

namespace TripleG3.P2P.Transfer;

/// <summary>Creates outbound and accepted inbound reusable TCP peer transfer sessions.</summary>
public sealed class PeerTransferSessionFactory : IPeerTransferSessionFactory
{
    public async Task<IPeerTransferSession> ConnectAsync(
        IPEndPoint remoteEndPoint,
        PeerTransferSessionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(remoteEndPoint);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        TcpClient client = new(remoteEndPoint.AddressFamily)
        {
            NoDelay = true
        };
        try
        {
            await client.ConnectAsync(remoteEndPoint.Address, remoteEndPoint.Port, cancellationToken).ConfigureAwait(false);
            return await PeerTransferSession.CreateAsync(client, options, initiator: true, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public async Task<IPeerTransferSession> AcceptAsync(
        TcpClient client,
        PeerTransferSessionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        try
        {
            return await PeerTransferSession.CreateAsync(client, options, initiator: false, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}
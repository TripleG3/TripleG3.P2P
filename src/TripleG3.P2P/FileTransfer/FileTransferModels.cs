using System.Net;

namespace TripleG3.P2P.FileTransfer;

public sealed record FileTransferRequest(
    Guid TransferId,
    IPEndPoint Peer,
    string FileName,
    long Length,
    string Sha256);

public sealed record FileTransferDecision(bool Accepted, string? DestinationPath = null, string? Reason = null)
{
    public static FileTransferDecision Accept(string destinationPath) => new(true, destinationPath);

    public static FileTransferDecision Reject(string reason) => new(false, null, reason);
}

public sealed record FileTransferProgress(
    Guid TransferId,
    string FileName,
    long BytesTransferred,
    long TotalBytes);

public sealed record FileTransferResult(
    Guid TransferId,
    IPEndPoint Peer,
    string FileName,
    long BytesTransferred,
    string Sha256,
    bool Succeeded,
    string? FailureReason = null);

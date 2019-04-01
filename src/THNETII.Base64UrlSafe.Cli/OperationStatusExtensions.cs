using System;
using System.Buffers;

namespace THNETII.Base64UrlSafe.Cli
{
    public static class OperationStatusExtensions
    {
        internal static void ThrowIfFailed(this OperationStatus status, string operation)
        {
            switch (status)
            {
                case OperationStatus.Done:
                    return;
                default:
                case OperationStatus.DestinationTooSmall:
                    throw new InvalidOperationException($"{operation} -> {status}");
                case OperationStatus.InvalidData:
                case OperationStatus.NeedMoreData:
                    throw new FormatException($"{operation} -> {status}");
            }
        }
    }
}

using System;
using System.Buffers;

namespace THNETII.Base64UrlSafe.Cli
{
    public static class OperationStatusExtensions
    {
        internal static void ThrowIfFailed(this OperationStatus status, string operation, bool isFinalBlock)
        {
            switch (status)
            {
                case OperationStatus.Done:
                    return;
                case OperationStatus.NeedMoreData:
                    if (isFinalBlock)
                        goto case OperationStatus.InvalidData;
                    return;
                default:
                case OperationStatus.DestinationTooSmall:
                    throw new InvalidOperationException($"{operation} -> {status}");
                case OperationStatus.InvalidData:
                    throw new FormatException($"{operation} -> {status}");
            }
        }
    }
}

using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace WebPass.Web.Application.Networking;

public sealed class Ipv4Cidr
{
    private readonly uint _network;
    private readonly uint _broadcast;

    private Ipv4Cidr(uint network, int prefixLength)
    {
        _network = network;
        PrefixLength = prefixLength;
        var mask = prefixLength == 0 ? 0U : uint.MaxValue << (32 - prefixLength);
        _broadcast = network | ~mask;
        NetworkAddress = ToAddress(network);
        BroadcastAddress = ToAddress(_broadcast);
    }

    public IPAddress NetworkAddress { get; }
    public IPAddress BroadcastAddress { get; }
    public int PrefixLength { get; }

    public static Ipv4Cidr Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !StringComparer.Ordinal.Equals(value, value.Trim()))
        {
            throw new ArgumentException("CIDR must be a non-empty canonical IPv4 value.", nameof(value));
        }

        var components = value.Split('/', StringSplitOptions.None);
        if (components.Length != 2 || string.IsNullOrEmpty(components[0]) || string.IsNullOrEmpty(components[1]) ||
            !IPAddress.TryParse(components[0], out var address) || address.AddressFamily != AddressFamily.InterNetwork ||
            !StringComparer.Ordinal.Equals(components[0], address.ToString()) ||
            !int.TryParse(components[1], NumberStyles.None, CultureInfo.InvariantCulture, out var prefixLength) ||
            prefixLength is < 0 or > 32 || !StringComparer.Ordinal.Equals(components[1], prefixLength.ToString(CultureInfo.InvariantCulture)))
        {
            throw new ArgumentException("CIDR must be canonical IPv4 address and prefix length.", nameof(value));
        }

        var addressNumber = ToUInt32(address);
        var mask = prefixLength == 0 ? 0U : uint.MaxValue << (32 - prefixLength);
        return new Ipv4Cidr(addressNumber & mask, prefixLength);
    }

    public bool Contains(IPAddress address) => address.AddressFamily == AddressFamily.InterNetwork && IsInRange(ToUInt32(address));

    public bool ContainsUsable(IPAddress address)
    {
        var addressNumber = address.AddressFamily == AddressFamily.InterNetwork ? ToUInt32(address) : 0;
        return GetUsableAddressCount() > 0 && IsInRange(addressNumber) && addressNumber != _network && addressNumber != _broadcast;
    }

    public long GetUsableAddressCount()
    {
        var total = ((long)_broadcast - _network) + 1;
        return total <= 2 ? 0 : total - 2;
    }

    public IReadOnlyList<IPAddress> EnumerateUsableAddresses(int skip, int take)
        => EnumerateUsableAddresses((long)skip, take).ToArray();

    public IEnumerable<IPAddress> EnumerateUsableAddresses(long skip, int take)
    {
        if (skip < 0) throw new ArgumentOutOfRangeException(nameof(skip));
        if (take < 0) throw new ArgumentOutOfRangeException(nameof(take));
        var usable = GetUsableAddressCount();
        if (skip >= usable) yield break;
        var start = (long)_network + 1 + skip;
        var endExclusive = Math.Min((long)_network + 1 + usable, start + take);
        for (var address = start; address < endExclusive; address++) yield return ToAddress((uint)address);
    }

    public bool Overlaps(Ipv4Cidr other) => _network <= other._broadcast && other._network <= _broadcast;
    public override string ToString() => $"{NetworkAddress}/{PrefixLength}";
    private bool IsInRange(uint address) => _network <= address && address <= _broadcast;
    private static uint ToUInt32(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }
    private static IPAddress ToAddress(uint address) => new([(byte)(address >> 24), (byte)(address >> 16), (byte)(address >> 8), (byte)address]);
}

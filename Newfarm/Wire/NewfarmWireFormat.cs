using System;
using System.Text;

namespace Newfarm.Wire;

/// <summary>
/// Reads and writes the newfarm wire format, which is a leading <see cref="NewfarmPacketType"/> byte followed by a
/// fixed field order per type.
/// </summary>
/// <remarks>
/// Every field is written in a fixed order with no framing, because every message is a single datagram whose length
/// the socket already reports. Multi-byte integers are big-endian so a capture is readable left to right.
/// Identities travel as the sixteen raw bytes of a <see cref="Guid"/> rather than as text, which keeps a heartbeat
/// down to thirty three bytes.
/// </remarks>
public static class NewfarmWireFormat
{
    /// <summary>
    /// The size in bytes of the leading <see cref="NewfarmPacketType"/> field.
    /// </summary>
    public const int TypeSize = 1;

    /// <summary>
    /// The size in bytes of a <see cref="Guid"/> on the wire, used for both session ids and session secrets.
    /// </summary>
    public const int IdentitySize = 16;

    /// <summary>
    /// The size in bytes of an epoch field.
    /// </summary>
    public const int EpochSize = 4;

    /// <summary>
    /// The largest datagram newfarm will read or write, which bounds the receive buffer and the credential a peer
    /// may publish.
    /// </summary>
    public const int MaximumDatagramSize = 1024;

    /// <summary>
    /// Writes a type byte alone, for the messages that carry no fields.
    /// </summary>
    /// <param name="destination">The buffer to write into.</param>
    /// <param name="newfarmPacketType">The type to write.</param>
    /// <returns>The number of bytes written.</returns>
    public static int WriteType(Span<byte> destination, NewfarmPacketType newfarmPacketType)
    {
        destination[0] = (byte)newfarmPacketType;

        return TypeSize;
    }

    /// <summary>
    /// Writes a type byte followed by a session id.
    /// </summary>
    /// <param name="destination">The buffer to write into.</param>
    /// <param name="newfarmPacketType">The type to write.</param>
    /// <param name="sessionId">The session the message concerns.</param>
    /// <returns>The number of bytes written.</returns>
    public static int WriteSession(Span<byte> destination, NewfarmPacketType newfarmPacketType, Guid sessionId)
    {
        int offset = WriteType(destination, newfarmPacketType);

        offset += WriteIdentity(destination.Slice(offset), sessionId);

        return offset;
    }

    /// <summary>
    /// Writes a type byte followed by a session id and an epoch.
    /// </summary>
    /// <param name="destination">The buffer to write into.</param>
    /// <param name="newfarmPacketType">The type to write.</param>
    /// <param name="sessionId">The session the message concerns.</param>
    /// <param name="epoch">The epoch the message concerns.</param>
    /// <returns>The number of bytes written.</returns>
    public static int WriteSessionEpoch(Span<byte> destination, NewfarmPacketType newfarmPacketType, Guid sessionId, uint epoch)
    {
        int offset = WriteSession(destination, newfarmPacketType, sessionId);

        offset += WriteUInt32(destination.Slice(offset), epoch);

        return offset;
    }

    /// <summary>
    /// Writes a type byte followed by a session id, its secret, and an epoch, which is the shape of every message a
    /// peer sends to prove it holds the session.
    /// </summary>
    /// <param name="destination">The buffer to write into.</param>
    /// <param name="newfarmPacketType">The type to write.</param>
    /// <param name="sessionId">The session the message concerns.</param>
    /// <param name="sessionSecret">The secret proving the sender was told about the session.</param>
    /// <param name="epoch">The epoch the message concerns.</param>
    /// <returns>The number of bytes written.</returns>
    public static int WriteAuthenticated(Span<byte> destination, NewfarmPacketType newfarmPacketType, Guid sessionId, Guid sessionSecret, uint epoch)
    {
        int offset = WriteSession(destination, newfarmPacketType, sessionId);

        offset += WriteIdentity(destination.Slice(offset), sessionSecret);

        offset += WriteUInt32(destination.Slice(offset), epoch);

        return offset;
    }

    /// <summary>
    /// Writes the credential a session currently lives at, following an already written header.
    /// </summary>
    /// <param name="destination">The buffer to write into, positioned past the header.</param>
    /// <param name="adapterTag">Names the service the credential belongs to, so a peer can tell whether it can use it.</param>
    /// <param name="credential">The opaque bytes a peer needs to reach the room. Newfarm never interprets these.</param>
    /// <returns>The number of bytes written.</returns>
    public static int WriteCredential(Span<byte> destination, string adapterTag, ReadOnlySpan<byte> credential)
    {
        int adapterTagByteCount = Encoding.UTF8.GetByteCount(adapterTag);

        destination[0] = checked((byte)adapterTagByteCount);

        int offset = 1;

        Encoding.UTF8.GetBytes(adapterTag.AsSpan(), destination.Slice(offset, adapterTagByteCount));

        offset += adapterTagByteCount;

        offset += WriteUInt16(destination.Slice(offset), checked((ushort)credential.Length));

        credential.CopyTo(destination.Slice(offset, credential.Length));

        return offset + credential.Length;
    }

    /// <summary>
    /// Reads the type byte of a datagram.
    /// </summary>
    /// <param name="source">The received datagram.</param>
    /// <param name="newfarmPacketType">When this returns <see langword="true"/>, the type that was read.</param>
    /// <returns><see langword="true"/> when the datagram was long enough to carry a type.</returns>
    public static bool TryReadType(ReadOnlySpan<byte> source, out NewfarmPacketType newfarmPacketType)
    {
        if (source.Length < TypeSize)
        {
            newfarmPacketType = default;

            return false;
        }

        newfarmPacketType = (NewfarmPacketType)source[0];

        return true;
    }

    /// <summary>
    /// Reads a session id written directly after the type byte.
    /// </summary>
    /// <param name="source">The received datagram.</param>
    /// <param name="sessionId">When this returns <see langword="true"/>, the session id that was read.</param>
    /// <returns><see langword="true"/> when the datagram was long enough to carry a session id.</returns>
    public static bool TryReadSession(ReadOnlySpan<byte> source, out Guid sessionId)
    {
        if (source.Length < TypeSize + IdentitySize)
        {
            sessionId = default;

            return false;
        }

        sessionId = ReadIdentity(source.Slice(TypeSize));

        return true;
    }

    /// <summary>
    /// Reads a session id and epoch written directly after the type byte.
    /// </summary>
    /// <param name="source">The received datagram.</param>
    /// <param name="sessionId">When this returns <see langword="true"/>, the session id that was read.</param>
    /// <param name="epoch">When this returns <see langword="true"/>, the epoch that was read.</param>
    /// <returns><see langword="true"/> when the datagram was long enough to carry both fields.</returns>
    public static bool TryReadSessionEpoch(ReadOnlySpan<byte> source, out Guid sessionId, out uint epoch)
    {
        if (source.Length < TypeSize + IdentitySize + EpochSize)
        {
            sessionId = default;
            epoch = 0;

            return false;
        }

        sessionId = ReadIdentity(source.Slice(TypeSize));
        epoch = ReadUInt32(source.Slice(TypeSize + IdentitySize));

        return true;
    }

    /// <summary>
    /// Reads the session id, secret and epoch a peer presents to prove it holds the session.
    /// </summary>
    /// <param name="source">The received datagram.</param>
    /// <param name="sessionId">When this returns <see langword="true"/>, the session id that was read.</param>
    /// <param name="sessionSecret">When this returns <see langword="true"/>, the secret that was read.</param>
    /// <param name="epoch">When this returns <see langword="true"/>, the epoch that was read.</param>
    /// <returns><see langword="true"/> when the datagram was long enough to carry all three fields.</returns>
    public static bool TryReadAuthenticated(ReadOnlySpan<byte> source, out Guid sessionId, out Guid sessionSecret, out uint epoch)
    {
        if (source.Length < TypeSize + IdentitySize + IdentitySize + EpochSize)
        {
            sessionId = default;
            sessionSecret = default;
            epoch = 0;

            return false;
        }

        sessionId = ReadIdentity(source.Slice(TypeSize));
        sessionSecret = ReadIdentity(source.Slice(TypeSize + IdentitySize));
        epoch = ReadUInt32(source.Slice(TypeSize + IdentitySize + IdentitySize));

        return true;
    }

    /// <summary>
    /// Reads a credential written at the supplied offset.
    /// </summary>
    /// <param name="source">The received datagram.</param>
    /// <param name="offset">The offset the credential starts at, past whatever header preceded it.</param>
    /// <param name="adapterTag">When this returns <see langword="true"/>, the service the credential belongs to.</param>
    /// <param name="credential">When this returns <see langword="true"/>, the opaque credential bytes.</param>
    /// <returns><see langword="true"/> when a complete credential was present.</returns>
    public static bool TryReadCredential(ReadOnlySpan<byte> source, int offset, out string adapterTag, out byte[] credential)
    {
        adapterTag = string.Empty;
        credential = [];

        if (source.Length < offset + 1)
            return false;

        int adapterTagByteCount = source[offset];

        offset += 1;

        if (source.Length < offset + adapterTagByteCount + 2)
            return false;

        adapterTag = Encoding.UTF8.GetString(source.Slice(offset, adapterTagByteCount));

        offset += adapterTagByteCount;

        int credentialLength = ReadUInt16(source.Slice(offset));

        offset += 2;

        if (source.Length < offset + credentialLength)
            return false;

        credential = source.Slice(offset, credentialLength).ToArray();

        return true;
    }

    /// <summary>
    /// The offset a credential starts at in a message whose header is a type, a session id and an epoch.
    /// </summary>
    public static int SessionEpochHeaderSize => TypeSize + IdentitySize + EpochSize;

    /// <summary>
    /// The offset a credential starts at in a message whose header is a type, a session id, a secret and an epoch.
    /// </summary>
    public static int AuthenticatedHeaderSize => TypeSize + IdentitySize + IdentitySize + EpochSize;

    /// <summary>
    /// Compares two identities without leaking, through timing, how many leading bytes matched.
    /// </summary>
    /// <param name="left">The identity presented by a peer.</param>
    /// <param name="right">The identity newfarm holds.</param>
    /// <returns><see langword="true"/> when the two are equal.</returns>
    /// <remarks>
    /// Hand written rather than taken from <c>CryptographicOperations</c> so the behaviour is identical on every
    /// target framework the client is built for.
    /// </remarks>
    public static bool FixedTimeEquals(Guid left, Guid right)
    {
        Span<byte> leftBytes = stackalloc byte[IdentitySize];
        Span<byte> rightBytes = stackalloc byte[IdentitySize];

        WriteIdentity(leftBytes, left);
        WriteIdentity(rightBytes, right);

        int difference = 0;

        for (int i = 0; i < IdentitySize; i++)
            difference |= leftBytes[i] ^ rightBytes[i];

        return difference == 0;
    }

    /// <summary>
    /// Writes the sixteen raw bytes of an identity.
    /// </summary>
    /// <param name="destination">The buffer to write into.</param>
    /// <param name="identity">The identity to write.</param>
    /// <returns>The number of bytes written.</returns>
    private static int WriteIdentity(Span<byte> destination, Guid identity)
    {
#if NET8_0_OR_GREATER
        identity.TryWriteBytes(destination);
#else
        byte[] identityBytes = identity.ToByteArray();

        identityBytes.CopyTo(destination);
#endif
        return IdentitySize;
    }

    /// <summary>
    /// Reads the sixteen raw bytes of an identity.
    /// </summary>
    /// <param name="source">The buffer to read from, positioned at the identity.</param>
    /// <returns>The identity that was read.</returns>
    private static Guid ReadIdentity(ReadOnlySpan<byte> source)
    {
#if NET8_0_OR_GREATER
        return new Guid(source.Slice(0, IdentitySize));
#else
        return new Guid(source.Slice(0, IdentitySize).ToArray());
#endif
    }

    /// <summary>
    /// Writes an unsigned 32 bit value in big-endian order.
    /// </summary>
    /// <param name="destination">The buffer to write into.</param>
    /// <param name="value">The value to write.</param>
    /// <returns>The number of bytes written.</returns>
    private static int WriteUInt32(Span<byte> destination, uint value)
    {
        destination[0] = (byte)(value >> 24);
        destination[1] = (byte)(value >> 16);
        destination[2] = (byte)(value >> 8);
        destination[3] = (byte)value;

        return 4;
    }

    /// <summary>
    /// Reads an unsigned 32 bit value written in big-endian order.
    /// </summary>
    /// <param name="source">The buffer to read from, positioned at the value.</param>
    /// <returns>The value that was read.</returns>
    private static uint ReadUInt32(ReadOnlySpan<byte> source) => ((uint)source[0] << 24) | ((uint)source[1] << 16) | ((uint)source[2] << 8) | source[3];

    /// <summary>
    /// Writes an unsigned 16 bit value in big-endian order.
    /// </summary>
    /// <param name="destination">The buffer to write into.</param>
    /// <param name="value">The value to write.</param>
    /// <returns>The number of bytes written.</returns>
    private static int WriteUInt16(Span<byte> destination, ushort value)
    {
        destination[0] = (byte)(value >> 8);
        destination[1] = (byte)value;

        return 2;
    }

    /// <summary>
    /// Reads an unsigned 16 bit value written in big-endian order.
    /// </summary>
    /// <param name="source">The buffer to read from, positioned at the value.</param>
    /// <returns>The value that was read.</returns>
    private static ushort ReadUInt16(ReadOnlySpan<byte> source) => (ushort)((source[0] << 8) | source[1]);
}

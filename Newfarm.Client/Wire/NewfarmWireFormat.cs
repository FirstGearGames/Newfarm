using System;
using System.Text;

namespace Newfarm.Wire
{
    /// <summary>
    /// Reads and writes the newfarm wire format, which is a leading <see cref="NewfarmPacketType"/> byte followed by a
    /// fixed field order per type.
    /// </summary>
    /// <remarks>
    /// Every field is written in a fixed order with no framing, because every message is a single datagram whose length
    /// the socket already reports. Multi-byte integers are big-endian so a capture is readable left to right.
    /// A session id travels as eight raw bytes rather than as text, which keeps a heartbeat down to thirteen bytes before
    /// padding.
    /// </remarks>
    public static class NewfarmWireFormat
    {
        /// <summary>
        /// The size in bytes of the leading <see cref="NewfarmPacketType"/> field.
        /// </summary>
        public const int TypeSize = 1;

        /// <summary>
        /// The size in bytes of a session id on the wire.
        /// </summary>
        public const int SessionIdSize = 8;

        /// <summary>
        /// The size in bytes of an epoch field.
        /// </summary>
        public const int EpochSize = 4;

        /// <summary>
        /// The most characters a room credential or an adapter tag may run to.
        /// </summary>
        /// <remarks>
        /// Text rather than raw bytes, so whatever a service hands out fits without newfarm knowing anything about it: a
        /// room code, an allocation id, a host and port. Anything longer is refused rather than truncated, because half a
        /// credential is worse than none.
        /// </remarks>
        public const int MaximumTextLength = 32;

        /// <summary>
        /// The largest datagram newfarm will read or write.
        /// </summary>
        public const int MaximumDatagramSize = 1024;

        /// <summary>
        /// The smallest datagram newfarm will act on. Anything shorter is dropped without a reply.
        /// </summary>
        /// <remarks>
        /// This is what stops newfarm being used to attack somebody else. A directory that answered a thirteen byte
        /// request with a long reply would let an attacker put a victim's address on the request and have newfarm do the
        /// sending, several times amplified. Requiring every request to be at least as large as the largest possible
        /// reply removes the gain entirely, so it is derived from that reply rather than picked: a session id, an epoch,
        /// and two values at their longest. Legitimate peers pad, which costs a heartbeat a few hundred bytes a second
        /// and costs an attacker the whole technique.
        /// </remarks>
        public const int MinimumRequestSize = TypeSize + SessionIdSize + EpochSize + (2 * (TextLengthPrefixSize + (MaximumTextLength * MaximumUtf8BytesPerCharacter)));

        /// <summary>
        /// The offset a credential starts at in a message whose header is a type, a session id and an epoch.
        /// </summary>
        public static int SessionEpochHeaderSize => TypeSize + SessionIdSize + EpochSize;

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
        public static int WriteSession(Span<byte> destination, NewfarmPacketType newfarmPacketType, ulong sessionId)
        {
            int offset = WriteType(destination, newfarmPacketType);

            offset += WriteUInt64(destination.Slice(offset), sessionId);

            return offset;
        }

        /// <summary>
        /// Writes a type byte followed by a session id and an epoch, which is the shape of nearly every newfarm message.
        /// </summary>
        /// <param name="destination">The buffer to write into.</param>
        /// <param name="newfarmPacketType">The type to write.</param>
        /// <param name="sessionId">The session the message concerns.</param>
        /// <param name="epoch">The epoch the message concerns.</param>
        /// <returns>The number of bytes written.</returns>
        public static int WriteSessionEpoch(Span<byte> destination, NewfarmPacketType newfarmPacketType, ulong sessionId, uint epoch)
        {
            int offset = WriteSession(destination, newfarmPacketType, sessionId);

            offset += WriteUInt32(destination.Slice(offset), epoch);

            return offset;
        }

        /// <summary>
        /// Writes a refusal, which is a type byte, the session the request named, and why it was refused.
        /// </summary>
        /// <param name="destination">The buffer to write into.</param>
        /// <param name="sessionId">The session the request named.</param>
        /// <param name="newfarmRefusalReason">Why the request was refused.</param>
        /// <returns>The number of bytes written.</returns>
        public static int WriteRefused(Span<byte> destination, ulong sessionId, NewfarmRefusalReason newfarmRefusalReason)
        {
            int offset = WriteSession(destination, NewfarmPacketType.Refused, sessionId);

            destination[offset] = (byte)newfarmRefusalReason;

            return offset + ReasonSize;
        }

        /// <summary>
        /// Writes where a session lives, following an already written header.
        /// </summary>
        /// <param name="destination">The buffer to write into, positioned past the header.</param>
        /// <param name="adapterTag">Names the service the credential belongs to, so a peer can tell whether it can use it.</param>
        /// <param name="credential">What a peer needs to reach the room. Newfarm never interprets this.</param>
        /// <returns>The number of bytes written.</returns>
        public static int WriteCredential(Span<byte> destination, string adapterTag, string credential)
        {
            int offset = WriteText(destination, adapterTag);

            offset += WriteText(destination.Slice(offset), credential);

            return offset;
        }

        /// <summary>
        /// Pads a written request out to <see cref="MinimumRequestSize"/>, which every request has to reach to be acted
        /// on.
        /// </summary>
        /// <param name="destination">The buffer the request was written into.</param>
        /// <param name="length">How many bytes the request itself came to.</param>
        /// <returns>The padded length.</returns>
        public static int PadRequest(Span<byte> destination, int length)
        {
            if (length >= MinimumRequestSize)
                return length;

            destination.Slice(length, MinimumRequestSize - length).Clear();

            return MinimumRequestSize;
        }

        /// <summary>
        /// Returns whether a value is short enough to travel on the wire.
        /// </summary>
        /// <param name="text">The value to measure.</param>
        /// <returns><see langword="true"/> when it fits.</returns>
        public static bool IsTextWithinLimit(string text)
        {
            return text is not null && text.Length <= MaximumTextLength;
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
        public static bool TryReadSession(ReadOnlySpan<byte> source, out ulong sessionId)
        {
            if (source.Length < TypeSize + SessionIdSize)
            {
                sessionId = 0;

                return false;
            }

            sessionId = ReadUInt64(source.Slice(TypeSize));

            return true;
        }

        /// <summary>
        /// Reads a session id and epoch written directly after the type byte.
        /// </summary>
        /// <param name="source">The received datagram.</param>
        /// <param name="sessionId">When this returns <see langword="true"/>, the session id that was read.</param>
        /// <param name="epoch">When this returns <see langword="true"/>, the epoch that was read.</param>
        /// <returns><see langword="true"/> when the datagram was long enough to carry both fields.</returns>
        public static bool TryReadSessionEpoch(ReadOnlySpan<byte> source, out ulong sessionId, out uint epoch)
        {
            if (source.Length < SessionEpochHeaderSize)
            {
                sessionId = 0;
                epoch = 0;

                return false;
            }

            sessionId = ReadUInt64(source.Slice(TypeSize));
            epoch = ReadUInt32(source.Slice(TypeSize + SessionIdSize));

            return true;
        }

        /// <summary>
        /// Reads why a request was refused.
        /// </summary>
        /// <param name="source">The received datagram.</param>
        /// <param name="sessionId">When this returns <see langword="true"/>, the session the refusal concerns.</param>
        /// <param name="newfarmRefusalReason">When this returns <see langword="true"/>, why the request was refused.</param>
        /// <returns><see langword="true"/> when a complete refusal was present.</returns>
        public static bool TryReadRefused(ReadOnlySpan<byte> source, out ulong sessionId, out NewfarmRefusalReason newfarmRefusalReason)
        {
            newfarmRefusalReason = default;

            if (!TryReadSession(source, out sessionId))
                return false;

            if (source.Length < TypeSize + SessionIdSize + ReasonSize)
                return false;

            newfarmRefusalReason = (NewfarmRefusalReason)source[TypeSize + SessionIdSize];

            return true;
        }

        /// <summary>
        /// Reads a credential written at the supplied offset.
        /// </summary>
        /// <param name="source">The received datagram.</param>
        /// <param name="offset">The offset the credential starts at, past whatever header preceded it.</param>
        /// <param name="adapterTag">When this returns <see langword="true"/>, the service the credential belongs to.</param>
        /// <param name="credential">When this returns <see langword="true"/>, what a peer needs to reach the room.</param>
        /// <returns><see langword="true"/> when both values were present and within the length limit.</returns>
        public static bool TryReadCredential(ReadOnlySpan<byte> source, int offset, out string adapterTag, out string credential)
        {
            credential = string.Empty;

            if (!TryReadText(source, ref offset, out adapterTag))
                return false;

            return TryReadText(source, ref offset, out credential);
        }

        /// <summary>
        /// Writes a length-prefixed value.
        /// </summary>
        /// <param name="destination">The buffer to write into.</param>
        /// <param name="text">The value to write, which must be within <see cref="MaximumTextLength"/>.</param>
        /// <returns>The number of bytes written.</returns>
        private static int WriteText(Span<byte> destination, string text)
        {
            int byteCount = Encoding.UTF8.GetByteCount(text);

            destination[0] = checked((byte)byteCount);

            Encoding.UTF8.GetBytes(text.AsSpan(), destination.Slice(TextLengthPrefixSize, byteCount));

            return TextLengthPrefixSize + byteCount;
        }

        /// <summary>
        /// Reads a length-prefixed value and advances past it.
        /// </summary>
        /// <param name="source">The received datagram.</param>
        /// <param name="offset">The offset to read at, advanced past the value that was read.</param>
        /// <param name="text">When this returns <see langword="true"/>, the value that was read.</param>
        /// <returns><see langword="true"/> when a complete value was present and within the length limit.</returns>
        private static bool TryReadText(ReadOnlySpan<byte> source, ref int offset, out string text)
        {
            text = string.Empty;

            if (source.Length < offset + TextLengthPrefixSize)
                return false;

            int byteCount = source[offset];

            offset += TextLengthPrefixSize;

            if (byteCount > MaximumTextLength * MaximumUtf8BytesPerCharacter || source.Length < offset + byteCount)
                return false;

            text = Encoding.UTF8.GetString(source.Slice(offset, byteCount));

            offset += byteCount;

            return text.Length <= MaximumTextLength;
        }

        /// <summary>
        /// Writes an unsigned 64 bit value in big-endian order.
        /// </summary>
        /// <param name="destination">The buffer to write into.</param>
        /// <param name="value">The value to write.</param>
        /// <returns>The number of bytes written.</returns>
        private static int WriteUInt64(Span<byte> destination, ulong value)
        {
            destination[0] = (byte)(value >> 56);
            destination[1] = (byte)(value >> 48);
            destination[2] = (byte)(value >> 40);
            destination[3] = (byte)(value >> 32);
            destination[4] = (byte)(value >> 24);
            destination[5] = (byte)(value >> 16);
            destination[6] = (byte)(value >> 8);
            destination[7] = (byte)value;

            return SessionIdSize;
        }

        /// <summary>
        /// Reads an unsigned 64 bit value written in big-endian order.
        /// </summary>
        /// <param name="source">The buffer to read from, positioned at the value.</param>
        /// <returns>The value that was read.</returns>
        private static ulong ReadUInt64(ReadOnlySpan<byte> source)
        {
            return ((ulong)source[0] << 56) | ((ulong)source[1] << 48) | ((ulong)source[2] << 40) | ((ulong)source[3] << 32) | ((ulong)source[4] << 24) | ((ulong)source[5] << 16) | ((ulong)source[6] << 8) | source[7];
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

            return EpochSize;
        }

        /// <summary>
        /// Reads an unsigned 32 bit value written in big-endian order.
        /// </summary>
        /// <param name="source">The buffer to read from, positioned at the value.</param>
        /// <returns>The value that was read.</returns>
        private static uint ReadUInt32(ReadOnlySpan<byte> source) => ((uint)source[0] << 24) | ((uint)source[1] << 16) | ((uint)source[2] << 8) | source[3];

        /// <summary>
        /// The size in bytes of the reason field on a refusal.
        /// </summary>
        private const int ReasonSize = 1;

        /// <summary>
        /// The size in bytes of the length that precedes a value.
        /// </summary>
        private const int TextLengthPrefixSize = 1;

        /// <summary>
        /// The most bytes one character can take once encoded, which bounds what a length prefix may claim.
        /// </summary>
        private const int MaximumUtf8BytesPerCharacter = 4;
    }
}

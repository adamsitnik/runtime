// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Runtime.Versioning;
using Internal.Cryptography;

namespace System.Security.Cryptography
{
    /// <summary>
    /// Provides support for computing a hash or HMAC value incrementally across several segments.
    /// </summary>
    public sealed class IncrementalHash : IDisposable
    {
        private readonly HashAlgorithmName _algorithmName;
        private HashProvider? _hash;
        private HMACCommon? _hmac;
        private bool _disposed;

        /// <summary>
        ///   Gets the output size of this hash or HMAC algorithm, in bytes.
        /// </summary>
        /// <value>
        ///   The output size of this hash or HMAC algorithm, in bytes.
        /// </value>
        public int HashLengthInBytes { get; }

        private IncrementalHash(HashAlgorithmName name, HashProvider hash)
        {
            Debug.Assert(!string.IsNullOrEmpty(name.Name));
            Debug.Assert(hash != null);

            _algorithmName = name;
            _hash = hash;
            HashLengthInBytes = _hash.HashSizeInBytes;
        }

        private IncrementalHash(HashAlgorithmName name, HMACCommon hmac)
        {
            Debug.Assert(!string.IsNullOrEmpty(name.Name));
            Debug.Assert(hmac != null);

            _algorithmName = new HashAlgorithmName("HMAC" + name.Name);
            _hmac = hmac;
            HashLengthInBytes = _hmac.HashSizeInBytes;
        }

        /// <summary>
        /// Get the name of the algorithm being performed.
        /// </summary>
        public HashAlgorithmName AlgorithmName => _algorithmName;

        /// <summary>
        /// Append the entire contents of <paramref name="data"/> to the data already processed in the hash or HMAC.
        /// </summary>
        /// <param name="data">The data to process.</param>
        /// <exception cref="ArgumentNullException"><paramref name="data"/> is <c>null</c>.</exception>
        /// <exception cref="ObjectDisposedException">The object has already been disposed.</exception>
        public void AppendData(byte[] data)
        {
            ArgumentNullException.ThrowIfNull(data);

            AppendData(new ReadOnlySpan<byte>(data));
        }

        /// <summary>
        /// Appends <paramref name="count"/> bytes of <paramref name="data"/>, starting at <paramref name="offset"/>,
        /// to the data already processed in the hash or HMAC.
        /// </summary>
        /// <param name="data">The data to process.</param>
        /// <param name="offset">The offset into the byte array from which to begin using data.</param>
        /// <param name="count">The number of bytes in the array to use as data.</param>
        /// <exception cref="ArgumentNullException"><paramref name="data"/> is <c>null</c>.</exception>
        /// <exception cref="ArgumentOutOfRangeException">
        ///     <paramref name="offset"/> is out of range. This parameter requires a non-negative number.
        /// </exception>
        /// <exception cref="ArgumentOutOfRangeException">
        ///     <paramref name="count"/> is out of range. This parameter requires a non-negative number less than
        ///     the <see cref="Array.Length"/> value of <paramref name="data"/>.
        ///     </exception>
        /// <exception cref="ArgumentException">
        ///     <paramref name="count"/> is greater than
        ///     <paramref name="data"/>.<see cref="Array.Length"/> - <paramref name="offset"/>.
        /// </exception>
        /// <exception cref="ObjectDisposedException">The object has already been disposed.</exception>
        public void AppendData(byte[] data, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(data);

            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(count, data.Length);
            if ((data.Length - count) < offset)
                throw new ArgumentException(SR.Argument_InvalidOffLen);
            ObjectDisposedException.ThrowIf(_disposed, this);

            AppendData(new ReadOnlySpan<byte>(data, offset, count));
        }

        public void AppendData(ReadOnlySpan<byte> data)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            Debug.Assert((_hash != null) ^ (_hmac != null));
            if (_hash != null)
            {
                _hash.AppendHashData(data);
            }
            else
            {
                _hmac!.AppendHashData(data);
            }
        }

        /// <summary>
        /// Retrieves the hash or HMAC for the data accumulated from prior calls to
        /// <see cref="AppendData(byte[])"/>, and resets the object to its initial state.
        /// </summary>
        /// <returns>The computed hash or HMAC.</returns>
        /// <exception cref="ObjectDisposedException">The object has already been disposed.</exception>
        public byte[] GetHashAndReset()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            byte[] ret = new byte[HashLengthInBytes];

            int written = GetHashAndResetCore(ret);
            Debug.Assert(written == HashLengthInBytes);

            return ret;
        }

        /// <summary>
        ///   Retrieves the hash or Hash-based Message Authentication Code (HMAC) for the data
        ///   accumulated from prior calls to the
        ///   <see cref="AppendData(ReadOnlySpan{byte})" />
        ///   methods, and resets the object to its initial state.
        /// </summary>
        /// <param name="destination">
        ///   The buffer to receive the hash or HMAC value.
        /// </param>
        /// <returns>
        ///   The number of bytes written to <paramref name="destination"/>.
        /// </returns>
        /// <exception cref="ArgumentException">
        ///   <paramref name="destination"/> has a <see cref="Span{T}.Length"/> value less
        ///   than <see cref="HashLengthInBytes"/>.
        /// </exception>
        /// <exception cref="ObjectDisposedException">The object has already been disposed.</exception>
        public int GetHashAndReset(Span<byte> destination)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (destination.Length < HashLengthInBytes)
                throw new ArgumentException(SR.Argument_DestinationTooShort, nameof(destination));

            return GetHashAndResetCore(destination);
        }

        public bool TryGetHashAndReset(Span<byte> destination, out int bytesWritten)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (destination.Length < HashLengthInBytes)
            {
                bytesWritten = 0;
                return false;
            }

            bytesWritten = GetHashAndResetCore(destination);
            return true;
        }

        private int GetHashAndResetCore(Span<byte> destination)
        {
            Debug.Assert(destination.Length >= HashLengthInBytes);

            Debug.Assert((_hash != null) ^ (_hmac != null));
            return _hash != null ?
                _hash.FinalizeHashAndReset(destination) :
                _hmac!.FinalizeHashAndReset(destination);
        }

        /// <summary>
        ///   Retrieves the hash or Hash-based Message Authentication Code (HMAC) for the data
        ///   accumulated from prior calls to the
        ///   <see cref="AppendData(ReadOnlySpan{byte})" />
        ///   methods, without resetting the object to its initial state.
        /// </summary>
        /// <returns>
        ///   The computed hash or HMAC.
        /// </returns>
        /// <exception cref="ObjectDisposedException">The object has already been disposed.</exception>
        public byte[] GetCurrentHash()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            byte[] ret = new byte[HashLengthInBytes];

            int written = GetCurrentHashCore(ret);
            Debug.Assert(written == HashLengthInBytes);

            return ret;
        }

        /// <summary>
        ///   Retrieves the hash or Hash-based Message Authentication Code (HMAC) for the data
        ///   accumulated from prior calls to the
        ///   <see cref="AppendData(ReadOnlySpan{byte})" />
        ///   methods, without resetting the object to its initial state.
        /// </summary>
        /// <param name="destination">
        ///   The buffer to receive the hash or HMAC value.
        /// </param>
        /// <returns>
        ///   The number of bytes written to <paramref name="destination"/>.
        /// </returns>
        /// <exception cref="ArgumentException">
        ///   <paramref name="destination"/> has a <see cref="Span{T}.Length"/> value less
        ///   than <see cref="HashLengthInBytes"/>.
        /// </exception>
        /// <exception cref="ObjectDisposedException">The object has already been disposed.</exception>
        public int GetCurrentHash(Span<byte> destination)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (destination.Length < HashLengthInBytes)
                throw new ArgumentException(SR.Argument_DestinationTooShort, nameof(destination));

            return GetCurrentHashCore(destination);
        }

        /// <summary>
        ///   Attempts to retrieve the hash or Hash-based Message Authentication Code (HMAC) for
        ///   the data accumulated from prior calls to the
        ///   <see cref="AppendData(ReadOnlySpan{byte})" />
        ///   methods, without resetting the object to its initial state.
        /// </summary>
        /// <param name="destination">
        ///   The buffer to receive the hash or HMAC value.
        /// </param>
        /// <param name="bytesWritten">
        ///   When this method returns, contains the total number of bytes written into <paramref name="destination" />.
        ///   This parameter is treated as uninitialized.
        /// </param>
        /// <returns>
        ///   <see langword="true" /> if <paramref name="destination" /> is long enough to receive
        ///   the hash or HMAC value; otherwise, <see langword="false" />.
        /// </returns>
        /// <exception cref="ObjectDisposedException">The object has already been disposed.</exception>
        public bool TryGetCurrentHash(Span<byte> destination, out int bytesWritten)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (destination.Length < HashLengthInBytes)
            {
                bytesWritten = 0;
                return false;
            }

            bytesWritten = GetCurrentHashCore(destination);
            return true;
        }

        private int GetCurrentHashCore(Span<byte> destination)
        {
            Debug.Assert(destination.Length >= HashLengthInBytes);

            Debug.Assert((_hash != null) ^ (_hmac != null));
            return _hash != null ?
                _hash.GetCurrentHash(destination) :
                _hmac!.GetCurrentHash(destination);
        }

        /// <summary>
        /// Creates a new instance of <see cref="IncrementalHash" /> with the existing appended data preserved.
        /// </summary>
        /// <returns>A clone of the current instance.</returns>
        /// <exception cref="CryptographicException">An error occurred during the operation.</exception>
        /// <exception cref="ObjectDisposedException">The object has already been disposed.</exception>
        public IncrementalHash Clone()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Debug.Assert((_hash != null) ^ (_hmac != null));

            return _hash is not null ?
                new IncrementalHash(_algorithmName, _hash.Clone()) :
                new IncrementalHash(_algorithmName, _hmac!.Clone());
        }

        /// <summary>
        /// Release all resources used by the current instance of the
        /// <see cref="IncrementalHash"/> class.
        /// </summary>
        public void Dispose()
        {
            _disposed = true;

            if (_hash != null)
            {
                _hash.Dispose();
                _hash = null;
            }

            if (_hmac != null)
            {
                _hmac.Dispose(true);
                _hmac = null;
            }
        }

        /// <summary>
        /// Creates an <see cref="IncrementalHash"/> for the algorithm specified by <paramref name="hashAlgorithm"/>.
        /// </summary>
        /// <param name="hashAlgorithm">The name of the hash algorithm to perform.</param>
        /// <returns>
        /// An <see cref="IncrementalHash"/> instance ready to compute the hash algorithm specified
        /// by <paramref name="hashAlgorithm"/>.
        /// </returns>
        /// <exception cref="ArgumentException">
        ///     <paramref name="hashAlgorithm"/>.<see cref="HashAlgorithmName.Name"/> is <c>null</c>, or
        ///     the empty string.
        /// </exception>
        /// <exception cref="CryptographicException"><paramref name="hashAlgorithm"/> is not a known hash algorithm.</exception>
        public static IncrementalHash CreateHash(HashAlgorithmName hashAlgorithm)
        {
            ArgumentException.ThrowIfNullOrEmpty(hashAlgorithm.Name, nameof(hashAlgorithm));
            CheckSha3Support(hashAlgorithm.Name);

            return new IncrementalHash(hashAlgorithm, HashProviderDispenser.CreateHashProvider(hashAlgorithm.Name));
        }

        /// <summary>
        /// Creates an <see cref="IncrementalHash"/> for the Hash-based Message Authentication Code (HMAC)
        /// algorithm using the hash algorithm specified by <paramref name="hashAlgorithm"/> and a
        /// key specified by <paramref name="key"/>.
        /// </summary>
        /// <param name="hashAlgorithm">The name of the hash algorithm to perform within the HMAC.</param>
        /// <param name="key">
        ///     The secret key for the HMAC. The key can be any length, but a key longer than the output size
        ///     of the hash algorithm specified by <paramref name="hashAlgorithm"/> will be hashed (using the
        ///     algorithm specified by <paramref name="hashAlgorithm"/>) to derive a correctly sized key. Therefore,
        ///     the recommended size of the secret key is the output size of the hash specified by
        ///     <paramref name="hashAlgorithm"/>.
        /// </param>
        /// <returns>
        /// An <see cref="IncrementalHash"/> instance ready to compute the hash algorithm specified
        /// by <paramref name="hashAlgorithm"/>.
        /// </returns>
        /// <exception cref="ArgumentException">
        ///     <paramref name="hashAlgorithm"/>.<see cref="HashAlgorithmName.Name"/> is <c>null</c>, or
        ///     the empty string.
        /// </exception>
        /// <exception cref="CryptographicException"><paramref name="hashAlgorithm"/> is not a known hash algorithm.</exception>
        public static IncrementalHash CreateHMAC(HashAlgorithmName hashAlgorithm, byte[] key)
        {
            ArgumentNullException.ThrowIfNull(key);

            return CreateHMAC(hashAlgorithm, (ReadOnlySpan<byte>)key);
        }

        /// <summary>
        /// Creates an <see cref="IncrementalHash"/> for the Hash-based Message Authentication Code (HMAC)
        /// algorithm using the hash algorithm specified by <paramref name="hashAlgorithm"/> and a
        /// key specified by <paramref name="key"/>.
        /// </summary>
        /// <param name="hashAlgorithm">The name of the hash algorithm to perform within the HMAC.</param>
        /// <param name="key">
        ///     The secret key for the HMAC. The key can be any length, but a key longer than the output size
        ///     of the hash algorithm specified by <paramref name="hashAlgorithm"/> will be hashed (using the
        ///     algorithm specified by <paramref name="hashAlgorithm"/>) to derive a correctly sized key. Therefore,
        ///     the recommended size of the secret key is the output size of the hash specified by
        ///     <paramref name="hashAlgorithm"/>.
        /// </param>
        /// <returns>
        /// An <see cref="IncrementalHash"/> instance ready to compute the hash algorithm specified
        /// by <paramref name="hashAlgorithm"/>.
        /// </returns>
        /// <exception cref="ArgumentException">
        ///     <paramref name="hashAlgorithm"/>.<see cref="HashAlgorithmName.Name"/> is <c>null</c>, or
        ///     the empty string.
        /// </exception>
        /// <exception cref="CryptographicException"><paramref name="hashAlgorithm"/> is not a known hash algorithm.</exception>
        public static IncrementalHash CreateHMAC(HashAlgorithmName hashAlgorithm, ReadOnlySpan<byte> key)
        {
            ArgumentException.ThrowIfNullOrEmpty(hashAlgorithm.Name, nameof(hashAlgorithm));
            CheckSha3Support(hashAlgorithm.Name);

            return new IncrementalHash(hashAlgorithm, new HMACCommon(hashAlgorithm.Name, key, -1));
        }

        private static void CheckSha3Support(string hashAlgorithmName)
        {
            switch (hashAlgorithmName)
            {
                case HashAlgorithmNames.SHA3_256 when !SHA3_256.IsSupported:
                    Debug.Assert(!HMACSHA3_256.IsSupported);
                    throw new PlatformNotSupportedException();
                case HashAlgorithmNames.SHA3_384 when !SHA3_384.IsSupported:
                    Debug.Assert(!HMACSHA3_384.IsSupported);
                    throw new PlatformNotSupportedException();
                case HashAlgorithmNames.SHA3_512 when !SHA3_512.IsSupported:
                    Debug.Assert(!HMACSHA3_512.IsSupported);
                    throw new PlatformNotSupportedException();
                default:
                    // Other unknown algorithms will be handled separately as CryptographicExceptions.
                    break;
            }
        }
    }
}

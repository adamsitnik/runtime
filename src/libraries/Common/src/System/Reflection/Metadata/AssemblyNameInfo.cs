// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable enable

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace System.Reflection.Metadata
{
    [DebuggerDisplay("{FullName}")]
#if SYSTEM_PRIVATE_CORELIB
    internal
#else
    public
#endif
    sealed class AssemblyNameInfo : IEquatable<AssemblyNameInfo>
    {
        private string? _fullName;

#if !SYSTEM_PRIVATE_CORELIB
        public AssemblyNameInfo(string name, Version? version = null, string? cultureName = null, AssemblyNameFlags flags = AssemblyNameFlags.None,
           Collections.Immutable.ImmutableArray<byte> publicKey = default, Collections.Immutable.ImmutableArray<byte> publicKeyToken = default)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Version = version;
            CultureName = cultureName;

            if (!publicKey.IsDefaultOrEmpty
#if NET8_0_OR_GREATER
                && ValidatePublicKey(Runtime.InteropServices.ImmutableCollectionsMarshal.AsArray(publicKey)))
#else
                && ValidatePublicKey(System.Linq.ImmutableArrayExtensions.ToArray(publicKey)))
#endif
            {
                throw new ArgumentException("SR.Security_InvalidAssemblyPublicKey", nameof(publicKey)); // TODO adsitnik: use actual resource
            }

            PublicKey = publicKey;
            PublicKeyToken = publicKeyToken;

            if (!publicKey.IsDefaultOrEmpty)
            {
                flags |= AssemblyNameFlags.PublicKey;
            }

            Flags = flags;
        }
#endif

        internal AssemblyNameInfo(AssemblyNameParser.AssemblyNameParts parts)
        {
            Name = parts._name;
            Version = parts._version;
            CultureName = parts._cultureName;
            Flags = parts._flags;

            bool publicKey = (parts._flags & AssemblyNameFlags.PublicKey) != 0;

#if SYSTEM_PRIVATE_CORELIB
            PublicKey = publicKey ? parts._publicKeyOrToken : null;
            PublicKeyToken = publicKey ? null : parts._publicKeyOrToken;
#else
            PublicKey = ToImmutable(publicKey ? parts._publicKeyOrToken : null);
            PublicKeyToken = ToImmutable(publicKey ? null : parts._publicKeyOrToken);

            static Collections.Immutable.ImmutableArray<byte> ToImmutable(byte[]? bytes)
               => bytes is null ? default : bytes.Length == 0 ? Collections.Immutable.ImmutableArray<byte>.Empty :
    #if NET8_0_OR_GREATER
                    Runtime.InteropServices.ImmutableCollectionsMarshal.AsImmutableArray(bytes);
    #else
                    Collections.Immutable.ImmutableArray.Create(bytes);
    #endif
#endif
        }

        public string Name { get; }
        public Version? Version { get; }
        public string? CultureName { get; }
        public AssemblyNameFlags Flags { get; }

#if SYSTEM_PRIVATE_CORELIB
        public byte[]? PublicKey { get; }
        public byte[]? PublicKeyToken { get; }
#else
        public Collections.Immutable.ImmutableArray<byte> PublicKey { get; }
        public Collections.Immutable.ImmutableArray<byte> PublicKeyToken { get; }
#endif

        public string FullName
        {
            get
            {
                if (_fullName is null)
                {
#if SYSTEM_PRIVATE_CORELIB
                    byte[]? pkt = PublicKeyToken ?? AssemblyNameHelpers.ComputePublicKeyToken(PublicKey);
#elif NET8_0_OR_GREATER
                    byte[]? pkt = !PublicKeyToken.IsDefault
                        ? Runtime.InteropServices.ImmutableCollectionsMarshal.AsArray(PublicKeyToken)
                        : !PublicKey.IsDefault
                            ? AssemblyNameHelpers.ComputePublicKeyToken(Runtime.InteropServices.ImmutableCollectionsMarshal.AsArray(PublicKey))
                            : null;
#else
                    byte[]? pkt = !PublicKeyToken.IsDefault
                        ? System.Linq.ImmutableArrayExtensions.ToArray(PublicKeyToken)
                        : !PublicKey.IsDefault
                            ? AssemblyNameHelpers.ComputePublicKeyToken(System.Linq.ImmutableArrayExtensions.ToArray(PublicKey))
                            : null;
#endif
                    _fullName = AssemblyNameFormatter.ComputeDisplayName(Name, Version, CultureName, pkt/*, ExtractAssemblyNameFlags(Flags), ExtractAssemblyContentType(Flags)*/); ;
                }

                return _fullName;
            }
        }

        public bool Equals(AssemblyNameInfo? other)
        {
            if (other is null || Flags != other.Flags || !Name.Equals(other.Name) || !string.Equals(CultureName, other.CultureName))
            {
                return false;
            }

            if (Version is null)
            {
                if (other.Version is not null)
                {
                    return false;
                }
            }
            else
            {
                if (!Version.Equals(other.Version))
                {
                    return false;
                }
            }

            if (!SequenceEqual(PublicKey, other.PublicKey) || !SequenceEqual(PublicKeyToken, other.PublicKeyToken))
            {
                return false;
            }

            return true;

#if SYSTEM_PRIVATE_CORELIB
            static bool SequenceEqual(byte[]? left, byte[]? right)
            {
                if (left is null)
                {
                    if (right is not null)
                    {
                        return false;
                    }
                }
                else if (right is null)
                {
                    return false;
                }
                else if (left.Length != right.Length)
                {
                    return false;
                }
                else
                {
                    for (int i = 0; i < left.Length; i++)
                    {
                        if (left[i] != right[i])
                        {
                            return false;
                        }
                    }
                }

                return true;
            }
#else
            static bool SequenceEqual(Collections.Immutable.ImmutableArray<byte> left, Collections.Immutable.ImmutableArray<byte> right)
            {
                int leftLength = left.IsDefaultOrEmpty ? 0 : left.Length;
                int rightLength = right.IsDefaultOrEmpty ? 0 : right.Length;

                if (leftLength != rightLength)
                {
                    return false;
                }
                else if (leftLength > 0)
                {
                    for (int i = 0; i < leftLength; i++)
                    {
                        if (left[i] != right[i])
                        {
                            return false;
                        }
                    }
                }

                return true;
            }
#endif
        }

        public override bool Equals(object? obj) => Equals(obj as AssemblyNameInfo);

        public override int GetHashCode() => FullName.GetHashCode();

        public AssemblyName ToAssemblyName()
        {
            AssemblyName assemblyName = new();
            assemblyName.Name = Name;
            assemblyName.CultureName = CultureName;
            assemblyName.Version = Version;

#if SYSTEM_PRIVATE_CORELIB
            assemblyName._flags = Flags;
            assemblyName.SetPublicKey(PublicKey);
            assemblyName.SetPublicKeyToken(PublicKeyToken);
#else
            assemblyName.Flags = Flags;

            if (!PublicKey.IsDefault)
            {
                assemblyName.SetPublicKey(System.Linq.ImmutableArrayExtensions.ToArray(PublicKey));
            }
            if (!PublicKeyToken.IsDefault)
            {
                assemblyName.SetPublicKeyToken(System.Linq.ImmutableArrayExtensions.ToArray(PublicKeyToken));
            }
#endif

            return assemblyName;
        }

        /// <summary>
        /// Parses a span of characters into a assembly name.
        /// </summary>
        /// <param name="assemblyName">A span containing the characters representing the assembly name to parse.</param>
        /// <returns>Parsed type name.</returns>
        /// <exception cref="ArgumentException">Provided assembly name was invalid.</exception>
        public static AssemblyNameInfo Parse(ReadOnlySpan<char> assemblyName)
            => TryParse(assemblyName, out AssemblyNameInfo? result)
                ? result
                : throw new ArgumentException("TODO_adsitnik_add_or_reuse_resource");

        /// <summary>
        /// Tries to parse a span of characters into an assembly name.
        /// </summary>
        /// <param name="assemblyName">A span containing the characters representing the assembly name to parse.</param>
        /// <param name="result">Contains the result when parsing succeeds.</param>
        /// <returns>true if assembly name was converted successfully, otherwise, false.</returns>
        public static bool TryParse(ReadOnlySpan<char> assemblyName,
#if SYSTEM_REFLECTION_METADATA || SYSTEM_PRIVATE_CORELIB // required by some tools that include this file but don't include the attribute
            [NotNullWhen(true)]
#endif
            out AssemblyNameInfo? result)
        {
            AssemblyNameParser.AssemblyNameParts parts = default;
            if (AssemblyNameParser.TryParse(assemblyName, ref parts)
                && ((parts._flags & AssemblyNameFlags.PublicKey) == 0 || ValidatePublicKey(parts._publicKeyOrToken)))
            {
                result = new(parts);
                return true;
            }

            result = null;
            return false;
        }

        private static bool ValidatePublicKey(byte[]? publicKey)
            => publicKey is null
            || publicKey.Length == 0
            || AssemblyNameHelpers.IsValidPublicKey(publicKey);
    }
}

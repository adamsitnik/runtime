// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;

namespace System.Reflection.Metadata.Tests.Metadata
{
    public class AssemblyNameInfoTests
    {
        [Theory]
        [InlineData("MyAssemblyName, Version=1.0.0.0, PublicKeyToken=b77a5c561934e089", "MyAssemblyName, Version=1.0.0.0, PublicKeyToken=b77a5c561934e089")]
        [InlineData("MyAssemblyName, Version=1.0.0.0, PublicKey=00000000000000000400000000000000", "MyAssemblyName, Version=1.0.0.0, PublicKeyToken=b77a5c561934e089")]
        [InlineData("TerraFX.Interop.Windows, PublicKey=" +
    "002400000c800000940000000602000000240000525341310004000001000100897039f5ff762b25b9ba982c3f5836c34e299279c33df505bf806a07bccdf0e1216e661943f557b954cb18422ed522a5" +
    "b3174b85385052677f39c4ce19f30a1ddbaa507054bc5943461651f396afc612cd80419c5ee2b5277571ff65f51d14ba99e4e4196de0f393e89850a465f019dbdc365ed5e81bbafe1370f54efd254ba8",
    "TerraFX.Interop.Windows, PublicKeyToken=35b01b53313a6f7e")]
        public void WithPublicKeyOrToken(string name, string expectedName)
        {
            AssemblyName assemblyName = new AssemblyName(name);

            AssemblyNameInfo assemblyNameInfo = AssemblyNameInfo.Parse(name.AsSpan());

            Assert.Equal(expectedName, assemblyName.FullName);
            Assert.Equal(expectedName, assemblyNameInfo.FullName);

            Roundtrip(assemblyName);
        }

        [Fact]
        public void NoPublicKeyOrToken()
        {
            AssemblyName source = new AssemblyName();
            source.Name = "test";
            source.Version = new Version(1, 2, 3, 4);
            source.CultureName = "en-US";

            Roundtrip(source);
        }

        static void Roundtrip(AssemblyName source)
        {
            AssemblyNameInfo parsed = AssemblyNameInfo.Parse(source.FullName.AsSpan());
            Assert.Equal(source.Name, parsed.Name);
            Assert.Equal(source.Version, parsed.Version);
            Assert.Equal(source.CultureName, parsed.CultureName);
            Assert.Equal(source.FullName, parsed.FullName);

            AssemblyName fromParsed = parsed.ToAssemblyName();
            Assert.Equal(source.Name, fromParsed.Name);
            Assert.Equal(source.Version, fromParsed.Version);
            Assert.Equal(source.CultureName, fromParsed.CultureName);
            Assert.Equal(source.FullName, fromParsed.FullName);
        }
    }
}

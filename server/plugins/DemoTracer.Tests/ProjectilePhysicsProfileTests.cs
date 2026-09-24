/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

namespace DemoTracer.Tests;

public sealed class ProjectilePhysicsProfileTests
{
    private static ProjectilePhysicsProfile Profile(int entry = 0x1000) => new(
        new string('0', 64), new string('0', 64), entry, 32, [0x2100], "C3 00 00 00 00");

    [Fact]
    public void ResolvesOnlyTheProfileEntryInExecutableSections()
        => Assert.Equal(0x1000, Profile().ResolveEntry(PeImageFingerprintTests.Fixture()));

    [Fact]
    public void RejectsUniqueButWrongTarget()
        => Assert.Throws<InvalidDataException>(() => Profile(0x1010).ResolveEntry(PeImageFingerprintTests.Fixture()));

    [Fact]
    public void RejectsAmbiguousTarget()
    {
        byte[] file = PeImageFingerprintTests.Fixture(); file[0x210] = 0xc3;
        Assert.Throws<InvalidDataException>(() => Profile().ResolveEntry(file));
    }

    [Fact]
    public void MissingProfileCannotFallBackToEmbeddedAddresses()
        => Assert.Throws<FileNotFoundException>(() => ProjectilePhysicsProfile.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json")));
}

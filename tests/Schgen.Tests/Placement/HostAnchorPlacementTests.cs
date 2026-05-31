using FluentAssertions;
using Schgen.Core.KiCad;
using Schgen.Core.Placement;
using Xunit;

namespace Schgen.Tests.Placement;

public class HostAnchorPlacementTests
{
    [Theory]
    // Real bug case: host pin N9 (rot 270) on a UC at rotation 0.
    // sourceOutwardDeg = (270 + 0 + 180) % 360 = 90. Cap pin 1 (rot 270).
    // rawDeg = 90 - 270 = -180 -> snapped = -180 -> normalized = 180.
    [InlineData(90.0, 270.0, 180)]
    // Symmetric case: GND pin A9 (rot 90). sourceOutwardDeg = (90 + 180) = 270.
    // Cap pin 2 (rot 90). rawDeg = 270 - 90 = 180 -> snapped = 180.
    [InlineData(270.0, 90.0, 180)]
    // Horizontal IN pin (rot 180). sourceOutwardDeg = (180 + 180) = 0.
    // Cap pin 1 (rot 270). rawDeg = 0 - 270 = -270 -> snapped = -270 -> +360 = 90.
    [InlineData(0.0, 270.0, 90)]
    // Horizontal OUT pin (rot 0). sourceOutwardDeg = 180.
    // Cap pin 2 (rot 90). rawDeg = 180 - 90 = 90.
    [InlineData(180.0, 90.0, 90)]
    public void SolveColinearRotation_returns_expected_snap(
        double sourceOutwardDeg, double targetPinRot, int expected)
    {
        var pin = new PinDef(name: "~", number: "1", type: PinType.Passive,
            x: 0, y: 3.81, rotation: targetPinRot, length: 1.27);
        SchPlacer.SolveColinearRotation(pin, sourceOutwardDeg).Should().Be(expected);
    }
}

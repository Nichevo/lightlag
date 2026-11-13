namespace Lightlag.Sim.Core;

/// <summary>Placeholder until the contact build lands (see Contact below).</summary>
public sealed class ContactData
{
    public double PrisonerPoints { get; set; } = 6;
    public double CapturePoints { get; set; } = 10;
    public double WreckPoints { get; set; } = 3;
}

public static class Contact
{
    public static ContactData D { get; } = new();
    public static void Learn(World w, Side side, Side about, double points, string how) { }
}

namespace ComCon.Core;

public static class FoundationCapabilities
{
    public static int MaximumProfiles => 4;
    public static bool HardwareRoutingImplemented => false;
    public const string Status = "Foundation only - hardware routing is not implemented.";
}

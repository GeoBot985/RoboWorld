namespace RoboWorld.Contracts;

/// <summary>Protocol and persisted contract versions are explicit and independently evolvable.</summary>
public static class ContractVersion
{
    public const int SchemaVersion = 1;
    public const string ProtobufPackage = "roboworld.runtime.v1";
}

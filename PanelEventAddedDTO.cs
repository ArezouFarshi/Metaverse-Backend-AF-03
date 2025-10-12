using System.Numerics;
using Nethereum.ABI.FunctionEncoding.Attributes;
using Nethereum.Contracts;

[Event("PanelEventAdded")]
public class PanelEventAddedDTO : IEventDTO
{
    [Parameter("string",  "panelId",      1, false)] public string? PanelId { get; set; }
    [Parameter("string",  "eventType",    2, false)] public string? EventType { get; set; }
    [Parameter("string",  "faultType",    3, false)] public string? FaultType { get; set; }
    [Parameter("string",  "faultSeverity",4, false)] public string? FaultSeverity { get; set; }
    [Parameter("string",  "actionTaken",  5, false)] public string? ActionTaken { get; set; }
    [Parameter("bytes32", "eventHash",    6, false)] public byte[]? EventHash { get; set; }
    [Parameter("address", "validatedBy",  7, false)] public string? ValidatedBy { get; set; }
    [Parameter("uint256", "timestamp",    8, false)] public BigInteger Timestamp { get; set; }
}

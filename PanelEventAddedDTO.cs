using System.Numerics;
using Nethereum.ABI.FunctionEncoding.Attributes;
using Nethereum.Contracts;

[Event("PanelEventAdded")]
public class PanelEventAddedDTO : IEventDTO
{
    [Parameter("string", "panelId", 1, false)] public string? PanelId { get; set; }
    [Parameter("bool", "ok", 2, false)] public bool Ok { get; set; }
    [Parameter("string", "color", 3, false)] public string? Color { get; set; }
    [Parameter("string", "status", 4, false)] public string? Status { get; set; }
    [Parameter("int256", "prediction", 5, false)] public BigInteger Prediction { get; set; }
    [Parameter("string", "reason", 6, false)] public string? Reason { get; set; }
    [Parameter("uint256", "timestamp", 7, false)] public BigInteger Timestamp { get; set; }
}

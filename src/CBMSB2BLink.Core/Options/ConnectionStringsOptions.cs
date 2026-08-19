using System.ComponentModel.DataAnnotations;

namespace CBMSB2BLink.Core.Options;

public sealed class ConnectionStringsOptions
{
    public const string SectionName = "ConnectionStrings";

    [Required(AllowEmptyStrings = false, ErrorMessage = "ConnectionStrings:CcrisB2B is required.")]
    public string CcrisB2B { get; set; } = string.Empty;

    [Required(AllowEmptyStrings = false, ErrorMessage = "ConnectionStrings:Cbms is required.")]
    public string Cbms { get; set; } = string.Empty;
}

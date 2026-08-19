using System;

namespace CBMSB2BLink.Core.Models;

/// <summary>
/// One row as returned by CCRISB2B's usp_GetBCBNewData (source: tblRPT).
/// </summary>
public sealed class BcbRecord
{
    public long RowId { get; init; }
    public string IdNo { get; init; } = string.Empty;
    public DateTime CreateDate { get; init; }
    public decimal Amount { get; init; }
}

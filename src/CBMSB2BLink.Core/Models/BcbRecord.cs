using System;

namespace CBMSB2BLink.Core.Models;

/// <summary>
/// One row as returned by CCRISB2B's usp_GetBCBNewData (source: src_tblRetRpt /
/// src_tblCRARawReport, see sql/source_CCRISB2B_01.sql).
/// </summary>
public sealed class BcbRecord
{
    public long RowId { get; init; }
    public int BcbCmsNo { get; init; }
    public string? BcbIdNo1 { get; init; }
    public string? BcbIdNo2 { get; init; }
    public string? BcbName1 { get; init; }
    public string? BcbDob { get; init; }
    public string? BcbNationality { get; init; }
    public DateTime? BcbCreateDate { get; init; }
    public string? BcbLastUpdateBy { get; init; }
    public string? BcbEntKey { get; init; }
    public string? BcbRefNo { get; init; }
    public string? BcbScrScoredTxnCode { get; init; }
}
